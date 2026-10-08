// Vessel (rocket) definition and state: staging, fuel, delta-v, attitude.

import * as THREE from 'three';
import { clamp } from './util.js';

export const G0 = 9.80665;

// The stock "OSP-1" launch vehicle: a two-stage rocket + capsule.
// Stages are listed bottom -> top. Fuel is liquid fuel (LF).
export const STOCK_ROCKET = [
  {
    name: "Torrent-1 Booster",
    dryMass: 600,       // kg, tank structure
    fuel: 8000,         // kg, liquid fuel
    engine: { name: "G-25 'Torrent'", thrust: 240e3, isp: 270, mass: 800, gimbal: 6 },
    fins: true,
  },
  {
    name: "Puddle Upper Stage",
    dryMass: 350,
    fuel: 3200,
    engine: { name: "S-10 'Puddle'", thrust: 60e3, isp: 330, mass: 250, gimbal: 8 },
    fins: false,
  },
  {
    name: 'CS-2 Capsule',
    dryMass: 1200,
    fuel: 0,
    engine: null,
    fins: false,
    capsule: true,
  },
];

export const DRAG_CD = 0.5;   // drag coefficient (streamlined rocket)
export const DRAG_A = 8.0;    // reference area m^2

// structural height of each stage [m] (matches the model in rocket.js)
export const STAGE_HEIGHTS = [7.35, 5.7, 4.3];

// quaternion that points the model nose (local +Y) along `dir`
export function noseQuat(dir) {
  const n = dir.clone().normalize();
  const up = Math.abs(n.y) > 0.99 ? new THREE.Vector3(1, 0, 0) : new THREE.Vector3(0, 1, 0);
  // lookAt matrix maps local +Z -> n
  const m = new THREE.Matrix4().lookAt(new THREE.Vector3(0, 0, 0), n.clone().negate(), up);
  const q = new THREE.Quaternion().setFromRotationMatrix(m);
  // the model nose is local +Y; rotate +90 deg about X so +Y -> +Z first
  const fix = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), Math.PI / 2);
  return q.multiply(fix).normalize();
}

export function stageMass(stage, fuelLeft = stage.fuel) {
  return stage.dryMass + fuelLeft + (stage.engine ? stage.engine.mass : 0);
}

// Total delta-v available from stage `i` upward (rocket equation).
export function deltaVFrom(stages, stageIndex) {
  let dv = 0;
  let mAbove = 0;
  for (let i = stages.length - 1; i >= stageIndex; i--) mAbove += stageMass(stages[i]);
  for (let i = stageIndex; i < stages.length; i++) {
    const st = stages[i];
    if (!st.engine || st.fuel <= 0) continue;
    const ve = st.engine.isp * G0;
    const m0 = mAbove;
    const m1 = mAbove - st.fuel;
    if (m1 > 0) dv += ve * Math.log(m0 / m1);
    mAbove = m1 - st.dryMass - (st.engine ? st.engine.mass : 0);
    if (mAbove < 0) mAbove = 0;
  }
  return dv;
}

export function totalDryMass(stages) {
  let m = 0;
  for (const st of stages) m += st.dryMass + (st.engine ? st.engine.mass : 0);
  return m;
}

// ---------------------------------------------------------------------------

export class Vessel {
  constructor(stages = STOCK_ROCKET) {
    // deep-ish copy so restarts reset fuel
    this.stages = stages.map((st) => ({
      ...st,
      engine: st.engine ? { ...st.engine } : null,
      fuelLeft: st.fuel,
      activated: false,
      jettisoned: false,
    }));
    this.stageIndex = 0;      // lowest intact stage
    this.stages[this.stages.length - 1].activated = true; // capsule "active"
    // current stage's engine burns only once staged (or the booster at launch)
    this.stages[0].activated = true;

    this.r = new THREE.Vector3();
    this.v = new THREE.Vector3();
    this.q = new THREE.Quaternion();      // body -> world
    this.omega = new THREE.Vector3();    // angular velocity, world frame [rad/s]
    this.throttle = 0;
    this.sasMode = 'off';   // off | stability | prograde | retrograde | radial | antiradial | normal | antinormal | maneuver | target | antitarget
    this.sasTarget = null;
    this.rcs = false;
    this.landed = false;
    this.landedBody = null;
    this.landedLat = 0;
    this.landedLon = 0;
    this.crashed = false;
    this.primary = null;    // body object
    this.nodes = [];        // maneuver nodes
    this.targetId = null;
    this.maxQ = 0;
    this.inAtmo = false;
    this.lastThrottle = 0;
    this.burnNode = null;   // node currently being burned
    this.prevThrottle = 0;
    this.gearLights = true;
    this.inertia = 1;
    this.qHold = null;      // attitude reference for SAS stability mode
    this._sasPrev = 'off';
  }

  get mass() {
    let m = 0;
    for (let i = this.stageIndex; i < this.stages.length; i++) {
      if (this.stages[i].jettisoned) continue;
      m += stageMass(this.stages[i], this.stages[i].fuelLeft);
    }
    return m;
  }

  get dryMass() {
    let m = 0;
    for (let i = this.stageIndex; i < this.stages.length; i++) {
      if (this.stages[i].jettisoned) continue;
      m += this.stages[i].dryMass + (this.stages[i].engine ? this.stages[i].engine.mass : 0);
    }
    return m;
  }

  // height of the intact stack below the nose tip [m]
  get stackHeight() {
    let h = 0;
    for (let i = this.stageIndex; i < this.stages.length; i++) {
      if (this.stages[i].jettisoned) continue;
      h += STAGE_HEIGHTS[i] ?? 5;
    }
    return h;
  }

  get deltaV() {
    return deltaVFrom(this.stages, this.stageIndex);
  }

  // active engines (lowest intact stage with an activated engine that has fuel)
  activeEngines() {
    const out = [];
    for (let i = this.stageIndex; i < this.stages.length; i++) {
      const st = this.stages[i];
      if (st.jettisoned || !st.engine) continue;
      if (!st.activated) continue;
      if (st.fuelLeft <= 0) continue;
      out.push({ stage: i, ...st.engine });
    }
    return out;
  }

  // total thrust [N] at current throttle (0 if engines starved)
  thrust() {
    let t = 0;
    for (const e of this.activeEngines()) t += e.thrust;
    return t * clamp(this.throttle, 0, 1);
  }

  // burn fuel for dt seconds at current throttle; returns mass flow used
  burnFuel(dt) {
    let mdot = 0;
    const th = clamp(this.throttle, 0, 1);
    for (const e of this.activeEngines()) {
      const ve = e.isp * G0;
      const md = (e.thrust * th) / ve;
      const st = this.stages[e.stage];
      const used = Math.min(md * dt, st.fuelLeft);
      st.fuelLeft -= used;
      mdot += used / dt;
      if (st.fuelLeft <= 0) {
        st.fuelLeft = 0;
        st.activated = false; // flameout
        this.eventFlameout = st.name;
      }
    }
    return mdot;
  }

  hasFuel() {
    return this.activeEngines().length > 0;
  }

  // stage (jettison the lowest intact stage, activate the next)
  stage() {
    if (this.landed) return false;
    if (this.stageIndex >= this.stages.length - 1) {
      // only the capsule left: jettison nothing more
      if (this.stageIndex === this.stages.length - 1) return false;
    }
    const st = this.stages[this.stageIndex];
    if (st.jettisoned) { this.stageIndex++; return true; }
    st.jettisoned = true;
    this.stageIndex++;
    const next = this.stages[this.stageIndex];
    if (next && next.engine) next.activated = true;
    this.eventStaged = st.name;
    return true;
  }

  nose() {
    return new THREE.Vector3(0, 1, 0).applyQuaternion(this.q);
  }

  setNose(dir) {
    this.q.copy(noseQuat(dir)).normalize();
    this.omega.set(0, 0, 0);
    this.qHold = null;
  }

  addNode(ut, dvp = 0, dvr = 0, dvn = 0) {
    const node = { ut, dvp, dvr, dvn, applied: false, id: Math.random().toString(36).slice(2, 8) };
    this.nodes.push(node);
    this.nodes.sort((a, b) => a.ut - b.ut);
    return node;
  }

  removeNode(id) {
    this.nodes = this.nodes.filter((n) => n.id !== id);
  }

  nextNode() {
    return this.nodes.find((n) => !n.applied) || null;
  }

  reset(stages = STOCK_ROCKET) {
    const fresh = new Vessel(stages);
    Object.assign(this, {
      stages: fresh.stages, stageIndex: fresh.stageIndex,
      r: new THREE.Vector3(), v: new THREE.Vector3(),
      q: new THREE.Quaternion(), omega: new THREE.Vector3(),
      throttle: 0, sasMode: 'off', landed: false, crashed: false,
      nodes: [], targetId: null, maxQ: 0, burnNode: null,
    });
    this.stages[this.stages.length - 1].activated = true;
    this.stages[0].activated = true;
  }
}
