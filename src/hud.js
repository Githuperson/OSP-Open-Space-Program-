// HUD: updates the DOM overlay (clock, orbit/vessel info, throttle, buttons,
// messages, crash overlay).

import * as THREE from 'three';
import {
  BODY_BY_ID, bodyPos, bodyVel,
} from './universe.js';
import { rvToOE } from './conics.js';
import {
  WARPS, altitudeOf, relativeState, pushMessage,
} from './sim.js';
import {
  fmtTimeShort, fmtDist, fmtVel, fmtMass, fmtAngle, clamp, TAU,
} from './util.js';

const $ = (id) => document.getElementById(id);

export class HUD {
  constructor(actions) {
    this.actions = actions;
    this.el = {
      clock: $('clock'), warp: $('warp'), target: $('target-info'),
      msgs: $('msg-log'),
      vMass: $('v-mass'), vDv: $('v-dv'), vTwr: $('v-twr'), vStage: $('v-stage'),
      vQ: $('v-q'), vG: $('v-g'), vThrottle: $('v-throttle'),
      oAlt: $('o-alt'), oVel: $('o-vel'), oAp: $('o-ap'), oPe: $('o-pe'),
      oTAp: $('o-tap'), oTPe: $('o-tpe'), oInc: $('o-inc'), oEcc: $('o-ecc'), oPeriod: $('o-period'),
      throttleBar: $('throttle-bar'),
      crash: $('crash-overlay'),
      sasButtons: [...document.querySelectorAll('.sas-btn')],
      warpMinus: $('warp-minus'), warpPlus: $('warp-plus'),
    };
    this._wireButtons();
  }

  _wireButtons() {
    const a = this.actions;
    $('btn-stage').addEventListener('click', () => a.stage());
    $('btn-sas').addEventListener('click', () => a.toggleSAS());
    $('btn-map').addEventListener('click', () => a.toggleMap());
    $('btn-camera').addEventListener('click', () => a.cycleCamera());
    $('btn-planner').addEventListener('click', () => a.togglePlanner());
    $('btn-revert').addEventListener('click', () => a.revert());
    $('btn-lights').addEventListener('click', () => a.toggleLights());
    this.el.warpMinus.addEventListener('click', () => a.warpDown());
    this.el.warpPlus.addEventListener('click', () => a.warpUp());
    for (const btn of this.el.sasButtons) {
      btn.addEventListener('click', () => a.setSAS(btn.dataset.mode));
    }
  }

  update(sim) {
    const V = sim.vessel;
    const e = this.el;
    const t = sim.ut;

    e.clock.textContent = `UT ${fmtTimeShort(t)}`;
    e.warp.textContent = sim.paused ? '❚❚ PAUSED' : `WARP ×${WARPS[sim.warpIndex]}`;
    e.warp.style.color = sim.warpIndex > 0 ? '#ffb74d' : '';

    // target info
    if (sim.targetId) {
      const name = sim.targetId === 'sat1' ? 'SAT-1' : (BODY_BY_ID[sim.targetId]?.name || sim.targetId);
      const tb = sim.targetId === 'sat1' ? sim.scene3d?.satBody : BODY_BY_ID[sim.targetId];
      if (tb) {
        const tp = bodyPos(tb, t);
        const d = tp.distanceTo(V.r);
        e.target.textContent = `⊕ ${name} · ${fmtDist(d)}`;
      }
    } else e.target.textContent = '';

    // messages
    e.msgs.innerHTML = sim.messages
      .filter((m) => sim.time - m.t < m.ttl)
      .slice(-4)
      .map((m) => `<div style="opacity:${clamp(1 - (sim.time - m.t) / m.ttl, 0.25, 1)}">${m.text}</div>`)
      .join('');

    // vessel info
    const mass = V.mass;
    const thrust = V.thrust();
    e.vMass.textContent = fmtMass(mass);
    e.vDv.textContent = `${V.deltaV.toFixed(0)} m/s`;
    const g0 = V.primary ? V.primary.g0 : 9.81;
    e.vTwr.textContent = mass > 0 ? (thrust / (mass * g0)).toFixed(2) : '—';
    e.vStage.textContent = `Stg ${V.stageIndex + 1}/${V.stages.length}`;
    e.vQ.textContent = V.maxQ > 0 ? `${(V.maxQ / 1000).toFixed(1)} kPa` : '—';
    e.vG.textContent = V.inAtmo ? 'atmo' : (V.landed ? 'landed' : 'vac');
    e.vThrottle.textContent = `${Math.round(V.throttle * 100)}%`;
    e.throttleBar.style.width = `${V.throttle * 100}%`;

    // orbit info
    const alt = altitudeOf(sim);
    if (alt != null && V.primary) {
      const { primary, rel, relv } = relativeState(sim);
      const speed = relv.length();
      e.oAlt.textContent = fmtDist(Math.max(alt, 0));
      e.oVel.textContent = fmtVel(speed);
      if (rel.length() > primary.radius * 1.0005 && !V.landed) {
        const oe = rvToOE(rel, relv, primary.mu);
        const altAp = oe.apoapsis - primary.radius;
        const altPe = oe.periapsis - primary.radius;
        e.oAp.textContent = oe.e < 1 ? fmtDist(altAp) : '∞ (escape)';
        e.oPe.textContent = fmtDist(altPe);
        e.oInc.textContent = fmtAngle(oe.inc * 180 / Math.PI);
        e.oEcc.textContent = oe.e.toFixed(3);
        e.oPeriod.textContent = oe.period !== Infinity ? fmtTimeShort(oe.period) : '—';
        // time to Ap/Pe
        if (oe.e < 1 && oe.period !== Infinity) {
          const n = Math.sqrt(primary.mu / Math.abs(oe.a) ** 3);
          const E = 2 * Math.atan2(Math.sqrt(1 - oe.e) * Math.sin(oe.ta / 2), Math.sqrt(1 + oe.e) * Math.cos(oe.ta / 2));
          const M = E - oe.e * Math.sin(E);
          const tPe = ((0 - M) % TAU + TAU) % TAU / n;
          const tAp = ((Math.PI - M) % TAU + TAU) % TAU / n;
          e.oTPe.textContent = relv.dot(rel) >= 0 ? fmtTimeShort(tPe) : fmtTimeShort(oe.period - tPe);
          e.oTAp.textContent = relv.dot(rel) >= 0 ? fmtTimeShort(oe.period - tAp) : fmtTimeShort(tAp);
        } else {
          e.oTPe.textContent = '—';
          e.oTAp.textContent = '—';
        }
      } else {
        e.oAp.textContent = '—';
        e.oPe.textContent = '—';
        e.oInc.textContent = '—';
        e.oEcc.textContent = '—';
        e.oPeriod.textContent = '—';
        e.oTPe.textContent = '—';
        e.oTAp.textContent = '—';
      }
    }

    // SAS buttons highlight
    for (const btn of e.sasButtons) {
      btn.classList.toggle('active', btn.dataset.mode === V.sasMode);
    }

    // crash overlay
    e.crash.style.display = V.crashed ? 'flex' : 'none';
  }
}
