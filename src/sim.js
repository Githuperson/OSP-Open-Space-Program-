// Simulation core: integrates the vessel under patched-conics physics
// (gravity of the current primary + thrust + atmospheric drag), handles
// staging, maneuver-node burns, landing/crash/liftoff, debris and time warp.

import * as THREE from 'three';
import {
  BODY_BY_ID, STAR, HOME, LAUNCH_SITE,
  bodyPos, bodyVel, surfacePos, surfaceVel, surfaceDir,
  primaryFor, atmoDensity, childrenOf,
} from './universe.js';
import { rvToOE, keplerProp, componentsToDv, orbitBasis } from './conics.js';
import { Vessel, DRAG_CD, DRAG_A, STAGE_HEIGHTS } from './vessel.js';
import { clamp, TAU } from './util.js';

export const WARPS = [1, 5, 10, 50, 100, 1000, 10000];
export const SAS_MODES = ['off', 'stability', 'prograde', 'retrograde', 'radial', 'antiradial', 'normal', 'antinormal', 'maneuver', 'target', 'antitarget'];

const RW_TORQUE = 120e3;    // reaction-wheel torque limit [N m]
const INPUT_TORQUE = 1.0;    // fraction of RW limit for manual input

export const PAD_TOP = 2.5; // launch pad surface height above the ground [m]

export function createSim() {
  const vessel = new Vessel();
  const sim = {
    ut: 0,
    warpIndex: 0,
    paused: false,
    vessel,
    debris: [],
    messages: [],
    input: { pitch: 0, yaw: 0, roll: 0 },
    warpToNodeUt: null,
    targetId: null,
    time: 0, // accumulated real seconds (for animations)
  };
  placeOnPad(sim);
  return sim;
}

export function placeOnPad(sim) {
  const V = sim.vessel;
  V.reset();
  const b = HOME;
  V.landed = true;
  V.landedBody = b;
  V.landedLat = LAUNCH_SITE.lat;
  V.landedLon = LAUNCH_SITE.lon;
  V.landedOnPad = true;
  V.primary = b;
  V.targetId = null;
  sim.ut = 0;
  sim.warpIndex = 0;
  sim.debris = [];
  sim.messages = [];
  sim.warpToNodeUt = null;
  sim._lastStageIndex = 0;
  updateLandedState(sim);
  const up = surfaceDir(b, V.landedLat, V.landedLon, sim.ut);
  V.setNose(up);
  V.throttle = 0;
  V.sasMode = 'stability';
  pushMessage(sim, 'OSP-1 on the pad. Full throttle (Z), stage (Space).');
}

function updateLandedState(sim) {
  const V = sim.vessel;
  if (!V.landed || !V.landedBody) return;
  const b = V.landedBody;
  V.primary = b;
  const alt = (V.landedOnPad ? PAD_TOP : 0.2) + V.stackHeight;
  V.r.copy(surfacePos(b, V.landedLat, V.landedLon, alt, sim.ut));
  V.v.copy(surfaceVel(b, V.landedLat, V.landedLon, alt, sim.ut));
}

export function pushMessage(sim, text, ttl = 5) {
  sim.messages.push({ text, t: sim.time, ttl });
  if (sim.messages.length > 12) sim.messages.shift();
}

// ---------------------------------------------------------------------------
// Main step. realDt in seconds (wall clock).
export function step(sim, realDt) {
  sim.time += realDt;
  if (sim.paused || sim.vessel.crashed) return;
  realDt = Math.min(realDt, 0.1);
  const V = sim.vessel;

  // warp-to-node helper
  if (sim.warpToNodeUt != null) {
    const node = V.nextNode();
    if (!node || node.ut !== sim.warpToNodeUt) sim.warpToNodeUt = null;
    else if (sim.ut >= node.ut - 60) { sim.warpToNodeUt = null; sim.warpIndex = 0; }
    else sim.warpIndex = WARPS.length - 1;
  }

  // landed vessel: only check liftoff, let time run
  if (V.landed) {
    updateLandedState(sim);
    sim.ut += WARPS[sim.warpIndex] * realDt;
    updateLandedState(sim);
    if (V.throttle > 0.02 && V.thrust() > V.mass * V.landedBody.g0 * 1.05 && !V.crashed) {
      V.landed = false;
      pushMessage(sim, 'Liftoff!');
    }
    updateDebris(sim, WARPS[sim.warpIndex] * realDt);
    return;
  }

  const simDt = WARPS[sim.warpIndex] * realDt;

  // adaptive substep
  const primary = V.primary;
  const rel0 = V.r.clone().sub(bodyPos(primary, sim.ut));
  const rn0 = Math.max(rel0.length(), primary.radius * 1.0001);
  const tOrbit = TAU * Math.sqrt((rn0 ** 3) / primary.mu);
  let dt = clamp(tOrbit / 60, 0.005, 5);
  dt = Math.min(dt, simDt / 4);
  dt = Math.max(dt, simDt / 2000);
  const nSub = Math.max(1, Math.ceil(simDt / dt));
  const h = simDt / nSub;

  for (let i = 0; i < nSub; i++) {
    physicsStep(sim, h);
    if (V.crashed || V.landed) break;
  }

  // spawn debris for stages jettisoned since the last step
  if (V.stageIndex !== sim._lastStageIndex) {
    for (let s = sim._lastStageIndex; s < V.stageIndex; s++) {
      const st = V.stages[s];
      if (!st || !st.jettisoned) continue;
      // debris starts where the jettisoned stage was: at the current vessel
      // position, offset down by the remaining stack below the separation point
      const primaryNow = V.primary;
      const up = V.r.clone().sub(bodyPos(primaryNow, sim.ut)).normalize();
      const below = (() => {
        let hgt = 0;
        for (let i = s + 1; i < V.stages.length; i++) {
          if (!V.stages[i].jettisoned) hgt += STAGE_HEIGHTS[i] ?? 5;
        }
        return hgt;
      })();
      const sepR = V.r.clone().sub(up.clone().multiplyScalar(below + 1));
      // small random separation velocity
      const kick = new THREE.Vector3((Math.random() - 0.5) * 4, (Math.random() - 0.5) * 4, (Math.random() - 0.5) * 4);
      const sepV = V.v.clone().add(kick);
      const d = spawnDebris(sim, sepR, sepV, primaryNow, sim.debrisFactory || null);
      d.stageIndex = s;
      d.name = st.name;
    }
    sim._lastStageIndex = V.stageIndex;
  }

  // auto-drop warp when thrusting or low in an atmosphere
  const altNow = altitudeOf(sim);
  if ((V.throttle > 0 && V.thrust() > 0) || V.burnNode || (altNow != null && altNow < 1000)) {
    if (sim.warpIndex > 0) sim.warpIndex = 0;
  }

  updateDebris(sim, simDt);

  // consume one-shot events
  if (V.eventStaged) { pushMessage(sim, `Stage separated: ${V.eventStaged}`); V.eventStaged = null; }
  if (V.eventFlameout) { pushMessage(sim, `Engine flameout: ${V.eventFlameout}`); V.eventFlameout = null; }
}

export function altitudeOf(sim) {
  const V = sim.vessel;
  if (!V.primary) return null;
  const rel = V.r.clone().sub(bodyPos(V.primary, sim.ut));
  return rel.length() - V.primary.radius;
}

export function relativeState(sim) {
  const V = sim.vessel;
  const primary = V.primary;
  const rel = V.r.clone().sub(bodyPos(primary, sim.ut));
  const relv = V.v.clone().sub(bodyVel(primary, sim.ut));
  return { primary, rel, relv };
}

// ---------------------------------------------------------------------------

function physicsStep(sim, dt) {
  const V = sim.vessel;
  const primary = V.primary;
  const ut = sim.ut;

  let rel = V.r.clone().sub(bodyPos(primary, ut));
  let relv = V.v.clone().sub(bodyVel(primary, ut));
  const rn = Math.max(rel.length(), 1);
  const alt = rn - primary.radius;

  // velocity relative to the rotating atmosphere (drag & aero act on this)
  const wAt = TAU / primary.rotPeriod;
  let eastA = new THREE.Vector3(0, 1, 0).cross(rel);
  if (eastA.lengthSq() < 1e-9) eastA.set(1, 0, 0);
  eastA.normalize();
  const vAir = relv.clone().sub(eastA.multiplyScalar(wAt * rn));

  const mass = Math.max(V.mass, 1);
  V.inertia = Math.max(200, mass * 0.64);

  // maneuver node burn control
  const node = V.nextNode();
  if (node && sim.ut >= node.ut - 1e-9 && !V.burnNode) {
    V.burnNode = node;
    V.prevThrottle = V.throttle;
    V.throttle = 1;
    V.sasMode = 'maneuver';
    node.dvDone = 0;
    node.dvMag = Math.hypot(node.dvp, node.dvr, node.dvn);
    const dv = componentsToDv({ prograde: node.dvp, radial: node.dvr, normal: node.dvn }, rel, relv);
    node.inertialDir = dv.lengthSq() > 1e-12 ? dv.normalize() : null;
    pushMessage(sim, 'Maneuver node: burning...');
  }

  // thrust
  const T = V.thrust();
  const aThrustMag = T / mass;
  const aThrust = T > 0 ? V.nose().multiplyScalar(aThrustMag) : new THREE.Vector3();
  if (T > 0 && dt > 0) V.burnFuel(dt);

  // accumulate node burn delta-v
  if (V.burnNode) {
    V.burnNode.dvDone += aThrustMag * dt;
    if (V.burnNode.dvDone >= V.burnNode.dvMag - 1e-6) {
      V.burnNode.applied = true;
      pushMessage(sim, `Maneuver complete (+${V.burnNode.dvMag.toFixed(1)} m/s)`);
      V.burnNode = null;
      V.throttle = V.prevThrottle;
      V.sasMode = 'stability';
    }
  }

  V.inAtmo = false;

  const accel = (rRel, vRel) => {
    const rnl = Math.max(rRel.length(), 1);
    const a = rRel.clone().multiplyScalar(-primary.mu / (rnl ** 3));
    if (T > 0) a.add(aThrust);
    if (primary.atmo) {
      const a2 = rnl - primary.radius;
      if (a2 < primary.atmo.top) {
        const rho = atmoDensity(primary, Math.max(a2, 0));
        const sp = vAir.length();
        if (sp > 0.01 && rho > 0) {
          const q = 0.5 * rho * sp * sp;
          if (q > V.maxQ) V.maxQ = q;
          V.inAtmo = true;
          a.add(vAir.clone().multiplyScalar(-(q * DRAG_CD * DRAG_A) / (mass * sp)));
        }
      }
    }
    return a;
  };

  // velocity Verlet
  const a1 = accel(rel, relv);
  const rNew = rel.clone()
    .add(relv.clone().multiplyScalar(dt))
    .add(a1.clone().multiplyScalar(0.5 * dt * dt));
  const vGuess = relv.clone().add(a1.clone().multiplyScalar(dt));
  const a2 = accel(rNew, vGuess);
  const vNew = relv.clone().add(a1.clone().add(a2).multiplyScalar(0.5 * dt));

  const utEnd = ut + dt;
  V.r.copy(rNew).add(bodyPos(primary, utEnd));
  V.v.copy(vNew).add(bodyVel(primary, utEnd));
  sim.ut = utEnd;

  updateAttitude(sim, dt, alt, rel, relv, vAir);

  // SOI transition
  const np = primaryFor(primary.id, V.r, sim.ut);
  if (np.id !== primary.id) V.primary = np;

  // landing / crash (trigger when the base of the stack reaches the surface)
  const newPrimary = V.primary;
  const relE = V.r.clone().sub(bodyPos(newPrimary, sim.ut));
  const relvE = V.v.clone().sub(bodyVel(newPrimary, sim.ut));
  const altE = relE.length() - newPrimary.radius - V.stackHeight;
  if (altE <= -0.05) {
    if (newPrimary.type === 'star') {
      V.crashed = true;
      pushMessage(sim, `Vessel fell into ${newPrimary.name}...`);
      return;
    }
    const up = relE.clone().normalize();
    // impact velocity relative to the rotating surface (not the planet center)
    const w = TAU / newPrimary.rotPeriod;
    const east = new THREE.Vector3(0, 1, 0).cross(up);
    if (east.lengthSq() < 1e-9) east.set(1, 0, 0);
    east.normalize();
    const vSurf = relvE.clone().sub(east.multiplyScalar(w * newPrimary.radius));
    const vUp = vSurf.dot(up);
    const speed = vSurf.length();
    if (speed < 12 && vUp > -4) {
      V.landed = true;
      V.landedOnPad = false;
      V.landedBody = newPrimary;
      V.qHold = null;
      const lat = Math.asin(clamp(up.y, -1, 1)) * 180 / Math.PI;
      const lon = (Math.atan2(up.z, up.x) - TAU * (sim.ut / newPrimary.rotPeriod)) * 180 / Math.PI;
      V.landedLat = lat;
      V.landedLon = ((lon % 360) + 360) % 360 - 180;
      V.throttle = 0;
      V.sasMode = 'off';
      V.burnNode = null;
      V.omega.set(0, 0, 0);
      updateLandedState(sim);
      pushMessage(sim, `Landed on ${newPrimary.name}.`);
    } else {
      V.crashed = true;
      V.throttle = 0;
      pushMessage(sim, `Crash! Impact speed ${speed.toFixed(1)} m/s.`);
    }
  }
}

function updateAttitude(sim, dt, alt, rel, relv, vAir) {
  const V = sim.vessel;
  const I = Math.max(V.inertia, 1);
  const tau = new THREE.Vector3();
  const nose = V.nose();

  // SAS
  if (V.sasMode !== 'off') {
    let targetDir = null;
    const speed = relv.length();
    const rHat = rel.clone().normalize();
    const h = new THREE.Vector3().crossVectors(rel, relv);
    const hHat = h.lengthSq() > 1e-12 ? h.normalize() : new THREE.Vector3(0, 1, 0);
    switch (V.sasMode) {
      case 'prograde': if (speed > 0.01) targetDir = relv.clone().normalize(); break;
      case 'retrograde': if (speed > 0.01) targetDir = relv.clone().negate().normalize(); break;
      case 'radial': targetDir = rHat; break;
      case 'antiradial': targetDir = rHat.negate(); break;
      case 'normal': targetDir = hHat; break;
      case 'antinormal': targetDir = hHat.negate(); break;
      case 'maneuver': if (V.burnNode && V.burnNode.inertialDir) targetDir = V.burnNode.inertialDir; break;
      case 'target': {
        const tb = sim.targetId ? BODY_BY_ID[sim.targetId] : null;
        if (tb) targetDir = bodyPos(tb, sim.ut).sub(V.r).normalize();
        break;
      }
      case 'antitarget': {
        const tb = sim.targetId ? BODY_BY_ID[sim.targetId] : null;
        if (tb) targetDir = V.r.clone().sub(bodyPos(tb, sim.ut)).normalize();
        break;
      }
      default: break;
    }
    if (targetDir) {
      // PD: proportional on the alignment error axis, plus angular-rate damping
      const err = new THREE.Vector3().crossVectors(nose, targetDir);
      tau.add(err.multiplyScalar(80e3));
      tau.add(V.omega.clone().multiplyScalar(-20e3));
    } else if (V.sasMode === 'stability') {
      // hold the attitude the vessel had when SAS was engaged (or last trimmed)
      if (V._sasPrev !== 'stability') V.qHold = null;
      if (!V.qHold) V.qHold = V.q.clone();
      // world-frame error rotation that takes the current attitude to the hold
      const qe = V.qHold.clone().multiply(V.q.clone().invert());
      if (qe.w < 0) { qe.x = -qe.x; qe.y = -qe.y; qe.z = -qe.z; qe.w = -qe.w; }
      const ang = 2 * Math.acos(clamp(qe.w, -1, 1));
      const err = new THREE.Vector3(qe.x, qe.y, qe.z).multiplyScalar(ang);
      tau.add(err.multiplyScalar(80e3));
      tau.add(V.omega.clone().multiplyScalar(-20e3));
    } else {
      tau.add(V.omega.clone().multiplyScalar(-20e3));
    }
  }

  // manual control input (body-frame axes: right / up / nose)
  const inp = sim.input;
  if (inp.pitch || inp.yaw || inp.roll) {
    const worldUp = new THREE.Vector3(0, 1, 0);
    let right = new THREE.Vector3().crossVectors(worldUp, nose);
    if (right.lengthSq() < 1e-6) right.set(1, 0, 0);
    right.normalize();
    const upv = new THREE.Vector3().crossVectors(nose, right).normalize();
    if (inp.pitch) tau.add(right.clone().multiplyScalar(-inp.pitch * RW_TORQUE * INPUT_TORQUE));
    if (inp.yaw) tau.add(upv.clone().multiplyScalar(inp.yaw * RW_TORQUE * INPUT_TORQUE));
    if (inp.roll) tau.add(nose.clone().multiplyScalar(-inp.roll * RW_TORQUE * INPUT_TORQUE));
    // while steering in stability mode, re-trim the hold attitude
    if (V.sasMode === 'stability') V.qHold = V.q.clone();
  }
  V._sasPrev = V.sasMode;

  // aerodynamic torque: weathervane + damping (acts on the airflow velocity)
  if (V.inAtmo && vAir.length() > 5 && alt < (V.primary.atmo ? V.primary.atmo.top : 0)) {
    const rho = atmoDensity(V.primary, Math.max(alt, 0));
    const q = 0.5 * rho * vAir.lengthSq();
    const vHat = vAir.clone().normalize();
    const axis = new THREE.Vector3().crossVectors(nose, vHat);
    const sinA = Math.min(axis.length(), 1);
    if (sinA > 1e-6) {
      axis.normalize();
      // weathervane: aligns the nose with the airflow (weak enough that the
      // pilot / SAS can still steer)
      tau.add(axis.multiplyScalar(q * DRAG_A * 6.0 * sinA * 0.01));
      tau.add(V.omega.clone().multiplyScalar(-q * DRAG_A * 3.6 * 0.05));
    }
  }

  // clamp to reaction wheel authority
  const tl = tau.length();
  if (tl > RW_TORQUE) tau.multiplyScalar(RW_TORQUE / tl);

  V.omega.add(tau.multiplyScalar(dt / I));

  // integrate quaternion (q' = 1/2 * omega_quat * q)
  const w = V.omega;
  const hx = w.x * dt / 2, hy = w.y * dt / 2, hz = w.z * dt / 2;
  const q = V.q;
  const nx = q.x + hx * q.w + hy * q.z - hz * q.y;
  const ny = q.y - hx * q.z + hy * q.w + hz * q.x;
  const nz = q.z + hx * q.y - hy * q.x + hz * q.w;
  const nw = q.w - hx * q.x - hy * q.y - hz * q.z;
  V.q.set(nx, ny, nz, nw).normalize();
}

// ---------------------------------------------------------------------------

function updateDebris(sim, dt) {
  if (dt <= 0) return;
  for (const d of sim.debris) {
    d.age += dt;
    if (d.age > d.ttl) { d.dead = true; continue; }
    // simple two-body drift about the primary it was spawned near
    const p = keplerProp(d.rel, d.relV, dt, d.mu);
    d.rel = p.r; d.relV = p.v;
    d.r.copy(d.rel).add(bodyPos(d.body, sim.ut));
    d.v.copy(d.relV).add(bodyVel(d.body, sim.ut));
    d.rot.x += d.angVel.x * dt;
    d.rot.y += d.angVel.y * dt;
    d.rot.z += d.angVel.z * dt;
    if (d.rel.length() < d.body.radius) d.dead = true;
  }
  sim.debris = sim.debris.filter((d) => !d.dead);
}

export function spawnDebris(sim, worldR, worldV, primary, makeObject) {
  const rel = worldR.clone().sub(bodyPos(primary, sim.ut));
  const relv = worldV.clone().sub(bodyVel(primary, sim.ut));
  const d = {
    rel, relV: relv, body: primary, mu: primary.mu,
    r: worldR.clone(), v: worldV.clone(),
    rot: new THREE.Vector3(Math.random(), Math.random(), Math.random()),
    angVel: new THREE.Vector3((Math.random() - 0.5) * 2, (Math.random() - 0.5) * 2, (Math.random() - 0.5) * 2),
    age: 0, ttl: 300, dead: false, obj: null,
  };
  sim.debris.push(d);
  if (makeObject) d.obj = makeObject(d);
  return d;
}

export { Vessel };
