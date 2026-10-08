// Physics smoke tests (run: npm test)
import * as THREE from 'three';
import {
  BODIES, BODY_BY_ID, STAR, HOME, bodyPos, bodyVel, primaryFor,
  childrenOf, circularOrbitVel,
} from '../src/universe.js';
import { rvToOE, keplerProp, orbitBasis, componentsToDv, dvComponents } from '../src/conics.js';
import { lambert } from '../src/lambert.js';
import { predictTrajectory } from '../src/propagate.js';
import { Vessel, deltaVFrom, STOCK_ROCKET } from '../src/vessel.js';
import { TAU } from '../src/util.js';

let failures = 0;
function check(name, cond, info = '') {
  if (cond) console.log(`  ok  ${name}`);
  else { failures++; console.log(`  FAIL ${name} ${info}`); }
}
const approx = (a, b, tol = 1e-6) => Math.abs(a - b) <= tol * Math.max(1, Math.abs(a), Math.abs(b));

console.log('== ephemeris ==');
for (const b of BODIES) {
  if (!b.parent) continue;
  const p = BODY_BY_ID[b.parent];
  // position relative to the parent at t=0 and t=period should match
  const r0 = bodyPos(b, 0).sub(bodyPos(p, 0));
  const r1 = bodyPos(b, b.period).sub(bodyPos(p, b.period));
  check(`${b.id} closed orbit`, r0.distanceTo(r1) < 1e-3 * b.a, `${r0.distanceTo(r1)} vs a=${b.a}`);
  // speed consistency: v^2 = mu(2/r - 1/a)
  const v = bodyVel(b, b.period * 0.37);
  const r = bodyPos(b, b.period * 0.37);
  const rParent = bodyPos(p, b.period * 0.37);
  const relR = r.clone().sub(rParent);
  const relV = v.clone().sub(bodyVel(p, b.period * 0.37));
  const expect = p.mu * (2 / relR.length() - 1 / b.a);
  check(`${b.id} vis-viva`, approx(relV.lengthSq(), expect, 1e-6), `${relV.lengthSq()} vs ${expect}`);
}

console.log('== keplerProp ==');
{
  // circular LKO around Kerra
  const mu = HOME.mu, R = HOME.radius + 100e3;
  const v = circularOrbitVel(HOME, 100e3);
  const r0 = new THREE.Vector3(R, 0, 0);
  const v0 = new THREE.Vector3(0, 0, v);
  const T = TAU * Math.sqrt(R ** 3 / mu);
  const p = keplerProp(r0, v0, T, mu);
  check('circular orbit returns', p.r.distanceTo(r0) < 1, `dr=${p.r.distanceTo(r0)}`);
  check('circular speed kept', approx(p.v.length(), v, 1e-4));
  // energy conservation on an eccentric orbit
  const v0e = new THREE.Vector3(0, 0, v * 1.3);
  const oe = rvToOE(r0, v0e, mu);
  let r = r0.clone(), vv = v0e.clone();
  const h0 = new THREE.Vector3().crossVectors(r, vv).length();
  const e0 = (vv.lengthSq() / 2) - mu / r.length();
  for (let i = 0; i < 200; i++) { const q = keplerProp(r, vv, T / 200, mu); r = q.r; vv = q.v; }
  const h1 = new THREE.Vector3().crossVectors(r, vv).length();
  const e1 = (vv.lengthSq() / 2) - mu / r.length();
  check('eccentric: angular momentum conserved', approx(h0, h1, 1e-6), `${h0} vs ${h1}`);
  check('eccentric: energy conserved', approx(e0, e1, 1e-6), `${e0} vs ${e1}`);
  // hyperbolic
  const vEsc = new THREE.Vector3(0, 0, v * 2.5);
  const ph = keplerProp(r0, vEsc, 1e5, mu);
  const oeH = rvToOE(ph.r, ph.v, mu);
  check('hyperbolic stays hyperbolic', oeH.e > 1, `e=${oeH.e}`);
}

console.log('== elements round trip ==');
{
  const mu = HOME.mu;
  const r0 = new THREE.Vector3(7e5, 1e5, -2e5);
  const v0 = new THREE.Vector3(-500, 2200, 800);
  const oe = rvToOE(r0, v0, mu);
  check('a matches', approx(oe.a, -mu / (2 * oe.energy)));
  check('periapsis = p/(1+e)', approx(oe.periapsis, oe.p / (1 + oe.e)));
  const b = orbitBasis(r0, v0);
  const dv = new THREE.Vector3(100, -50, 25);
  const comp = dvComponents(dv, r0, v0);
  const dv2 = componentsToDv({ prograde: comp.prograde, radial: comp.radial, normal: comp.normal }, r0, v0);
  check('dv components round trip', dv.distanceTo(dv2) < 1e-6, `${dv.distanceTo(dv2)}`);
}

console.log('== lambert ==');
{
  const mu = STAR.mu;
  // transfer from Kerra's orbit to Violet's orbit positions
  const t0 = 1e6, tof = 60 * 86400;
  const r1 = bodyPos(BODY_BY_ID.kerra, t0);
  const r2 = bodyPos(BODY_BY_ID.violet, t0 + tof);
  const sol = lambert(r1, r2, tof, mu);
  check('lambert solved', !!sol);
  if (sol) {
    const p = keplerProp(r1, sol.v1, tof, mu);
    check('lambert arrival', p.r.distanceTo(r2) < 1e-3 * r2.length(), `dr=${p.r.distanceTo(r2)} m`);
    check('lambert final velocity', p.v.distanceTo(sol.v2) < 1e-3 * sol.v2.length(), `dv=${p.v.distanceTo(sol.v2)} m/s`);
    // long way should differ
    const solL = lambert(r1, r2, tof, mu, { shortWay: false });
    check('long way differs', solL && solL.v1.distanceTo(sol.v1) > 1);
    check('long way arrives', solL && keplerProp(r1, solL.v1, tof, mu).r.distanceTo(r2) < 1e-3 * r2.length());
  }
  // Munar transfer (moon of Kerra)
  const muK = HOME.mu;
  const r1m = new THREE.Vector3(6.7e5, 0, 0);
  const vCirc = circularOrbitVel(HOME, 100e3);
  const target = bodyPos(BODY_BY_ID.munara, 0).sub(bodyPos(HOME, 0));
  const solM = lambert(r1m, target, 1.2 * 86400, muK);
  check('moon lambert solved', !!solM);
  if (solM) {
    const p = keplerProp(r1m, solM.v1, 1.2 * 86400, muK);
    check('moon lambert arrival', p.r.distanceTo(target) < 1e-3 * target.length(), `dr=${p.r.distanceTo(target)}`);
  }
}

console.log('== SOI / primary ==');
{
  const t = 2e6;
  const kerraPos = bodyPos(HOME, t);
  const p1 = primaryFor('solara', kerraPos.clone().add(new THREE.Vector3(1e7, 0, 0)), t);
  check('inside Kerra SOI -> kerra', p1.id === 'kerra', p1.id);
  const p2 = primaryFor('kerra', new THREE.Vector3(3e9, 0, 0), t);
  check('outside Kerra SOI -> solara', p2.id === 'solara', p2.id);
  const munPos = bodyPos(BODY_BY_ID.munara, t);
  const p3 = primaryFor('kerra', munPos.clone().add(new THREE.Vector3(1e6, 0, 0)), t);
  check('inside Munara SOI -> munara', p3.id === 'munara', p3.id);
  const p4 = primaryFor('munara', munPos.clone().add(new THREE.Vector3(2e7, 0, 0)), t);
  check('outside Munara SOI -> kerra', p4.id === 'kerra', p4.id);
}

console.log('== predictTrajectory ==');
{
  const t = 3e6;
  const kerraPos = bodyPos(HOME, t);
  const kerraVel = bodyVel(HOME, t);
  // LKO prograde
  const R = HOME.radius + 100e3;
  const v = circularOrbitVel(HOME, 100e3);
  const r = kerraPos.clone().add(new THREE.Vector3(R, 0, 0));
  const vel = kerraVel.clone().add(new THREE.Vector3(0, 0, v));
  const pred = predictTrajectory({ r, v: vel, ut: t }, { primary: 'kerra', maxTime: 2 * TAU * Math.sqrt(R ** 3 / HOME.mu) });
  check('orbit preview has segments', pred.segments.length >= 1);
  const pts = pred.segments[0].points;
  check('orbit preview samples', pts.length > 100, `${pts.length}`);
  const rFirst = pts[0].r, rLast = pts[pts.length - 1].r;
  check('orbit preview stays near Kerra', rFirst.distanceTo(kerraPos) < HOME.rsoi);
  check('no false impact', !pred.impact);
  // deorbit: kill most of the velocity -> should impact Kerra
  const vLow = kerraVel.clone().add(new THREE.Vector3(0, 0, v * 0.55));
  const pred2 = predictTrajectory({ r, v: vLow, ut: t }, { primary: 'kerra' });
  check('deorbit impacts', !!pred2.impact, JSON.stringify(pred2.impact && pred2.impact.body));
  // Munar transfer with a node
  const nodeUt = t + 3600;
  const pred3 = predictTrajectory({ r, v: vel, ut: t }, {
    primary: 'kerra',
    nodes: [{ ut: nodeUt, dvp: 900, dvr: 0, dvn: 0, applied: false }],
  });
  check('node preview escapes Kerra SOI', pred3.segments.length > 1, `segments=${pred3.segments.length}`);
  check('node preview applied node', pred3.appliedThrough === 0);
}

console.log('== vessel ==');
{
  const V = new Vessel();
  const m0 = V.mass;
  check('liftoff mass', approx(m0, 14400, 1e-6), `${m0}`);
  const dv = V.deltaV;
  check('delta-v ~5.4 km/s', dv > 5000 && dv < 6000, `${dv.toFixed(0)}`);
  V.throttle = 1;
  const T = V.thrust();
  check('booster thrust', approx(T, 240e3), `${T}`);
  check('TWR > 1', T / (m0 * HOME.g0) > 1.2, `${T / (m0 * HOME.g0)}`);
  // burn 10 s
  const mdot = V.burnFuel(10);
  check('fuel burned', V.mass < m0, `${V.mass}`);
  check('mdot ~ 88 kg/s', approx(mdot, 240e3 / (270 * 9.80665), 1e-3), `${mdot}`);
  // stage
  V.stage();
  check('staged to upper', V.stageIndex === 1);
  check('upper engine active', V.activeEngines().length === 1 && V.activeEngines()[0].name.includes('Puddle'));
  V.stage();
  check('staged to capsule', V.stageIndex === 2 && V.activeEngines().length === 0);
  const dvAfter = V.deltaV;
  check('dv after staging ~0', dvAfter < 1, `${dvAfter}`);
}

console.log(failures === 0 ? '\nALL TESTS PASSED' : `\n${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
