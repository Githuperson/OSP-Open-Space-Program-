// Orbit planner test: verify the Lambert transfer solver and porkchop scan
// using the real Planner class (headless — no DOM construction).
// Run: node test/planner.mjs

import * as THREE from 'three';
import { createSim } from '../src/sim.js';
import { BODY_BY_ID, HOME, STAR, DAY, bodyPos, bodyVel } from '../src/universe.js';
import { Planner } from '../src/planner.js';
import { clamp } from '../src/util.js';

let failures = 0;
const check = (name, cond, info = '') => {
  if (cond) console.log(`  ok  ${name}`);
  else { failures++; console.log(`  FAIL ${name} ${info}`); }
};

// build a planner bound to a sim without touching the DOM
const sim = createSim();
const planner = Object.create(Planner.prototype);
planner.sim = sim;

// put the vessel into an 80 km circular equatorial Kerra orbit
const V = sim.vessel;
const kerra = HOME;
const r0 = kerra.radius + 80e3;
const v0 = Math.sqrt(kerra.mu / r0);
V.landed = false;
V.primary = kerra;
V.r.copy(bodyPos(kerra, 0)).add(new THREE.Vector3(r0, 0, 0));
V.v.copy(bodyVel(kerra, 0)).add(new THREE.Vector3(0, 0, -v0)); // east = -z at +x
sim.ut = 0;

console.log('--- Kerra -> Munara (moon transfer, Kerra frame) ---');
const munara = BODY_BY_ID.munara;
// Munara's period is ~1.6 d; a Hohmann-like transfer takes ~0.8 d.
// A single arbitrary departure window can be expensive - scan departure
// times and require the best window to be cheap (like the porkchop does).
let bestMun = null;
for (let k = 0; k < 48; k++) {
  const td = sim.ut + k * (munara.period / 48);
  const r = planner.computeTransfer(td, 0.8 * DAY, munara);
  if (r && (!bestMun || r.dvTotal < bestMun.dvTotal)) bestMun = r;
}
const res = bestMun;
check('transfer solution found', !!res);
if (res) {
  console.log(`  best window: departure dv=${res.dvDep.toFixed(0)} m/s, arrival dv=${res.dvArr.toFixed(0)} m/s, total=${res.dvTotal.toFixed(0)} m/s`);
  check('total dv plausible (< 2000 m/s)', res.dvTotal < 2000, `${res.dvTotal.toFixed(0)}`);
  check('departure dv plausible (300-1200 m/s)', res.dvDep > 300 && res.dvDep < 1200, `${res.dvDep.toFixed(0)}`);
  // verify arrival: propagate the post-burn state to td+tof, compare to Munara
  const dep = planner._depState(res.td, res.frame);
  const p = (await import('../src/conics.js')).keplerProp(dep.r, res.sol.v1, res.tof, kerra.mu);
  const munaraRel = bodyPos(munara, res.td + res.tof).sub(bodyPos(kerra, res.td + res.tof));
  const err = p.r.distanceTo(munaraRel);
  check('arrival position error < 1 km', err < 1e3, `${(err / 1e3).toFixed(2)} km`);
}

console.log('--- Kerra -> Violet (interplanetary, star frame) ---');
const violet = BODY_BY_ID.violet;
const res2 = planner.computeTransfer(sim.ut, 60 * DAY, violet);
check('transfer solution found', !!res2);
if (res2) {
  console.log(`  departure dv=${res2.dvDep.toFixed(0)} m/s, arrival dv=${res2.dvArr.toFixed(0)} m/s, total=${res2.dvTotal.toFixed(0)} m/s`);
  check('total dv plausible (< 8000 m/s)', res2.dvTotal < 8000, `${res2.dvTotal.toFixed(0)}`);
  check('departure dv plausible (1000-6000 m/s)', res2.dvDep > 1000 && res2.dvDep < 6000, `${res2.dvDep.toFixed(0)}`);
}

console.log('--- porkchop scan (Kerra -> Munara) ---');
// replicate the porkchop grid computation with the planner's own methods
const target = munara;
const frame = planner._frame(target);
const srcPeriod = kerra.period;
const tgtPeriod = munara.period;
const synodic = 1 / Math.abs(1 / srcPeriod - 1 / tgtPeriod);
const depWindow = clamp(1.3 * synodic, 20 * DAY, 1200 * DAY);
const tofMin = clamp(0.15 * synodic, 6 * 3600, 5 * DAY);
const tofMax = clamp(0.9 * synodic, 1 * DAY, 900 * DAY);
const ND = 24, NT = 16;
let best = null;
let nSolutions = 0;
for (let i = 0; i < ND; i++) {
  const td = sim.ut + (i + 0.5) * (depWindow / ND);
  for (let j = 0; j < NT; j++) {
    const tof = tofMin + (j + 0.5) * ((tofMax - tofMin) / NT);
    const r = planner.computeTransfer(td, tof, target);
    if (!r) continue;
    nSolutions++;
    if (!best || r.dvTotal < best.dvTotal) best = { td, tof, dvTotal: r.dvTotal };
  }
}
console.log(`  ${nSolutions}/${ND * NT} windows solved, best total=${best ? best.dvTotal.toFixed(0) : 'n/a'} m/s`);
check('most windows solvable (> 80%)', nSolutions > 0.8 * ND * NT, `${nSolutions}/${ND * NT}`);
check('best window found', !!best);
if (best) {
  check('best window plausible (< 2000 m/s)', best.dvTotal < 2000, `${best.dvTotal.toFixed(0)}`);
  check('best window departure within scan', best.td > sim.ut && best.td < sim.ut + depWindow);
  check('best window tof within scan', best.tof >= tofMin && best.tof <= tofMax);
}

console.log('--- maneuver node delta-v components round trip ---');
const { dvComponents, componentsToDv } = await import('../src/conics.js');
const rel = V.r.clone().sub(bodyPos(kerra, sim.ut));
const relv = V.v.clone().sub(bodyVel(kerra, sim.ut));
const comp = dvComponents(new THREE.Vector3(300, -150, 80), rel, relv);
const dv = componentsToDv(comp, rel, relv);
const err = dv.distanceTo(new THREE.Vector3(300, -150, 80));
check('dv components round-trip < 1e-6 m/s', err < 1e-6, `${err.toExponential(2)}`);

console.log(failures === 0 ? '\nPLANNER TEST PASSED' : `\n${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
