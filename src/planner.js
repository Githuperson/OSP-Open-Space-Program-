// Orbit planner panel: Lambert transfer solver, porkchop plot (departure
// date x transfer time heatmap of total delta-v), and a maneuver node editor.

import * as THREE from 'three';
import {
  BODIES, BODY_BY_ID, STAR, HOME, DAY, bodyPos, bodyVel,
} from './universe.js';
import { lambert } from './lambert.js';
import { keplerProp, dvComponents } from './conics.js';
import { WARPS } from './sim.js';
import { fmtTimeShort, fmtVel, fmtDist, heatColorCss, clamp, TAU } from './util.js';

const $ = (id) => document.getElementById(id);

export class Planner {
  constructor(sim) {
    this.sim = sim;
    this.visible = false;
    this.pork = null; // {grid, best, depWindow, tofMax, ND, NT, targetId}
    this._buildTargetSelect();
    this._wire();
  }

  _buildTargetSelect() {
    const sel = $('pl-target');
    sel.innerHTML = '';
    for (const b of BODIES) {
      if (b.type === 'star') continue;
      const o = document.createElement('option');
      o.value = b.id;
      o.textContent = b.name;
      sel.appendChild(o);
    }
    const sat = document.createElement('option');
    sat.value = 'sat1';
    sat.textContent = 'SAT-1 (satellite)';
    sel.appendChild(sat);
    sel.value = 'munara';
  }

  _wire() {
    $('planner-close').addEventListener('click', () => this.hide());
    $('pl-target').addEventListener('change', () => {
      this.sim.targetId = $('pl-target').value;
      this.sim.vessel.targetId = this.sim.targetId;
      this.computePorkchop();
    });
    $('pl-lambert').addEventListener('click', () => this.computeLambert());
    $('pl-add-node').addEventListener('click', () => this.addNodeFromTransfer());
    $('pl-draw').addEventListener('click', () => this.drawTransfer());
    $('pl-pork-best').addEventListener('click', () => this.usePorkchop(this.pork && this.pork.best));
    $('pl-porkchop').addEventListener('click', (e) => this._porkClick(e));
    $('pl-node-add').addEventListener('click', () => this.addNode());
    $('pl-node-warp').addEventListener('click', () => this.warpToNode());
    $('pl-node-clear').addEventListener('click', () => {
      this.sim.vessel.nodes = [];
      this.renderNodes();
    });
  }

  show() {
    this.visible = true;
    $('planner').classList.remove('hidden');
    if (!this.pork || this.pork.targetId !== $('pl-target').value) this.computePorkchop();
    this.renderNodes();
  }

  hide() {
    this.visible = false;
    $('planner').classList.add('hidden');
  }

  toggle() { this.visible ? this.hide() : this.show(); }

  // ------------------------------------------------------------ transfers

  // Frame setup for a transfer from the vessel's current primary to target.
  _frame(target) {
    const V = this.sim.vessel;
    const src = V.primary || HOME;
    const useSrcFrame = target.parent === src.id;
    return {
      src,
      useSrcFrame,
      mu: useSrcFrame ? src.mu : STAR.mu,
      frameBody: useSrcFrame ? src : null,
    };
  }

  // departure state (position+velocity in the transfer frame) at time td
  _depState(td, frame) {
    const V = this.sim.vessel;
    const sim = this.sim;
    const src = frame.src;
    let r, v;
    if (!V.landed && V.primary === src && td >= sim.ut - 1) {
      // propagate the vessel's current orbit about its primary
      const rel = V.r.clone().sub(bodyPos(src, sim.ut));
      const relv = V.v.clone().sub(bodyVel(src, sim.ut));
      const p = keplerProp(rel, relv, Math.max(0, td - sim.ut), src.mu);
      r = p.r; v = p.v;
    } else {
      // nominal parking orbit around src (equatorial, prograde)
      const alt = Math.max(100e3, src.radius * 0.15);
      const a = src.radius + alt;
      const n = Math.sqrt(src.mu / (a ** 3));
      const phase = n * (td - sim.ut);
      const vc = Math.sqrt(src.mu / a);
      r = new THREE.Vector3(a * Math.cos(phase), 0, a * Math.sin(phase));
      v = new THREE.Vector3(-vc * Math.sin(phase), 0, vc * Math.cos(phase));
    }
    if (frame.useSrcFrame) return { r, v };
    // star frame: convert the primary-relative state to system coordinates
    return {
      r: r.add(bodyPos(src, td)),
      v: v.add(bodyVel(src, td)),
    };
  }

  _targetState(target, ta, frame) {
    if (frame.useSrcFrame) {
      return {
        r2: bodyPos(target, ta).sub(bodyPos(frame.src, ta)),
        baseVel: bodyVel(target, ta).sub(bodyVel(frame.src, ta)),
      };
    }
    return { r2: bodyPos(target, ta), baseVel: bodyVel(target, ta) };
  }

  _arrivalDv(target, sol, ts) {
    // capture into a circular parking orbit around the target. The vessel
    // arrives with vInf relative to the target and accelerates hyperbolically
    // to vHyp at the parking radius; the burn is vHyp - vCirc.
    const vArrRel = sol.v2.clone().sub(ts.baseVel);
    const vInf = vArrRel.length();
    const altT = Math.max(50e3, target.radius * 0.1);
    const rPark = target.radius + altT;
    const vCirc = Math.sqrt(target.mu / rPark);
    const vHyp = Math.sqrt(vInf * vInf + 2 * target.mu / rPark);
    return vHyp - vCirc;
  }

  // Compute a transfer departing at td with the given tof. Returns details.
  computeTransfer(td, tof, target) {
    const frame = this._frame(target);
    const dep = this._depState(td, frame);
    const ts = this._targetState(target, td + tof, frame);
    const sol = lambert(dep.r, ts.r2, tof, frame.mu);
    if (!sol) return null;
    const dvDep = sol.v1.distanceTo(dep.v);
    const dvArr = this._arrivalDv(target, sol, ts);
    return {
      td, tof, target, frame, dep, sol,
      dvDep, dvArr, dvTotal: dvDep + dvArr,
    };
  }

  computeLambert() {
    const sim = this.sim;
    const targetId = $('pl-target').value;
    const target = BODY_BY_ID[targetId];
    if (!target) return;
    sim.targetId = targetId;
    sim.vessel.targetId = targetId;
    const tofDays = clamp(parseFloat($('pl-tof').value) || 120, 1, 3000);
    const res = this.computeTransfer(sim.ut, tofDays * DAY, target);
    const out = $('pl-lambert-out');
    if (!res) {
      out.textContent = 'No single-revolution solution for this transfer time.';
      return;
    }
    this._lastTransfer = res;
    const e = res.sol.v1.clone().sub(res.dep.v);
    out.innerHTML =
      `Departure burn: <b>${res.dvDep.toFixed(0)} m/s</b> &nbsp; ` +
      `Arrival capture: <b>${res.dvArr.toFixed(0)} m/s</b> &nbsp; ` +
      `Total: <b>${res.dvTotal.toFixed(0)} m/s</b><br>` +
      `Arrives at ${target.name} UT ${fmtTimeShort(res.td + res.tof)}`;
  }

  addNodeFromTransfer() {
    const res = this._lastTransfer;
    const sim = this.sim;
    if (!res) { this.computeLambert(); return; }
    if (!this._lastTransfer) return;
    const V = sim.vessel;
    // delta-v in the system frame
    const dvSys = res.sol.v1.clone().sub(res.dep.v);
    if (res.frame.useSrcFrame) {
      // dep.v is relative to src; sol.v1 is relative to src; dv is frame-free
    }
    const { rel, relv } = (() => {
      const p = V.primary;
      return {
        rel: V.r.clone().sub(bodyPos(p, sim.ut)),
        relv: V.v.clone().sub(bodyVel(p, sim.ut)),
      };
    })();
    const comp = dvComponents(dvSys, rel, relv);
    V.addNode(sim.ut + 5, comp.prograde, comp.radial, comp.normal);
    this.renderNodes();
  }

  drawTransfer() {
    const res = this._lastTransfer;
    if (!res) return;
    const sim = this.sim;
    // r1/v1 stay in the transfer frame; frameBody tells the map how to place them
    sim.plannerTransfer = {
      r1: res.dep.r.clone(),
      v1: res.sol.v1.clone(),
      tof: res.tof,
      mu: res.frame.mu,
      frameBody: res.frame.frameBody,
      targetId: res.target.id,
    };
  }

  // ------------------------------------------------------------ porkchop

  computePorkchop() {
    const sim = this.sim;
    const targetId = $('pl-target').value;
    const target = BODY_BY_ID[targetId];
    const canvas = $('pl-porkchop');
    if (!target) return;
    sim.targetId = targetId;
    sim.vessel.targetId = targetId;
    const V = sim.vessel;
    const src = V.primary || HOME;
    const frame = this._frame(target);

    const srcPeriod = src.period !== Infinity ? src.period : 1e9;
    const tgtPeriod = target.period !== Infinity ? target.period : 1e9;
    const synodic = 1 / Math.abs(1 / srcPeriod - 1 / tgtPeriod);
    const depWindow = clamp(1.3 * synodic, 20 * DAY, 1200 * DAY);
    // tof range scales with the synodic period so moons get short windows and
    // planets get long ones
    const tofMin = clamp(0.15 * synodic, 6 * 3600, 5 * DAY);
    const tofMax = clamp(0.9 * synodic, 1 * DAY, 900 * DAY);
    const ND = 56, NT = 40;

    const grid = new Float64Array(ND * NT).fill(Infinity);
    let best = null;
    for (let i = 0; i < ND; i++) {
      const td = sim.ut + (i + 0.5) * (depWindow / ND);
      const dep = this._depState(td, frame);
      for (let j = 0; j < NT; j++) {
        const tof = tofMin + (j + 0.5) * ((tofMax - tofMin) / NT);
        const res = this.computeTransfer(td, tof, target);
        if (!res) continue;
        grid[i * NT + j] = res.dvTotal;
        if (!best || res.dvTotal < best.dvTotal) {
          best = { i, j, td, tof, dvTotal: res.dvTotal, dvDep: res.dvDep, dvArr: res.dvArr };
        }
      }
    }
    this.pork = { grid, best, depWindow, tofMin, tofMax, ND, NT, targetId, src, frame };
    this._drawPorkchop(canvas);
    const out = $('pl-pork-out');
    if (best) {
      out.innerHTML =
        `Best window: depart UT ${fmtTimeShort(best.td)} (T+${((best.td - sim.ut) / DAY).toFixed(0)}d), ` +
        `fly ${(best.tof / DAY).toFixed(0)}d → <b>${best.dvTotal.toFixed(0)} m/s</b> total ` +
        `(${best.dvDep.toFixed(0)} out + ${best.dvArr.toFixed(0)} in)`;
    } else {
      out.textContent = 'No transfers found in the scanned window.';
    }
  }

  _drawPorkchop(canvas) {
    const pork = this.pork;
    if (!pork) return;
    const ctx = canvas.getContext('2d');
    const W = canvas.width, H = canvas.height;
    const padL = 46, padB = 22, padT = 8, padR = 8;
    const cw = (W - padL - padR) / pork.ND;
    const ch = (H - padT - padB) / pork.NT;
    const dvMin = 300, dvMax = 15000;
    const lMin = Math.log(dvMin), lMax = Math.log(dvMax);
    for (let i = 0; i < pork.ND; i++) {
      for (let j = 0; j < pork.NT; j++) {
        const dv = pork.grid[i * pork.NT + j];
        if (!isFinite(dv)) { ctx.fillStyle = '#101826'; }
        else {
          const t = clamp((Math.log(dv) - lMin) / (lMax - lMin), 0, 1);
          ctx.fillStyle = heatColorCss(t);
        }
        ctx.fillRect(padL + i * cw, padT + (pork.NT - 1 - j) * ch, Math.ceil(cw), Math.ceil(ch));
      }
    }
    // best marker
    if (pork.best) {
      const x = padL + (pork.best.i + 0.5) * cw;
      const y = padT + (pork.NT - 1 - pork.best.j + 0.5) * ch;
      ctx.strokeStyle = '#ffffff';
      ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.arc(x, y, 5, 0, TAU); ctx.stroke();
    }
    // axes
    ctx.fillStyle = 'rgba(210,225,245,0.85)';
    ctx.font = '9px monospace';
    ctx.textAlign = 'center';
    for (let i = 0; i < pork.ND; i += 8) {
      const td = this.sim.ut + (i + 0.5) * (pork.depWindow / pork.ND);
      const x = padL + (i + 0.5) * cw;
      ctx.fillText(`+${((td - this.sim.ut) / DAY).toFixed(0)}d`, x, H - 8);
    }
    ctx.textAlign = 'right';
    for (let j = 0; j < pork.NT; j += 8) {
      const tof = pork.tofMin + (j + 0.5) * ((pork.tofMax - pork.tofMin) / pork.NT);
      const y = padT + (pork.NT - 1 - j + 0.5) * ch;
      ctx.fillText(`${(tof / DAY).toFixed(0)}d`, padL - 5, y + 3);
    }
    ctx.textAlign = 'left';
    ctx.fillText('departure →', padL, H - 8 + 0); // placeholder replaced below
    ctx.fillText('tof ↑', 4, padT + 8);
  }

  _porkClick(ev) {
    const pork = this.pork;
    if (!pork) return;
    const canvas = $('pl-porkchop');
    const rect = canvas.getBoundingClientRect();
    const x = (ev.clientX - rect.left) * (canvas.width / rect.width);
    const y = (ev.clientY - rect.top) * (canvas.height / rect.height);
    const padL = 46, padB = 22, padT = 8, padR = 8;
    const cw = (canvas.width - padL - padR) / pork.ND;
    const ch = (canvas.height - padT - padB) / pork.NT;
    const i = clamp(Math.floor((x - padL) / cw), 0, pork.ND - 1);
    const j = clamp(pork.NT - 1 - Math.floor((y - padT) / ch), 0, pork.NT - 1);
    if (i == null || j == null) return;
    this.usePorkchop({ i, j });
  }

  usePorkchop(cell) {
    const pork = this.pork;
    if (!pork || !cell) return;
    const target = BODY_BY_ID[pork.targetId];
    const td = this.sim.ut + (cell.i + 0.5) * (pork.depWindow / pork.ND);
    const tof = pork.tofMin + (cell.j + 0.5) * ((pork.tofMax - pork.tofMin) / pork.NT);
    const res = this.computeTransfer(td, tof, target);
    if (!res) return;
    this._lastTransfer = res;
    // draw on map
    const sim = this.sim;
    let r1 = res.dep.r.clone();
    let v1 = res.sol.v1.clone();
    let frameBody = res.frame.frameBody;
    if (!res.frame.useSrcFrame) {
      // system frame already
    }
    sim.plannerTransfer = { r1, v1, tof: res.tof, td: res.td, mu: res.frame.mu, frameBody, targetId: target.id };
    $('pl-pork-out').innerHTML =
      `Selected: depart UT ${fmtTimeShort(td)} (T+${((td - sim.ut) / DAY).toFixed(0)}d), ` +
      `fly ${(tof / DAY).toFixed(0)}d → <b>${res.dvTotal.toFixed(0)} m/s</b> total ` +
      `(${res.dvDep.toFixed(0)} out + ${res.dvArr.toFixed(0)} in). Drawn on map.`;
  }

  // ------------------------------------------------------------ nodes

  addNode() {
    const sim = this.sim;
    sim.vessel.addNode(sim.ut + 60, 0, 0, 0);
    this.renderNodes();
  }

  warpToNode() {
    const node = this.sim.vessel.nextNode();
    if (node) this.sim.warpToNodeUt = node.ut;
  }

  renderNodes() {
    const sim = this.sim;
    const V = sim.vessel;
    const container = $('pl-nodes');
    container.innerHTML = '';
    V.nodes.forEach((node, idx) => {
      const row = document.createElement('div');
      row.className = 'node-row';
      const dv = Math.hypot(node.dvp, node.dvr, node.dvn);
      const dt = node.ut - sim.ut;
      row.innerHTML = `
        <div class="node-head">
          <span>Node ${idx + 1} · T${dt >= 0 ? '+' : ''}${fmtTimeShort(Math.abs(dt))} · Δv ${dv.toFixed(0)} m/s ${node.applied ? '(done)' : ''}</span>
          <button class="node-del" title="Delete node">✕</button>
        </div>
        <label>Pro/Retro <input type="range" min="-2000" max="2000" step="1" value="${node.dvp.toFixed(0)}" data-k="dvp"><span>${node.dvp.toFixed(0)}</span></label>
        <label>Radial <input type="range" min="-1000" max="1000" step="1" value="${node.dvr.toFixed(0)}" data-k="dvr"><span>${node.dvr.toFixed(0)}</span></label>
        <label>Normal <input type="range" min="-1000" max="1000" step="1" value="${node.dvn.toFixed(0)}" data-k="dvn"><span>${node.dvn.toFixed(0)}</span></label>
      `;
      row.querySelector('.node-del').addEventListener('click', () => {
        V.removeNode(node.id);
        this.renderNodes();
      });
      row.querySelectorAll('input[type=range]').forEach((inp) => {
        inp.addEventListener('input', () => {
          node[inp.dataset.k] = parseFloat(inp.value);
          row.querySelector(`span`, );
          inp.nextElementSibling.textContent = inp.value;
          row.querySelector('.node-head span').textContent =
            `Node ${idx + 1} · T+${fmtTimeShort(Math.max(0, node.ut - sim.ut))} · Δv ${Math.hypot(node.dvp, node.dvr, node.dvn).toFixed(0)} m/s`;
        });
      });
      container.appendChild(row);
    });
    if (V.nodes.length === 0) {
      container.innerHTML = '<div class="muted">No maneuver nodes. Add one to plan a burn.</div>';
    }
  }

  update(sim) {
    if (!this.visible) return;
    // refresh node head labels with the countdown
    const V = sim.vessel;
    const rows = $('pl-nodes').querySelectorAll('.node-row');
    rows.forEach((row, idx) => {
      const node = V.nodes[idx];
      if (!node) return;
      const head = row.querySelector('.node-head span');
      if (head) {
        const dt = node.ut - sim.ut;
        head.textContent =
          `Node ${idx + 1} · T${dt >= 0 ? '+' : '−'}${fmtTimeShort(Math.abs(dt))} · Δv ${Math.hypot(node.dvp, node.dvr, node.dvn).toFixed(0)} m/s ${node.applied ? '(done)' : ''}`;
      }
    });
    // next-node countdown in the warp button
    const node = V.nextNode();
    $('pl-node-warp').textContent = node
      ? `Warp to node (T+${fmtTimeShort(Math.max(0, node.ut - sim.ut))})`
      : 'Warp to next node';
  }
}
