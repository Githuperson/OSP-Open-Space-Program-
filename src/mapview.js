// 2D map view (KSP-style): top-down view of the ecliptic plane showing planet
// orbits, SOIs, the vessel trajectory (patched-conic prediction), maneuver
// nodes, targets and closest approach. Pan/zoom/click-to-target.

import * as THREE from 'three';
import {
  BODIES, BODY_BY_ID, STAR, HOME, bodyPos, bodyVel, childrenOf, parentOf,
} from './universe.js';
import { sampleOrbitElements, rvToOE, oeToRV, keplerProp } from './conics.js';
import { closestApproach } from './propagate.js';
import { fmtDist, fmtTimeShort, TAU, clamp } from './util.js';

const BG = '#070b14';
const GRID = 'rgba(120,160,220,0.10)';
const ORBIT = 'rgba(140,180,230,0.35)';
const SOI = 'rgba(120,200,160,0.22)';
const TRAJ = '#58b6ff';
const TRAJ_NODE = '#ffa726';
const TRANSFER = '#e060ff';

export class MapView {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.cx = 0;      // centre, system frame X [m]
    this.cz = 0;      // centre, system frame Z [m]
    this.scale = 1e7; // metres per pixel
    this.follow = true;
    this.orbitCache = {};
    this.dragging = false;
    this._dragMoved = false;
    this._attachInput();
  }

  resize() {
    const dpr = Math.min(window.devicePixelRatio, 2);
    const w = this.canvas.clientWidth, h = this.canvas.clientHeight;
    if (this.canvas.width !== Math.round(w * dpr) || this.canvas.height !== Math.round(h * dpr)) {
      this.canvas.width = Math.round(w * dpr);
      this.canvas.height = Math.round(h * dpr);
    }
    this.dpr = dpr;
    this.w = w;
    this.h = h;
  }

  worldToScreen(wx, wz) {
    return [
      (wx - this.cx) / this.scale + this.w / 2,
      (wz - this.cz) / this.scale + this.h / 2,
    ];
  }

  screenToWorld(sx, sy) {
    return [
      (sx - this.w / 2) * this.scale + this.cx,
      (sy - this.h / 2) * this.scale + this.cz,
    ];
  }

  zoomAt(sx, sy, factor) {
    const [wx, wz] = this.screenToWorld(sx, sy);
    this.scale = clamp(this.scale * factor, 1e2, 1e9);
    const [nx, nz] = this.screenToWorld(sx, sy);
    this.cx += wx - nx;
    this.cz += wz - nz;
  }

  _attachInput() {
    const c = this.canvas;
    c.addEventListener('mousedown', (e) => {
      this.dragging = true;
      this._dragMoved = false;
      this._lx = e.clientX; this._ly = e.clientY;
    });
    window.addEventListener('mousemove', (e) => {
      if (!this.dragging) return;
      const dx = e.clientX - this._lx, dy = e.clientY - this._ly;
      if (Math.abs(dx) + Math.abs(dy) > 3) this._dragMoved = true;
      this.cx -= dx * this.scale;
      this.cz -= dy * this.scale;
      this._lx = e.clientX; this._ly = e.clientY;
      this.follow = false;
    });
    window.addEventListener('mouseup', (e) => {
      if (this.dragging && !this._dragMoved) this._click(e);
      this.dragging = false;
    });
    c.addEventListener('wheel', (e) => {
      e.preventDefault();
      const rect = c.getBoundingClientRect();
      this.zoomAt(e.clientX - rect.left, e.clientY - rect.top, Math.exp(-e.deltaY * 0.0015));
    }, { passive: false });
  }

  _click(e) {
    const rect = this.canvas.getBoundingClientRect();
    const sx = e.clientX - rect.left, sy = e.clientY - rect.top;
    const [wx, wz] = this.screenToWorld(sx, sy);
    const pos = new THREE.Vector3(wx, 0, wz);
    // find nearest body (planets + moons + satellite) at current time
    let best = null, bestD = Infinity;
    const t = this._sim ? this._sim.ut : 0;
    const consider = (id, p) => {
      const d = pos.distanceTo(p);
      if (d < bestD) { bestD = d; best = id; }
    };
    for (const b of BODIES) {
      if (b.type === 'star') continue;
      consider(b.id, bodyPos(b, t));
    }
    if (this._sim && this._sim.scene3d && this._sim.scene3d.satBody) {
      consider('sat1', bodyPos(this._sim.scene3d.satBody, t));
    }
    const pickRadius = 14 * this.scale;
    if (best && bestD < pickRadius) {
      this._sim.targetId = best;
      this._sim.vessel.targetId = best;
    }
  }

  // cached static orbit path (parent frame == system frame for planets)
  _orbitPath(b) {
    if (this.orbitCache[b.id]) return this.orbitCache[b.id];
    const pts = sampleOrbitElements(b.a, b.e, b.inc, b.raan, b.argp, BODY_BY_ID[b.parent].mu, 220);
    this.orbitCache[b.id] = pts;
    return pts;
  }

  draw(sim, preview, previewNode) {
    this._sim = sim;
    this.resize();
    const ctx = this.ctx;
    const { w, h } = this;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.fillStyle = BG;
    ctx.fillRect(0, 0, w, h);
    const t = sim.ut;
    const V = sim.vessel;

    if (this.follow && !V.landed) {
      this.cx = V.r.x;
      this.cz = V.r.z;
    }

    // faint grid
    ctx.strokeStyle = GRID;
    ctx.lineWidth = 1;
    const gridStep = this._niceStep(this.scale * 120);
    const [wx0, wz0] = this.screenToWorld(0, 0);
    const [wx1, wz1] = this.screenToWorld(w, h);
    ctx.beginPath();
    for (let x = Math.floor(wx0 / gridStep) * gridStep; x <= wx1; x += gridStep) {
      const [sx] = this.worldToScreen(x, 0);
      ctx.moveTo(sx, 0); ctx.lineTo(sx, h);
    }
    for (let z = Math.floor(wz0 / gridStep) * gridStep; z <= wz1; z += gridStep) {
      const [, sy] = this.worldToScreen(0, z);
      ctx.moveTo(0, sy); ctx.lineTo(w, sy);
    }
    ctx.stroke();

    // star
    {
      const [sx, sy] = this.worldToScreen(0, 0);
      const g = ctx.createRadialGradient(sx, sy, 0, sx, sy, 26);
      g.addColorStop(0, 'rgba(255,220,140,0.9)');
      g.addColorStop(0.4, 'rgba(255,180,90,0.35)');
      g.addColorStop(1, 'rgba(255,160,80,0)');
      ctx.fillStyle = g;
      ctx.beginPath(); ctx.arc(sx, sy, 26, 0, TAU); ctx.fill();
      ctx.fillStyle = '#ffd27f';
      ctx.beginPath(); ctx.arc(sx, sy, 3.5, 0, TAU); ctx.fill();
      ctx.fillStyle = 'rgba(255,220,150,0.8)';
      ctx.font = '11px monospace';
      ctx.fillText('Solara', sx + 8, sy - 8);
    }

    // planet orbits + SOIs + bodies
    const primary = V.primary || STAR;
    for (const b of BODIES) {
      if (b.type === 'star' || !b.parent) continue;
      const parent = BODY_BY_ID[b.parent];
      const p = bodyPos(b, t);
      if (parent.id === STAR.id) {
        // static orbit path
        ctx.strokeStyle = ORBIT;
        ctx.lineWidth = 1;
        ctx.beginPath();
        const pts = this._orbitPath(b);
        for (let i = 0; i < pts.length; i++) {
          const [sx, sy] = this.worldToScreen(pts[i].x, pts[i].z);
          if (i === 0) ctx.moveTo(sx, sy); else ctx.lineTo(sx, sy);
        }
        ctx.stroke();
        // SOI of the current primary's family
        if (b.id === primary.id || b.id === (parentOf(primary.id)?.id) || childrenOf(primary.id).some((c) => c.id === b.id)) {
          ctx.strokeStyle = SOI;
          ctx.setLineDash([6, 6]);
          ctx.beginPath();
          const [sx, sy] = this.worldToScreen(p.x, p.z);
          ctx.arc(sx, sy, b.rsoi / this.scale, 0, TAU);
          ctx.stroke();
          ctx.setLineDash([]);
        }
      } else {
        // moon: orbit circle around its parent's current position
        ctx.strokeStyle = ORBIT;
        ctx.lineWidth = 1;
        ctx.beginPath();
        const [px, pz] = this.worldToScreen(p.x, p.z);
        ctx.arc(px, pz, b.a / this.scale, 0, TAU);
        ctx.stroke();
        if (b.id === primary.id || parentOf(primary.id)?.id === b.id) {
          ctx.strokeStyle = SOI;
          ctx.setLineDash([4, 5]);
          ctx.beginPath();
          ctx.arc(px, pz, b.rsoi / this.scale, 0, TAU);
          ctx.stroke();
          ctx.setLineDash([]);
        }
      }
      // body dot
      const [sx, sy] = this.worldToScreen(p.x, p.z);
      const isTarget = sim.targetId === b.id;
      const rPx = clamp(Math.log10(b.radius) * 2.2, 2.5, 9);
      ctx.fillStyle = '#' + b.color.getHexString();
      ctx.beginPath(); ctx.arc(sx, sy, rPx, 0, TAU); ctx.fill();
      if (isTarget) {
        ctx.strokeStyle = '#7cf2ff';
        ctx.lineWidth = 1.5;
        ctx.beginPath(); ctx.arc(sx, sy, rPx + 5, 0, TAU); ctx.stroke();
      }
      ctx.fillStyle = 'rgba(220,230,245,0.85)';
      ctx.font = '11px monospace';
      ctx.fillText(b.name, sx + rPx + 4, sy - 4);
    }

    // current primary's own SOI
    if (primary.type !== 'star') {
      const pp = bodyPos(primary, t);
      const [sx, sy] = this.worldToScreen(pp.x, pp.z);
      ctx.strokeStyle = SOI;
      ctx.setLineDash([8, 6]);
      ctx.beginPath(); ctx.arc(sx, sy, primary.rsoi / this.scale, 0, TAU); ctx.stroke();
      ctx.setLineDash([]);
    }

    // satellite
    if (sim.scene3d && sim.scene3d.satBody) {
      const sp = bodyPos(sim.scene3d.satBody, t);
      const [sx, sy] = this.worldToScreen(sp.x, sp.z);
      ctx.fillStyle = '#66ffcc';
      ctx.beginPath(); ctx.arc(sx, sy, 3, 0, TAU); ctx.fill();
      ctx.fillStyle = 'rgba(160,255,220,0.9)';
      ctx.font = '10px monospace';
      ctx.fillText('SAT-1', sx + 6, sy - 6);
      if (sim.targetId === 'sat1') {
        ctx.strokeStyle = '#7cf2ff';
        ctx.beginPath(); ctx.arc(sx, sy, 8, 0, TAU); ctx.stroke();
      }
    }

    // planner transfer arc
    if (sim.plannerTransfer) {
      const tr = sim.plannerTransfer;
      ctx.strokeStyle = TRANSFER;
      ctx.lineWidth = 1.5;
      ctx.setLineDash([8, 5]);
      ctx.beginPath();
      let r = tr.r1.clone();
      let v = tr.v1.clone();
      const n = 160;
      for (let i = 0; i <= n; i++) {
        if (i > 0) {
          const p = keplerProp(r, v, tr.tof / n, tr.mu);
          r = p.r; v = p.v;
        }
        const tI = (tr.td ?? sim.ut) + (i / n) * tr.tof;
        const pos = tr.frameBody ? r.clone().add(bodyPos(tr.frameBody, tI)) : r;
        const [sx, sy] = this.worldToScreen(pos.x, pos.z);
        if (i === 0) ctx.moveTo(sx, sy); else ctx.lineTo(sx, sy);
      }
      ctx.stroke();
      ctx.setLineDash([]);
    }

    // vessel trajectory
    const drawTraj = (pred, color, dashed) => {
      if (!pred) return;
      ctx.strokeStyle = color;
      ctx.lineWidth = dashed ? 1.5 : 1.8;
      if (dashed) ctx.setLineDash([7, 6]);
      for (const seg of pred.segments) {
        ctx.beginPath();
        seg.points.forEach((p, i) => {
          const [sx, sy] = this.worldToScreen(p.r.x, p.r.z);
          if (i === 0) ctx.moveTo(sx, sy); else ctx.lineTo(sx, sy);
        });
        ctx.stroke();
      }
      ctx.setLineDash([]);
      if (pred.impact) {
        const [sx, sy] = this.worldToScreen(pred.impact.r.x, pred.impact.r.z);
        ctx.fillStyle = '#ff5555';
        ctx.beginPath(); ctx.arc(sx, sy, 4, 0, TAU); ctx.fill();
      }
    };
    drawTraj(preview, TRAJ, false);
    drawTraj(previewNode, TRAJ_NODE, true);

    // Ap / Pe markers
    if (!V.landed && primary.type !== 'star') {
      const rel = V.r.clone().sub(bodyPos(primary, t));
      const relv = V.v.clone().sub(bodyVel(primary, t));
      if (rel.length() > primary.radius * 1.001) {
        const oe = rvToOE(rel, relv, primary.mu);
        if (oe.e < 1) {
          const bp = bodyPos(primary, t);
          const pe = oeToRV(oe, primary.mu, 0).r.add(bp);
          const ap = oeToRV(oe, primary.mu, Math.PI).r.add(bp);
          for (const [pos, label, col] of [[pe, 'Pe', '#7cf2ff'], [ap, 'Ap', '#ffa726']]) {
            const [sx, sy] = this.worldToScreen(pos.x, pos.z);
            ctx.strokeStyle = col;
            ctx.lineWidth = 1.2;
            ctx.beginPath(); ctx.arc(sx, sy, 4, 0, TAU); ctx.stroke();
            ctx.fillStyle = col;
            ctx.font = '10px monospace';
            ctx.fillText(label, sx + 6, sy - 6);
          }
        }
      }
    }

    // maneuver nodes
    for (const node of V.nodes) {
      if (node.applied || !node.predictedPos) continue;
      const [sx, sy] = this.worldToScreen(node.predictedPos.x, node.predictedPos.z);
      ctx.strokeStyle = TRAJ_NODE;
      ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.arc(sx, sy, 6, 0, TAU); ctx.stroke();
      ctx.beginPath(); ctx.moveTo(sx - 9, sy); ctx.lineTo(sx + 9, sy);
      ctx.moveTo(sx, sy - 9); ctx.lineTo(sx, sy + 9);
      ctx.stroke();
      ctx.fillStyle = TRAJ_NODE;
      ctx.font = '10px monospace';
      ctx.fillText(`Δv ${Math.hypot(node.dvp, node.dvr, node.dvn).toFixed(0)}`, sx + 10, sy + 4);
    }

    // target line + closest approach
    const targetBody = sim.targetId && sim.targetId !== 'sat1' ? BODY_BY_ID[sim.targetId] : null;
    const targetPos = targetBody ? bodyPos(targetBody, t)
      : (sim.targetId === 'sat1' && sim.scene3d ? bodyPos(sim.scene3d.satBody, t) : null);
    if (targetPos) {
      const [tx, ty] = this.worldToScreen(targetPos.x, targetPos.z);
      const [vx, vy] = this.worldToScreen(V.r.x, V.r.z);
      ctx.strokeStyle = 'rgba(124,242,255,0.4)';
      ctx.setLineDash([4, 5]);
      ctx.beginPath(); ctx.moveTo(vx, vy); ctx.lineTo(tx, ty); ctx.stroke();
      ctx.setLineDash([]);
      if (preview) {
        const ca = closestApproach(preview, targetBody || sim.scene3d.satBody, t);
        if (ca && ca.d < 5e9) {
          const [cx, cz] = this.worldToScreen(ca.r.x, ca.r.z);
          ctx.strokeStyle = '#7cf2ff';
          ctx.lineWidth = 1.2;
          ctx.beginPath(); ctx.arc(cx, cz, 5, 0, TAU); ctx.stroke();
          ctx.fillStyle = '#7cf2ff';
          ctx.font = '10px monospace';
          ctx.fillText(`Enc ${fmtDist(ca.d)}`, cx + 8, cz - 8);
        }
      }
    }

    // vessel marker
    {
      const [sx, sy] = this.worldToScreen(V.r.x, V.r.z);
      let ang;
      if (V.landed) {
        const up = V.r.clone().sub(bodyPos(primary, t)).normalize();
        ang = Math.atan2(up.z, up.x);
      } else {
        const relv = V.v.clone().sub(bodyVel(primary, t));
        if (relv.lengthSq() > 1) ang = Math.atan2(relv.z, relv.x);
        else ang = 0;
      }
      ctx.save();
      ctx.translate(sx, sy);
      ctx.rotate(ang - Math.PI / 2);
      ctx.fillStyle = '#ffffff';
      ctx.beginPath();
      ctx.moveTo(0, -7); ctx.lineTo(5, 6); ctx.lineTo(-5, 6);
      ctx.closePath(); ctx.fill();
      ctx.restore();
      // primary marker
      ctx.strokeStyle = 'rgba(255,255,255,0.5)';
      ctx.beginPath(); ctx.arc(sx, sy, 10, 0, TAU); ctx.stroke();
    }

    // HUD text
    ctx.fillStyle = 'rgba(220,230,245,0.9)';
    ctx.font = '12px monospace';
    ctx.fillText('MAP VIEW  ·  drag: pan  wheel: zoom  click: target  F: follow', 12, 20);
    // scale bar
    const barM = this._niceStep(this.scale * 100);
    const barPx = barM / this.scale;
    ctx.strokeStyle = 'rgba(220,230,245,0.8)';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(12, h - 18); ctx.lineTo(12 + barPx, h - 18);
    ctx.stroke();
    ctx.fillStyle = 'rgba(220,230,245,0.9)';
    ctx.font = '11px monospace';
    ctx.fillText(fmtDist(barM), 16 + barPx, h - 14);
    if (sim.targetId) {
      const name = sim.targetId === 'sat1' ? 'SAT-1' : (BODY_BY_ID[sim.targetId]?.name || sim.targetId);
      ctx.fillText(`Target: ${name}`, w - 140, 20);
    }
    if (this.follow) {
      ctx.fillStyle = 'rgba(124,242,255,0.9)';
      ctx.fillText('(following)', w - 140, 36);
    }
  }

  _niceStep(raw) {
    const exp = Math.floor(Math.log10(raw));
    const f = raw / Math.pow(10, exp);
    const nice = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
    return nice * Math.pow(10, exp);
  }
}
