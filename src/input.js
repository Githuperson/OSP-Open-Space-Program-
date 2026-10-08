// Keyboard/mouse input. Edge-triggered actions go through `actions`;
// continuous controls (throttle, attitude) are polled per frame.

import { clamp } from './util.js';

export class Input {
  constructor() {
    this.keys = {};
  }

  attach(actions, sim, scene3d, mapView, getMapMode) {
    this.actions = actions;
    this.sim = sim;
    this.scene3d = scene3d;
    this.mapView = mapView;
    this.getMapMode = getMapMode;

    window.addEventListener('keydown', (e) => {
      if (e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT' || e.target.tagName === 'TEXTAREA')) return;
      if (e.repeat) return;
      this.keys[e.code] = true;
      this._edge(e.code);
    });
    window.addEventListener('keyup', (e) => { this.keys[e.code] = false; });

    // 3D camera mouse controls
    const gl = scene3d.renderer.domElement;
    let dragging = false, lx = 0, ly = 0;
    gl.addEventListener('mousedown', (e) => { dragging = true; lx = e.clientX; ly = e.clientY; });
    window.addEventListener('mousemove', (e) => {
      if (!dragging) return;
      if (this.getMapMode()) return;
      scene3d.camAz += (e.clientX - lx) * 0.006;
      scene3d.camEl = clamp(scene3d.camEl + (e.clientY - ly) * 0.006, -1.5, 1.5);
      lx = e.clientX; ly = e.clientY;
    });
    window.addEventListener('mouseup', () => { dragging = false; });
    gl.addEventListener('wheel', (e) => {
      e.preventDefault();
      if (this.getMapMode()) return;
      scene3d.camDist = clamp(scene3d.camDist * Math.exp(e.deltaY * 0.0012), 8, 1e10);
    }, { passive: false });
    gl.addEventListener('contextmenu', (e) => e.preventDefault());
  }

  _edge(code) {
    const a = this.actions;
    const V = this.sim.vessel;
    switch (code) {
      case 'Space': a.stage(); break;
      case 'KeyX': V.throttle = 0; break;
      case 'KeyZ': V.throttle = 1; break;
      case 'KeyT': a.toggleSAS(); break;
      case 'KeyM': a.toggleMap(); break;
      case 'KeyC': a.cycleCamera(); break;
      case 'Comma': a.warpDown(); break;
      case 'Period': a.warpUp(); break;
      case 'KeyN': a.addNode(); break;
      case 'KeyY': a.cycleTarget(); break;
      case 'Escape': a.togglePause(); break;
      case 'KeyG': a.toggleLights(); break;
      case 'KeyF': if (this.getMapMode()) this.mapView.follow = !this.mapView.follow; break;
      case 'KeyP': a.togglePlanner(); break;
      case 'KeyH': a.toggleHelp(); break;
    }
  }

  isDown(code) { return !!this.keys[code]; }

  // per-frame continuous controls
  process(sim, realDt) {
    const V = sim.vessel;
    const k = this.keys;
    const any = (c) => this.isDown(c);
    if (any('ShiftLeft') || any('ShiftRight')) V.throttle = clamp(V.throttle + 0.6 * realDt, 0, 1);
    if (any('ControlLeft') || any('ControlRight')) V.throttle = clamp(V.throttle - 0.6 * realDt, 0, 1);
    sim.input.pitch = (any('KeyW') ? 1 : 0) - (any('KeyS') ? 1 : 0);
    sim.input.yaw = (any('KeyD') ? 1 : 0) - (any('KeyA') ? 1 : 0);
    sim.input.roll = (any('KeyE') ? 1 : 0) - (any('KeyQ') ? 1 : 0);
  }
}
