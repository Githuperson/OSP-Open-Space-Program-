// Lambert's problem solver (transfer orbit between two position vectors in a
// given time of flight). Universal-variable formulation with Stumpff
// functions and a safeguarded Newton iteration - solves single-revolution
// transfers, short way or long way.

import * as THREE from 'three';
import { stumpffC2, stumpffC3 } from './conics.js';
import { clamp } from './util.js';

// Solve Lambert: find v1 at r1 such that the orbit reaches r2 after `tof`
// seconds under gravity of mu. Returns {v1, v2, a, e} or null on failure.
export function lambert(r1, r2, tof, mu, opts = {}) {
  const shortWay = opts.shortWay !== false;
  const r1n = r1.length();
  const r2n = r2.length();
  if (r1n < 1e-6 || r2n < 1e-6 || tof <= 0) return null;

  const cVec = new THREE.Vector3().subVectors(r2, r1);
  const cn = cVec.length();
  let cosDnu = clamp(r1.dot(r2) / (r1n * r2n), -1, 1);
  // nudge away from the degenerate collinear cases
  if (cosDnu > 1 - 1e-9) cosDnu = 1 - 1e-9;
  if (cosDnu < -1 + 1e-9) cosDnu = -1 + 1e-9;

  // A encodes the transfer-angle geometry. Sign selects short (+) vs long (-)
  // way. (A = sin(dnu)*sqrt(r1 r2/(1 - cos dnu)) is an equivalent form.)
  const A = (shortWay ? 1 : -1) * Math.sqrt(r1n * r2n * (1 + cosDnu));

  const sqrtMu = Math.sqrt(mu);

  // F(z) = (y/c2)^1.5 * c3 + A*sqrt(y) - sqrt(mu)*tof = 0
  //   y(z) = r1n + r2n + A * (z*c3(z) - 1) / sqrt(c2(z))
  function yOf(z) {
    const c2 = stumpffC2(z);
    const c3 = stumpffC3(z);
    const sq = Math.sqrt(Math.max(c2, 1e-12));
    return r1n + r2n + (A * (z * c3 - 1)) / sq;
  }
  function F(z) {
    const c2 = stumpffC2(z);
    if (c2 <= 1e-12) return Infinity;
    const c3 = stumpffC3(z);
    const y = yOf(z);
    if (y <= 0) return -Infinity;
    return Math.pow(y / c2, 1.5) * c3 + A * Math.sqrt(y) - sqrtMu * tof;
  }

  // Bracket the root. z = alpha*chi^2; single-rev transfers live roughly in
  // (-4*pi^2, 4*pi^2). F is monotonic increasing in z for the relevant branch.
  let lo = -4 * Math.PI * Math.PI * 0.999;
  let hi = 4 * Math.PI * Math.PI * 0.999;
  let flo = F(lo), fhi = F(hi);
  if (!(flo < 0) || !(fhi > 0)) {
    // try to expand bracket a bit (multi-period transfers)
    for (let k = 0; k < 6 && (!(flo < 0) || !(fhi > 0)); k++) {
      if (!(flo < 0)) { lo -= 4 * Math.PI * Math.PI; flo = F(lo); }
      if (!(fhi > 0)) { hi += 4 * Math.PI * Math.PI; fhi = F(hi); }
    }
  }
  if (!(flo < 0) || !(fhi > 0)) return null; // no single-rev solution

  // Bisection + Newton (safeguarded)
  let z = 0.5 * (lo + hi);
  for (let i = 0; i < 80; i++) {
    const fz = F(z);
    if (fz < 0) lo = z; else hi = z;
    // numeric derivative
    const dz = Math.max(1e-8, Math.abs(z) * 1e-6);
    const dF = (F(z + dz) - F(z - dz)) / (2 * dz);
    let zn = Math.abs(dF) > 1e-12 ? z - fz / dF : 0.5 * (lo + hi);
    if (!(zn > lo && zn < hi)) zn = 0.5 * (lo + hi);
    if (Math.abs(zn - z) < 1e-10 * (1 + Math.abs(z))) { z = zn; break; }
    z = zn;
  }

  const c2 = stumpffC2(z);
  const y = yOf(z);
  if (y <= 0 || c2 <= 0) return null;

  const f = 1 - y / r1n;
  const g = A * Math.sqrt(y / mu);
  if (Math.abs(g) < 1e-12) return null;
  const gdot = 1 - y / r2n;

  const v1 = new THREE.Vector3().subVectors(r2, r1.clone().multiplyScalar(f)).divideScalar(g);
  const v2 = new THREE.Vector3().subVectors(r2.clone().multiplyScalar(gdot), r1).divideScalar(g);

  // elements of the transfer orbit (for display)
  const rn = r1n;
  const vn = v1.length();
  const energy = (vn * vn) / 2 - mu / rn;
  const a = -mu / (2 * energy);
  return { v1, v2, a, e: Math.sqrt(Math.max(0, 1 + (2 * energy * new THREE.Vector3().crossVectors(r1, v1).lengthSq()) / (mu * mu))) };
}
