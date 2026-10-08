// Navball: attitude indicator showing the ship's nose direction relative to
// the local horizon frame, with prograde/retrograde/maneuver/target markers.

import * as THREE from 'three';
import { bodyPos, bodyVel, BODY_BY_ID } from './universe.js';
import { clamp } from './util.js';

export class Navball {
  constructor(canvas, size = 180) {
    this.canvas = canvas;
    this.size = size;
    this.ctx = canvas.getContext('2d');
    const dpr = Math.min(window.devicePixelRatio, 2);
    canvas.width = size * dpr;
    canvas.height = size * dpr;
    this.dpr = dpr;
  }

  draw(sim) {
    const ctx = this.ctx;
    const S = this.size;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.clearRect(0, 0, S, S);
    const cx = S / 2, cy = S / 2;
    const R = S / 2 - 6;

    const V = sim.vessel;
    const primary = V.primary;
    const t = sim.ut;
    const rel = V.r.clone().sub(bodyPos(primary, t));
    const relv = V.v.clone().sub(bodyVel(primary, t));

    // local reference frame (world): up = radial, fwd = horizontal prograde
    const up = rel.lengthSq() > 1 ? rel.normalize() : new THREE.Vector3(0, 1, 0);
    const h = new THREE.Vector3().crossVectors(rel, relv);
    let fwd;
    if (h.lengthSq() > 1e-6) fwd = new THREE.Vector3().crossVectors(h.normalize(), up).normalize();
    else fwd = new THREE.Vector3(1, 0, 0);
    const right = new THREE.Vector3().crossVectors(up, fwd).normalize();
    // M columns = [right, up, fwd]; local coords = M^T * world
    const M = new THREE.Matrix4().makeBasis(right, up, fwd);
    const toLocal = (w) => w.clone().applyMatrix4(new THREE.Matrix4().copy(M).transpose());

    const nose = V.nose();
    const nLocal = toLocal(nose);
    if (nLocal.lengthSq() < 1e-12) return;
    nLocal.normalize();

    // Q maps local -> dial space so that the nose is at dial +Z (centre)
    const zA = nLocal.clone();
    const upL = new THREE.Vector3(0, 1, 0);
    let yA = upL.clone().sub(zA.clone().multiplyScalar(upL.dot(zA)));
    if (yA.lengthSq() < 1e-9) yA = new THREE.Vector3(1, 0, 0);
    yA.normalize();
    const xA = new THREE.Vector3().crossVectors(yA, zA).normalize();
    const project = (dLocal) => {
      // dial coords
      const x = dLocal.dot(xA), y = dLocal.dot(yA), z = dLocal.dot(zA);
      if (z < 0.02) return null;
      let px = x / z, py = -y / z;
      const len = Math.hypot(px, py);
      if (len > 3.2) return null;
      // clamp into the dial circle (gnomonic stretch at the rim)
      if (len > 1) { px /= len; py /= len; }
      return [cx + px * R, cy + py * R];
    };

    // dial background
    ctx.beginPath();
    ctx.arc(cx, cy, R, 0, Math.PI * 2);
    ctx.fillStyle = '#0b1220';
    ctx.fill();
    ctx.strokeStyle = 'rgba(150,180,220,0.8)';
    ctx.lineWidth = 2;
    ctx.stroke();

    // rim ticks
    ctx.strokeStyle = 'rgba(150,180,220,0.6)';
    ctx.lineWidth = 1;
    for (let a = 0; a < 360; a += 10) {
      const rad = (a - 90) * Math.PI / 180;
      const r1 = R - (a % 30 === 0 ? 9 : 5);
      ctx.beginPath();
      ctx.moveTo(cx + Math.cos(rad) * r1, cy + Math.sin(rad) * r1);
      ctx.lineTo(cx + Math.cos(rad) * (R - 1), cy + Math.sin(rad) * (R - 1));
      ctx.stroke();
    }

    // latitude / longitude grid
    const latLon = (latDeg, lonDeg) => {
      const la = latDeg * Math.PI / 180, lo = lonDeg * Math.PI / 180;
      return new THREE.Vector3(
        Math.cos(la) * Math.cos(lo),
        Math.sin(la),
        Math.cos(la) * Math.sin(lo)
      );
    };
    ctx.lineWidth = 0.8;
    ctx.strokeStyle = 'rgba(140,200,255,0.28)';
    for (let lat = -80; lat <= 80; lat += 10) {
      ctx.beginPath();
      let started = false;
      for (let lon = 0; lon <= 360; lon += 4) {
        const p = project(latLon(lat, lon));
        if (!p) { started = false; continue; }
        if (!started) { ctx.moveTo(p[0], p[1]); started = true; }
        else ctx.lineTo(p[0], p[1]);
      }
      ctx.stroke();
    }
    for (let lon = 0; lon < 360; lon += 15) {
      ctx.beginPath();
      let started = false;
      for (let lat = -90; lat <= 90; lat += 4) {
        const p = project(latLon(lat, lon));
        if (!p) { started = false; continue; }
        if (!started) { ctx.moveTo(p[0], p[1]); started = true; }
        else ctx.lineTo(p[0], p[1]);
      }
      ctx.stroke();
    }
    // horizon
    ctx.strokeStyle = 'rgba(255,255,255,0.85)';
    ctx.lineWidth = 1.6;
    ctx.beginPath();
    let started = false;
    for (let lon = 0; lon <= 360; lon += 3) {
      const p = project(latLon(0, lon));
      if (!p) { started = false; continue; }
      if (!started) { ctx.moveTo(p[0], p[1]); started = true; }
      else ctx.lineTo(p[0], p[1]);
    }
    ctx.stroke();

    // markers
    const marker = (dirWorld, draw, size = 7) => {
      const p = project(toLocal(dirWorld));
      if (!p) return;
      ctx.save();
      ctx.translate(p[0], p[1]);
      draw(ctx, size);
      ctx.restore();
    };
    const prograde = relv.lengthSq() > 1 ? relv.clone().normalize() : fwd;
    const circle = (col) => (c, s) => {
      c.strokeStyle = col; c.lineWidth = 1.6;
      c.beginPath(); c.arc(0, 0, s, 0, Math.PI * 2); c.stroke();
      c.beginPath(); c.moveTo(-s, 0); c.lineTo(s, 0);
      c.moveTo(0, -s); c.lineTo(0, s); c.stroke();
    };
    marker(prograde, circle('#ffd54f'));
    marker(prograde.clone().negate(), circle('#ffd54f'));
    // radial in/out
    marker(up, (c, s) => {
      c.strokeStyle = '#4fc3f7'; c.lineWidth = 1.4;
      c.beginPath(); c.arc(0, 0, s * 0.7, 0, Math.PI * 2); c.stroke();
      c.beginPath(); c.moveTo(0, s); c.lineTo(0, s * 0.3); c.stroke();
    }, 6);
    marker(up.clone().negate(), (c, s) => {
      c.strokeStyle = '#4fc3f7'; c.lineWidth = 1.4;
      c.beginPath(); c.arc(0, 0, s * 0.7, 0, Math.PI * 2); c.stroke();
      c.beginPath(); c.moveTo(0, -s); c.lineTo(0, -s * 0.3); c.stroke();
    }, 6);
    // maneuver node
    if (V.sasMode === 'maneuver' && V.burnNode && V.burnNode.inertialDir) {
      marker(V.burnNode.inertialDir, (c, s) => {
        c.fillStyle = '#26c6da';
        c.beginPath();
        c.moveTo(0, -s); c.lineTo(s, 0); c.lineTo(0, s); c.lineTo(-s, 0);
        c.closePath(); c.fill();
      }, 7);
    }
    // target
    if (sim.targetId) {
      let tp = null;
      if (sim.targetId === 'sat1' && sim.scene3d) tp = bodyPos(sim.scene3d.satBody, t);
      else if (BODY_BY_ID[sim.targetId]) tp = bodyPos(BODY_BY_ID[sim.targetId], t);
      if (tp) {
        const dir = tp.clone().sub(V.r);
        if (dir.lengthSq() > 1) {
          marker(dir, (c, s) => {
            c.strokeStyle = '#7cf2ff'; c.lineWidth = 1.6;
            c.beginPath(); c.arc(0, 0, s, 0, Math.PI * 2); c.stroke();
            c.beginPath(); c.moveTo(-s * 0.6, -s * 0.6); c.lineTo(s * 0.6, s * 0.6);
            c.moveTo(s * 0.6, -s * 0.6); c.lineTo(-s * 0.6, s * 0.6); c.stroke();
          }, 6);
        }
      }
    }

    // centre crosshair (ship nose)
    ctx.strokeStyle = 'rgba(255,255,255,0.9)';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(cx - 8, cy); ctx.lineTo(cx - 3, cy);
    ctx.moveTo(cx + 3, cy); ctx.lineTo(cx + 8, cy);
    ctx.moveTo(cx, cy - 8); ctx.lineTo(cx, cy - 3);
    ctx.moveTo(cx, cy + 3); ctx.lineTo(cx, cy + 8);
    ctx.stroke();
    ctx.beginPath(); ctx.arc(cx, cy, 2, 0, Math.PI * 2); ctx.fillStyle = '#fff'; ctx.fill();

    // mode label
    ctx.fillStyle = 'rgba(200,215,235,0.9)';
    ctx.font = '10px monospace';
    ctx.textAlign = 'center';
    ctx.fillText(V.sasMode === 'off' ? '' : V.sasMode.toUpperCase(), cx, cy + R - 12);
    ctx.textAlign = 'left';
  }
}
