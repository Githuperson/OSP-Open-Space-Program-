// The OSP solar system ("Solara" system) + hierarchical-Kepler ephemeris.
//
// Frames: system inertial frame, origin at the star. The XZ plane is the
// ecliptic; +Y is north. All positions/velocities are in metres / metres per
// second. Bodies move on Keplerian orbits about their parent (patched
// conics, like KSP), which keeps the system stable at any time warp.

import * as THREE from 'three';
import { DEG, TAU, clamp, mod } from './util.js';

export const DAY = 86400;

// Keplerian elements of a body relative to its parent.
//   a     semi-major axis [m]
//   e     eccentricity
//   inc   inclination [deg, relative to ecliptic]
//   raan  right ascension of ascending node [deg]
//   argp  argument of periapsis [deg]
//   m0    mean anomaly at UT=0 [deg]
// The orbital period is derived from a and the parent's mu (self-consistent).

function body(def) {
  return {
    atmo: null, ocean: false, rings: false, cloudTex: null, nightTex: null,
    tint: 0xffffff, axialTilt: 0, color: 0xffffff, ...def,
  };
}

export const BODIES = [
  body({
    id: 'solara', name: 'Solara', type: 'star', parent: null,
    mu: 1.1723e18, radius: 2.616e8, rotPeriod: 32 * DAY,
    tex: 'textures/sun.jpg', color: 0xffd27f,
  }),
  body({
    id: 'cinder', name: 'Cinder', parent: 'solara',
    a: 5.0e9, e: 0.10, inc: 7, raan: 15, argp: 40, m0: 40,
    mu: 1.7e11, radius: 2.5e5, rotPeriod: 30 * DAY, axialTilt: 2,
    tex: 'textures/mercury.jpg', tint: 0xffa070, color: 0xff8855,
  }),
  body({
    id: 'kerra', name: 'Kerra', parent: 'solara',
    a: 1.36e10, e: 0.005, inc: 0, raan: 0, argp: 0, m0: 0,
    mu: 3.5316e12, radius: 6.0e5, rotPeriod: 21600, axialTilt: 0,
    tex: 'textures/earth_day.jpg', nightTex: 'textures/earth_night.jpg',
    cloudTex: 'textures/earth_clouds.png',
    atmo: { rho0: 1.225, H: 5000, top: 70e3 }, ocean: true, color: 0x3388dd,
  }),
  body({
    id: 'munara', name: 'Munara', parent: 'kerra',
    a: 1.2e7, e: 0.02, inc: 5, raan: 0, argp: 90, m0: 120,
    mu: 6.5e11, radius: 2.0e5, rotPeriod: 1.5 * DAY, axialTilt: 6,
    tex: 'textures/moon.jpg', color: 0xbbbbbb,
  }),
  body({
    id: 'violet', name: 'Violet', parent: 'solara',
    a: 2.2e10, e: 0.03, inc: 2.5, raan: 80, argp: 120, m0: 200,
    mu: 3.0e12, radius: 5.5e5, rotPeriod: 30 * DAY, axialTilt: 177,
    tex: 'textures/venus.jpg', tint: 0xcc99ee,
    atmo: { rho0: 2.0, H: 6000, top: 90e3 }, color: 0x9966cc,
  }),
  body({
    id: 'fasius', name: 'Fasius', parent: 'solara',
    a: 4.2e10, e: 0.05, inc: 1.2, raan: 140, argp: 200, m0: 310,
    mu: 8.0e13, radius: 3.0e6, rotPeriod: 36000, axialTilt: 3,
    tex: 'textures/jupiter.jpg',
    atmo: { rho0: 1.5, H: 12000, top: 300e3 }, color: 0xcc9966,
  }),
  body({
    id: 'oceana', name: 'Oceana', parent: 'fasius',
    a: 2.7e7, e: 0.01, inc: 2, raan: 40, argp: 0, m0: 60,
    mu: 2.8e12, radius: 5.0e5, rotPeriod: 1.2 * DAY, axialTilt: 8,
    tex: 'textures/earth_day.jpg', tint: 0x88bbff,
    atmo: { rho0: 0.8, H: 5500, top: 60e3 }, ocean: true, color: 0x2266cc,
  }),
  body({
    id: 'grayne', name: 'Grayne', parent: 'fasius',
    a: 6.8e7, e: 0.10, inc: 0.5, raan: 100, argp: 30, m0: 200,
    mu: 3.5e12, radius: 6.0e5, rotPeriod: 3 * DAY, axialTilt: 4,
    tex: 'textures/moon.jpg', tint: 0x999999, color: 0x999999,
  }),
  body({
    id: 'tarn', name: 'Tarn', parent: 'fasius',
    a: 9.0e7, e: 0.15, inc: 6, raan: 160, argp: 60, m0: 280,
    mu: 8.0e11, radius: 3.0e5, rotPeriod: 4 * DAY, axialTilt: 10,
    tex: 'textures/uranus.jpg', tint: 0xaaddff, color: 0xaaddff,
  }),
  body({
    id: 'aurelia', name: 'Aurelia', parent: 'solara',
    a: 7.5e10, e: 0.04, inc: 0.8, raan: 200, argp: 90, m0: 90,
    mu: 5.0e13, radius: 2.5e6, rotPeriod: 38000, axialTilt: 27,
    tex: 'textures/saturn.jpg', rings: true,
    atmo: { rho0: 1.2, H: 14000, top: 250e3 }, color: 0xddbb88,
  }),
  body({
    id: 'nerune', name: 'Nerune', parent: 'solara',
    a: 1.1e11, e: 0.06, inc: 1.5, raan: 260, argp: 150, m0: 150,
    mu: 2.0e13, radius: 1.2e6, rotPeriod: 40000, axialTilt: 28,
    tex: 'textures/neptune.jpg',
    atmo: { rho0: 1.0, H: 11000, top: 200e3 }, color: 0x4477dd,
  }),
  body({
    id: 'urelle', name: 'Urelle', parent: 'solara',
    a: 1.5e11, e: 0.03, inc: 0.5, raan: 310, argp: 30, m0: 260,
    mu: 1.8e13, radius: 1.1e6, rotPeriod: 34000, axialTilt: 98,
    tex: 'textures/uranus.jpg',
    atmo: { rho0: 0.9, H: 10000, top: 150e3 }, color: 0x99ddff,
  }),
];

export const BODY_BY_ID = {};
for (const b of BODIES) BODY_BY_ID[b.id] = b;

// ---- derived constants -------------------------------------------------

for (const b of BODIES) {
  if (!b.parent) {
    b.period = Infinity;
    b.rsoi = 1e13;
    continue;
  }
  const p = BODY_BY_ID[b.parent];
  b.period = TAU * Math.sqrt((b.a ** 3) / p.mu);
  b.rsoi = b.a * Math.pow(b.mu / p.mu, 0.4);
  b.g0 = b.mu / (b.radius * b.radius);
}

export const STAR = BODIES[0];
export const HOME = BODY_BY_ID.kerra;

// Launch site on Kerra (lat/lon in degrees, planet-fixed).
export const LAUNCH_SITE = { lat: 0, lon: -0.08, alt: 65 };

// ---- Kepler helpers (elements -> position/velocity) --------------------

function solveKeplerE(M, e) {
  let E = M + e * Math.sin(M);
  for (let i = 0; i < 60; i++) {
    const f = E - e * Math.sin(E) - M;
    const fp = 1 - e * Math.cos(E);
    const d = f / fp;
    E -= d;
    if (Math.abs(d) < 1e-12) break;
  }
  return E;
}

// Rotation from perifocal to inertial (ecliptic) frame.
// Y-up convention (ecliptic = XZ plane, +Y = north), matching conics.js
function perifocalToInertial(raan, inc, argp) {
  const m = new THREE.Matrix4();
  m.makeRotationY(raan * DEG);
  const m2 = new THREE.Matrix4().makeRotationX(inc * DEG);
  const m3 = new THREE.Matrix4().makeRotationX(-90 * DEG);
  const m4 = new THREE.Matrix4().makeRotationZ(argp * DEG);
  return m.multiply(m2).multiply(m3).multiply(m4);
}

const _R = new THREE.Matrix4();

// Position of a body's centre in the system frame at time t [s].
export function bodyPos(b, t) {
  if (!b.parent) return new THREE.Vector3(0, 0, 0);
  const parent = BODY_BY_ID[b.parent];
  const M = mod((b.m0 * DEG) + TAU * (t / b.period), TAU);
  const E = solveKeplerE(M, b.e);
  const cosE = Math.cos(E), sinE = Math.sin(E);
  const fac = Math.sqrt(1 - b.e * b.e);
  const x = b.a * (cosE - b.e);
  const y = b.a * fac * sinE;
  _R.copy(perifocalToInertial(b.raan, b.inc, b.argp));
  const local = new THREE.Vector3(x, y, 0).applyMatrix4(_R);
  return local.add(bodyPos(parent, t));
}

// Velocity of a body's centre in the system frame at time t [s].
export function bodyVel(b, t) {
  if (!b.parent) return new THREE.Vector3(0, 0, 0);
  const parent = BODY_BY_ID[b.parent];
  const M = mod((b.m0 * DEG) + TAU * (t / b.period), TAU);
  const E = solveKeplerE(M, b.e);
  const cosE = Math.cos(E), sinE = Math.sin(E);
  const r = b.a * (1 - b.e * cosE);
  const n = Math.sqrt(parent.mu / (b.a ** 3)); // mean motion
  const fac = Math.sqrt(1 - b.e * b.e);
  const vx = -b.a * n * sinE / (1 - b.e * cosE) * 1; // perifocal
  const vy = b.a * n * fac * cosE / (1 - b.e * cosE);
  _R.copy(perifocalToInertial(b.raan, b.inc, b.argp));
  const local = new THREE.Vector3(vx, vy, 0).applyMatrix4(_R);
  return local.add(bodyVel(parent, t));
}

// Planet-fixed surface direction (unit) for a lat/lon at time t.
export function surfaceDir(b, lat, lon, t) {
  const phi = lat * DEG;
  const lam = lon * DEG + TAU * (t / b.rotPeriod);
  return new THREE.Vector3(
    Math.cos(phi) * Math.cos(lam),
    Math.sin(phi),
    Math.cos(phi) * Math.sin(lam)
  );
}

export function surfacePos(b, lat, lon, alt, t) {
  return surfaceDir(b, lat, lon, t)
    .multiplyScalar(b.radius + alt)
    .add(bodyPos(b, t));
}

// Surface (rotation) velocity at a surface point.
export function surfaceVel(b, lat, lon, alt, t) {
  const w = TAU / b.rotPeriod;
  const r = surfaceDir(b, lat, lon, t).multiplyScalar(b.radius + alt);
  const east = new THREE.Vector3(0, 1, 0).cross(r).normalize();
  return east.multiplyScalar(w * (b.radius + alt)).add(bodyVel(b, t));
}

// ---- spheres of influence ----------------------------------------------

export function childrenOf(id) {
  return BODIES.filter((b) => b.parent === id);
}

export function parentOf(id) {
  const b = BODY_BY_ID[id];
  return b && b.parent ? BODY_BY_ID[b.parent] : null;
}

// Decide which body's sphere of influence a vessel at system position r is
// in, given its current primary (patched-conics rules, like KSP):
//  - inside a child's SOI   -> child (a planet's moon, or the star's planet)
//  - moon outside its SOI   -> back to its parent planet
//  - planet outside its SOI -> back to the star
// Small hysteresis bands (0.98 / 1.02) prevent flicker at the boundary.
export function primaryFor(id, r, t) {
  const cur = BODY_BY_ID[id];
  if (!cur) return STAR;
  if (cur.type === 'star') {
    let best = null, bestD = Infinity;
    for (const c of childrenOf(cur.id)) {
      const d = r.distanceTo(bodyPos(c, t));
      if (d < c.rsoi * 0.98 && d < bestD) { best = c; bestD = d; }
    }
    return best || cur;
  }
  if (cur.parent) {
    const parent = BODY_BY_ID[cur.parent];
    if (parent.type === 'star') {
      // planet: enter a moon's SOI, or leave for the star
      let best = null, bestD = Infinity;
      for (const c of childrenOf(cur.id)) {
        const d = r.distanceTo(bodyPos(c, t));
        if (d < c.rsoi * 0.98 && d < bestD) { best = c; bestD = d; }
      }
      if (best) return best;
      if (r.distanceTo(bodyPos(cur, t)) > cur.rsoi * 1.02) return parent;
      return cur;
    }
    // moon: leave SOI -> parent planet
    if (r.distanceTo(bodyPos(cur, t)) > cur.rsoi * 1.02) return parent;
    return cur;
  }
  return cur;
}

// Atmospheric density [kg/m^3] at altitude above a body's surface.
export function atmoDensity(b, alt) {
  if (!b.atmo || alt >= b.atmo.top || alt < 0) return 0;
  return b.atmo.rho0 * Math.exp(-alt / b.atmo.H);
}

export function surfaceGravity(b) {
  return b.g0;
}

export function circularOrbitVel(b, alt) {
  return Math.sqrt(b.mu / (b.radius + alt));
}

export { clamp };
