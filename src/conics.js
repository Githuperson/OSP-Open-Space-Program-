// Two-body orbital mechanics: state vectors <-> orbital elements, universal
// variable Kepler propagation (handles ellipse / parabola / hyperbola),
// Stumpff functions and orbit sampling.

import * as THREE from 'three';
import { clamp, mod, TAU, DEG } from './util.js';

// ---- Stumpff functions ---------------------------------------------------

export function stumpffC2(z) {
  if (z > 1e-6) return (1 - Math.cos(Math.sqrt(z))) / z;
  if (z < -1e-6) return (Math.cosh(Math.sqrt(-z)) - 1) / -z;
  return 1 / 2 - z / 24 + (z * z) / 720;
}

export function stumpffC3(z) {
  if (z > 1e-6) {
    const s = Math.sqrt(z);
    return (s - Math.sin(s)) / (s * s * s);
  }
  if (z < -1e-6) {
    const s = Math.sqrt(-z);
    return (Math.sinh(s) - s) / (s * s * s);
  }
  return 1 / 6 - z / 120 + (z * z) / 5040;
}

// ---- state vectors -> orbital elements -----------------------------------

export function rvToOE(r, v, mu) {
  const rn = r.length();
  const vn = v.length();
  const h = new THREE.Vector3().crossVectors(r, v);
  const hn = h.length();
  const eVec = new THREE.Vector3().crossVectors(v, h).divideScalar(mu).sub(r.clone().normalize());
  const e = eVec.length();
  const energy = (vn * vn) / 2 - mu / rn;
  const a = Math.abs(energy) > 1e-12 ? -mu / (2 * energy) : Infinity;
  const p = (hn * hn) / mu;
  // Y-up: inclination is the angle between h and the north pole (+Y)
  const inc = hn > 0 ? Math.acos(clamp(h.y / hn, -1, 1)) : 0;

  const n = new THREE.Vector3(0, 1, 0).cross(h); // line of nodes (in the XZ plane)
  const nn = n.length();
  let raan = 0, argp = 0, ta = 0;
  const eHat = e > 1e-9 ? eVec.clone().divideScalar(e) : new THREE.Vector3(1, 0, 0);

  if (nn > 1e-9) {
    const nHat = n.clone().divideScalar(nn);
    // Ry(raan) maps +X to the node line: node = (cos raan, 0, -sin raan)
    raan = mod(Math.atan2(-n.z, n.x), TAU);
    argp = Math.acos(clamp(nHat.dot(eHat), -1, 1));
    if (eVec.y < 0) argp = TAU - argp;
    const rHat = r.clone().divideScalar(rn);
    ta = Math.acos(clamp(eHat.dot(rHat), -1, 1));
    if (r.dot(v) < 0) ta = TAU - ta;
  } else {
    // equatorial: use longitude of periapsis / position in the XZ plane
    raan = 0;
    argp = e > 1e-9 ? mod(Math.atan2(eVec.z, eVec.x), TAU) : 0;
    const rHat = r.clone().divideScalar(rn);
    if (e > 1e-9) {
      ta = Math.acos(clamp(eHat.dot(rHat), -1, 1));
      if (r.dot(v) < 0) ta = TAU - ta;
    } else {
      ta = mod(Math.atan2(r.z, r.x), TAU);
    }
  }

  return {
    a, e, inc, raan, argp, ta, p, energy,
    periapsis: a * (1 - e),
    apoapsis: a > 0 ? a * (1 + e) : Infinity,
    period: a > 0 ? TAU * Math.sqrt((a ** 3) / mu) : Infinity,
    h: hn,
    r: rn,
    v: vn,
  };
}

// ---- elements -> state vectors (perifocal -> inertial) -------------------

export function oeToRVPerifocal(oe, mu, ta) {
  const p = oe.p;
  const r = p / (1 + oe.e * Math.cos(ta));
  const rp = new THREE.Vector3(r * Math.cos(ta), r * Math.sin(ta), 0);
  const vp = new THREE.Vector3(
    -Math.sqrt(mu / p) * Math.sin(ta),
    Math.sqrt(mu / p) * (oe.e + Math.cos(ta)),
    0
  );
  return { r: rp, v: vp };
}

// Y-up convention (ecliptic = XZ plane, +Y = north), matching universe.js:
// perifocal x = periapsis, y = in-plane 90 deg ahead, z = orbit normal (h).
// For inc=0/raan=0/argp=0 the perifocal frame maps to (x, -z, y).
export function perifocalToInertialM(oe) {
  const m = new THREE.Matrix4();
  m.makeRotationY(oe.raan);
  m.multiply(new THREE.Matrix4().makeRotationX(oe.inc));
  m.multiply(new THREE.Matrix4().makeRotationX(-Math.PI / 2));
  m.multiply(new THREE.Matrix4().makeRotationZ(oe.argp));
  return m;
}

export function oeToRV(oe, mu, ta) {
  const { r, v } = oeToRVPerifocal(oe, mu, ta);
  const m = perifocalToInertialM(oe);
  return { r: r.applyMatrix4(m), v: v.applyMatrix4(m) };
}

// ---- universal variable Kepler propagation --------------------------------
// Vallado Algorithm 12. Propagates (r0, v0) by dt under gravity of mu.

export function keplerProp(r0, v0, dt, mu) {
  if (dt === 0) return { r: r0.clone(), v: v0.clone() };
  const sqrtMu = Math.sqrt(mu);
  const r0n = r0.length();
  const v0n2 = v0.lengthSq();
  const r0v0 = r0.dot(v0);
  const alpha = 2 / r0n - v0n2 / mu; // 1/a

  let chi;
  if (alpha > 1e-6) {
    chi = sqrtMu * dt * alpha;
  } else if (alpha < -1e-6) {
    const a = 1 / alpha;
    const sgn = Math.sign(dt);
    const denom = r0v0 + sgn * Math.sqrt(-mu * a) * (1 - r0n * alpha);
    chi = sgn * Math.sqrt(-a) * Math.log((-2 * mu * alpha * dt) / denom);
  } else {
    chi = (sqrtMu * dt) / r0n;
  }

  let c2 = 0, c3 = 0, z = 0, rn = r0n;
  for (let i = 0; i < 60; i++) {
    z = alpha * chi * chi;
    c2 = stumpffC2(z);
    c3 = stumpffC3(z);
    rn = chi * chi * c2 + (r0v0 / sqrtMu) * chi * (1 - z * c3) + r0n * (1 - z * c2);
    // universal Kepler equation:
    //   sqrt(mu)*dt = chi^3*c3 + (r0.v0/sqrt(mu))*chi^2*c2 + r0*chi*(1 - z*c3)
    const F = chi * chi * chi * c3 + (r0v0 / sqrtMu) * chi * chi * c2 + r0n * chi * (1 - z * c3);
    const dChi = (sqrtMu * dt - F) / rn;
    chi += dChi;
    if (Math.abs(dChi) < 1e-9 * (1 + Math.abs(chi))) break;
  }

  const f = 1 - (chi * chi / r0n) * c2;
  const g = dt - (chi * chi * chi / sqrtMu) * c3;
  const r = r0.clone().multiplyScalar(f).add(v0.clone().multiplyScalar(g));
  const rn2 = r.length();
  const fdot = (sqrtMu / (rn2 * r0n)) * chi * (z * c3 - 1);
  const gdot = 1 - (chi * chi / rn2) * c2;
  const v = r0.clone().multiplyScalar(fdot).add(v0.clone().multiplyScalar(gdot));
  return { r, v };
}

// ---- sampling -------------------------------------------------------------

// Sample a conic (given as r0,v0 in the parent frame) over a time span.
// Returns array of {t, r, v} in the parent-relative frame.
export function sampleConic(r0, v0, mu, t0, t1, maxPoints = 720) {
  const oe = rvToOE(r0, v0, mu);
  let dt = (t1 - t0) / maxPoints;
  if (oe.period !== Infinity && oe.period > 0) {
    dt = Math.min(dt, oe.period / 360);
  }
  const n = Math.max(2, Math.min(maxPoints * 4, Math.ceil((t1 - t0) / dt)));
  const out = [];
  let r = r0.clone(), v = v0.clone();
  const step = (t1 - t0) / n;
  out.push({ t: t0, r: r.clone(), v: v.clone() });
  for (let i = 1; i <= n; i++) {
    const p = keplerProp(r, v, step, mu);
    r = p.r; v = p.v;
    out.push({ t: t0 + i * step, r: r.clone(), v: v.clone() });
  }
  return out;
}

// True anomaly sweep of an orbit defined by elements (for drawing closed
// orbits as seen from the parent). Returns points in the parent frame.
export function sampleOrbitElements(a, e, inc, raan, argp, mu, count = 240, taStart = 0, taEnd = TAU) {
  const oe = { a, e, inc: inc * DEG, raan: raan * DEG, argp: argp * DEG, p: a * (1 - e * e) };
  const m = perifocalToInertialM(oe);
  const pts = [];
  for (let i = 0; i <= count; i++) {
    const ta = taStart + (taEnd - taStart) * (i / count);
    const { r } = oeToRVPerifocal(oe, mu, ta);
    pts.push(r.applyMatrix4(m));
  }
  return pts;
}

// Orbital basis at a state (prograde / radial / normal unit vectors).
export function orbitBasis(r, v) {
  const radial = r.clone().normalize();
  const h = new THREE.Vector3().crossVectors(r, v);
  const hn = h.length();
  const normal = hn > 1e-12 ? h.divideScalar(hn) : new THREE.Vector3(0, 1, 0);
  const prograde = new THREE.Vector3().crossVectors(normal, radial).normalize();
  return { radial, prograde, normal };
}

// Delta-v vector decomposed into prograde/radial/normal components.
export function dvComponents(dv, r, v) {
  const b = orbitBasis(r, v);
  return {
    prograde: dv.dot(b.prograde),
    radial: dv.dot(b.radial),
    normal: dv.dot(b.normal),
  };
}

export function componentsToDv(comp, r, v) {
  const b = orbitBasis(r, v);
  return b.prograde.clone().multiplyScalar(comp.prograde)
    .add(b.radial.clone().multiplyScalar(comp.radial))
    .add(b.normal.clone().multiplyScalar(comp.normal));
}

export { DEG, TAU };
