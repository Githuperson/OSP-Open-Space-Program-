// End-to-end flight test: a scripted autopilot launches the stock rocket from
// the pad, performs a gravity turn, stages, and circularizes into Kerra orbit.
// Run: node test/launch.mjs

import * as THREE from 'three';
import { createSim, step, altitudeOf, relativeState } from '../src/sim.js';
import { rvToOE } from '../src/conics.js';
import { noseQuat } from '../src/vessel.js';
import { clamp } from '../src/util.js';

const sim = createSim();
const V = sim.vessel;
console.log(`start: mass=${V.mass.toFixed(0)} kg, dv=${V.deltaV.toFixed(0)} m/s, on pad`);

// phases: 0 = powered gravity turn, 1 = coast to apoapsis, 2 = circularize, 3 = done
let phase = 0;
let maxAlt = 0;
let maxDebris = 0;
const dt = 1 / 60;
let simTime = 0;
const maxSimTime = 60 * 60;

while (simTime < maxSimTime) {
  const alt = altitudeOf(sim) ?? 0;
  maxAlt = Math.max(maxAlt, alt);
  maxDebris = Math.max(maxDebris, sim.debris.length);
  const { primary, rel, relv } = relativeState(sim);
  const oe = rvToOE(rel, relv, primary.mu);
  const altAp = oe.apoapsis - primary.radius;
  const altPe = oe.periapsis - primary.radius;

  if (V.crashed) break;

  if (!V.landed) {
    if (phase === 0) {
      V.throttle = 1;
      // gravity turn: blend from vertical toward the local east (prograde)
      const up = rel.clone().normalize();
      let east = new THREE.Vector3(0, 1, 0).cross(up);
      if (east.lengthSq() < 1e-9) east.set(1, 0, 0);
      east.normalize();
      const t = clamp(alt / 35e3, 0, 1);
      const dir = up.clone().multiplyScalar(1 - t).add(east.multiplyScalar(t)).normalize();
      V.sasMode = 'stability';
      V.qHold = noseQuat(dir);
      // stage when the active stage is out of fuel
      const st = V.stages[V.stageIndex];
      if (st && st.engine && st.fuelLeft <= 0.5 && V.stageIndex < V.stages.length - 1) {
        V.stage();
        console.log(`  [stage] t=${simTime.toFixed(0)}s alt=${(alt / 1e3).toFixed(1)} km -> stage ${V.stageIndex}`);
      }
      // cut the engine when the speed nears circular speed for the current
      // altitude (above the thick air) — the coast then has both apsides high
      const vCirc = Math.sqrt(primary.mu / rel.length());
      if (alt > 70e3 && relv.length() >= 0.985 * vCirc) phase = 1;
    } else if (phase === 1) {
      V.throttle = 0;
      V.sasMode = 'prograde';
      if (relv.dot(rel) < 300) phase = 2; // approaching apoapsis
    } else if (phase === 2) {
      V.throttle = 1;
      V.sasMode = 'prograde';
      if (altPe >= 75e3) { phase = 3; V.throttle = 0; }
    } else {
      V.throttle = 0;
      V.sasMode = 'prograde';
    }
  } else {
    V.throttle = 1; // liftoff
    V.sasMode = 'stability';
    V.qHold = noseQuat(relativeState(sim).rel.clone().normalize());
  }

  step(sim, dt);
  simTime += dt;
  if (phase === 3) break;
}

const { primary, rel, relv } = relativeState(sim);
const oe = rvToOE(rel, relv, primary.mu);
const alt = altitudeOf(sim);
console.log('---');
console.log(`sim time   : ${simTime.toFixed(0)} s (${(simTime / 60).toFixed(1)} min)`);
console.log(`crashed    : ${V.crashed}`);
console.log(`landed     : ${V.landed}`);
console.log(`altitude   : ${(alt / 1e3).toFixed(2)} km`);
console.log(`speed      : ${relv.length().toFixed(1)} m/s`);
console.log(`apoapsis   : ${((oe.apoapsis - primary.radius) / 1e3).toFixed(1)} km`);
console.log(`periapsis  : ${((oe.periapsis - primary.radius) / 1e3).toFixed(1)} km`);
console.log(`ecc        : ${oe.e.toFixed(4)}`);
console.log(`incl       : ${(oe.inc * 180 / Math.PI).toFixed(2)} deg`);
console.log(`maxQ seen  : ${(V.maxQ / 1e3).toFixed(1)} kPa`);
console.log(`mass left  : ${V.mass.toFixed(0)} kg`);
console.log(`debris     : ${sim.debris.length}`);

let failures = 0;
const check = (name, cond, info = '') => {
  if (cond) console.log(`  ok  ${name}`);
  else { failures++; console.log(`  FAIL ${name} ${info}`); }
};

console.log('---');
check('not crashed', !V.crashed);
check('reached space (max alt > 70 km)', maxAlt > 70e3, `maxAlt=${(maxAlt / 1e3).toFixed(1)} km`);
check('in orbit (not landed, alt > 60 km)', !V.landed && alt > 60e3, `alt=${(alt / 1e3).toFixed(1)} km`);
// note: simple open-loop gravity turn; a 75 x 160 km parking orbit is a pass
check('apoapsis 70-200 km', oe.apoapsis - primary.radius > 70e3 && oe.apoapsis - primary.radius < 200e3,
  `${((oe.apoapsis - primary.radius) / 1e3).toFixed(1)} km`);
check('periapsis 70-120 km', oe.periapsis - primary.radius > 70e3 && oe.periapsis - primary.radius < 120e3,
  `${((oe.periapsis - primary.radius) / 1e3).toFixed(1)} km`);
check('stable orbit (e < 0.12, Pe above atmosphere)', oe.e < 0.12, `e=${oe.e.toFixed(4)}`);
check('staged at least once', V.stageIndex >= 1, `stageIndex=${V.stageIndex}`);
check('debris from staging', maxDebris >= 1, `maxDebris=${maxDebris}`);

console.log(failures === 0 ? '\nLAUNCH TEST PASSED — stable orbit achieved' : `\n${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
