// OSP - Open Space Program. Application entry point: wires together the
// simulation, 3D scene, map view, navball, HUD, planner and input, and runs
// the game loop.

import * as THREE from 'three';
import { createSim, step, placeOnPad, WARPS, SAS_MODES, pushMessage } from './sim.js';
import { BODY_BY_ID, BODIES, STAR } from './universe.js';
import { predictTrajectory } from './propagate.js';
import { buildRocket } from './rocket.js';
import { Scene3D } from './scene3d.js';
import { MapView } from './mapview.js';
import { Navball } from './navball.js';
import { HUD } from './hud.js';
import { Planner } from './planner.js';
import { Input } from './input.js';
import { clamp } from './util.js';

const $ = (id) => document.getElementById(id);

// ---------------------------------------------------------------- setup

const sim = createSim();
const scene3d = new Scene3D($('gl'));
sim.scene3d = scene3d;

const rocket = buildRocket();
scene3d.attachRocket(rocket);

const mapView = new MapView($('map'));
const navball = new Navball($('navball'), 190);
const planner = new Planner(sim);

let mapMode = false;
let helpVisible = true;
$('help').classList.remove('hidden');

const actions = {
  stage: () => {
    const V = sim.vessel;
    if (V.landed && V.throttle < 0.05) {
      pushMessage(sim, 'Ignition locked: throttle up first (Z).');
      return;
    }
    V.stage();
  },
  toggleSAS: () => {
    const V = sim.vessel;
    if (V.sasMode === 'off') V.sasMode = V.lastSASMode || 'stability';
    else { V.lastSASMode = V.sasMode; V.sasMode = 'off'; }
  },
  setSAS: (mode) => {
    const V = sim.vessel;
    if (mode === 'off') { V.sasMode = 'off'; return; }
    V.lastSASMode = mode;
    V.sasMode = mode;
  },
  toggleMap: () => {
    mapMode = !mapMode;
    $('map').classList.toggle('hidden', !mapMode);
    $('hud').classList.toggle('hidden', mapMode);
    if (!mapMode) scene3d.onResize();
  },
  cycleCamera: () => {
    scene3d.camMode = scene3d.camMode === 'orbital' ? 'chase' : 'orbital';
    pushMessage(sim, `Camera: ${scene3d.camMode}`);
  },
  togglePlanner: () => planner.toggle(),
  togglePause: () => { sim.paused = !sim.paused; },
  toggleLights: () => { sim.vessel.gearLights = !sim.vessel.gearLights; },
  toggleHelp: () => {
    helpVisible = !helpVisible;
    $('help').classList.toggle('hidden', !helpVisible);
  },
  revert: () => {
    placeOnPad(sim);
    planner.renderNodes();
    pushMessage(sim, 'Reverted to launch pad.');
  },
  warpUp: () => { if (!sim.paused && !sim.vessel.crashed) sim.warpIndex = clamp(sim.warpIndex + 1, 0, WARPS.length - 1); },
  warpDown: () => { sim.warpIndex = clamp(sim.warpIndex - 1, 0, WARPS.length - 1); },
  addNode: () => {
    sim.vessel.addNode(sim.ut + 60, 0, 0, 0);
    planner.renderNodes();
    if (!planner.visible) planner.show();
  },
  cycleTarget: () => {
    const ids = BODIES.filter((b) => b.type !== 'star').map((b) => b.id).concat(['sat1']);
    const cur = ids.indexOf(sim.targetId);
    const next = ids[(cur + 1) % ids.length];
    sim.targetId = next;
    sim.vessel.targetId = next;
    $('pl-target').value = next;
    const name = next === 'sat1' ? 'SAT-1' : BODY_BY_ID[next].name;
    pushMessage(sim, `Target: ${name}`);
  },
};

const hud = new HUD(actions);
const input = new Input();
input.attach(actions, sim, scene3d, mapView, () => mapMode);

$('help-close').addEventListener('click', () => actions.toggleHelp());
$('crash-revert').addEventListener('click', () => actions.revert());

// ---------------------------------------------------------------- loop

let last = performance.now();

function frame(now) {
  const realDt = Math.min((now - last) / 1000, 0.1);
  last = now;

  input.process(sim, realDt);
  step(sim, realDt);

  const V = sim.vessel;
  // trajectory previews (patched conics, with and without maneuver nodes)
  let pred = null, predNode = null;
  if (!V.crashed && V.primary) {
    pred = predictTrajectory(
      { r: V.r, v: V.v, ut: sim.ut },
      { primary: V.primary.id, nodes: V.nodes, maxPoints: 2600 }
    );
    if (V.nodes.some((n) => !n.applied)) {
      predNode = predictTrajectory(
        { r: V.r, v: V.v, ut: sim.ut },
        { primary: V.primary.id, nodes: V.nodes, maxPoints: 2600, stopAfterNode: V.nodes.length - 1 }
      );
    }
  }

  scene3d.update(sim, realDt, pred, predNode);

  if (mapMode) {
    mapView.draw(sim, pred, predNode);
  } else {
    navball.draw(sim);
    scene3d.render();
  }

  hud.update(sim);
  planner.update(sim);

  requestAnimationFrame(frame);
}

requestAnimationFrame(frame);
