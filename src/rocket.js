// Procedural 3D rocket model built from Three.js primitives (the satellite
// you can rendezvous with is a real GLB model downloaded from the internet).
// The rocket points along +Y (nose up). Group origin = nose tip.
// Each stage group's local origin is at the TOP CENTRE of the stage, with
// content extending downward (negative Y).

import * as THREE from 'three';
import { STOCK_ROCKET } from './vessel.js';

const MAT = {
  metal: new THREE.MeshStandardMaterial({ color: 0xd8dde2, metalness: 0.75, roughness: 0.35 }),
  metalDark: new THREE.MeshStandardMaterial({ color: 0x5a6068, metalness: 0.8, roughness: 0.4 }),
  white: new THREE.MeshStandardMaterial({ color: 0xf2efe9, metalness: 0.1, roughness: 0.6 }),
  dark: new THREE.MeshStandardMaterial({ color: 0x2b2f36, metalness: 0.4, roughness: 0.7 }),
  heatshield: new THREE.MeshStandardMaterial({ color: 0x4a3628, roughness: 0.9, metalness: 0.1 }),
  stripe: new THREE.MeshStandardMaterial({ color: 0xc23b2e, metalness: 0.3, roughness: 0.5 }),
  window: new THREE.MeshStandardMaterial({ color: 0x9fd8e8, emissive: 0x66c8e0, emissiveIntensity: 0.6, roughness: 0.2, metalness: 0.4 }),
};

// place a part of height h so its TOP is at topY (local, downward-positive layout)
function place(part, topY, h) {
  part.position.y = -(topY + h / 2);
  return part;
}

function tank(radius, cylH, stripes = 2) {
  const g = new THREE.Group();
  g.add(new THREE.Mesh(new THREE.CylinderGeometry(radius, radius, cylH, 32), MAT.metal));
  for (let i = 1; i <= stripes; i++) {
    const ring = new THREE.Mesh(
      new THREE.CylinderGeometry(radius + 0.006, radius + 0.006, cylH * 0.05, 32), MAT.stripe
    );
    ring.position.y = -cylH / 2 + (cylH * i) / (stripes + 1);
    g.add(ring);
  }
  const domeT = new THREE.Mesh(new THREE.SphereGeometry(radius, 32, 16, 0, Math.PI * 2, 0, Math.PI / 2), MAT.metal);
  domeT.position.y = -cylH / 2;
  const domeB = new THREE.Mesh(new THREE.SphereGeometry(radius, 32, 16, 0, Math.PI * 2, Math.PI / 2, Math.PI), MAT.metal);
  domeB.position.y = cylH / 2;
  g.add(domeT, domeB);
  g.userData.height = cylH + radius; // cylinder + top dome
  return g;
}

function engineBell(rTop, rBottom, bellH, mountH) {
  const g = new THREE.Group();
  const mount = new THREE.Mesh(new THREE.CylinderGeometry(rTop + 0.12, rTop + 0.05, mountH, 24), MAT.metalDark);
  mount.position.y = -mountH / 2;
  const bell = new THREE.Mesh(new THREE.CylinderGeometry(rTop, rBottom, bellH, 32, 1, true), MAT.metalDark);
  bell.position.y = -(mountH + bellH / 2);
  const rim = new THREE.Mesh(new THREE.TorusGeometry(rBottom, 0.045, 12, 32), MAT.metal);
  rim.rotation.x = Math.PI / 2;
  rim.position.y = -(mountH + bellH);
  g.add(mount, bell, rim);
  g.userData.height = mountH + bellH;
  return g;
}

function interstage(radius, height) {
  const g = new THREE.Group();
  const tube = new THREE.Mesh(new THREE.CylinderGeometry(radius + 0.02, radius + 0.02, height, 24, 1, true), MAT.dark);
  tube.position.y = -height / 2;
  g.add(tube);
  for (let i = 0; i < 6; i++) {
    const strut = new THREE.Mesh(new THREE.BoxGeometry(0.05, height, 0.05), MAT.metalDark);
    const a = (i / 6) * Math.PI * 2;
    strut.position.set(Math.cos(a) * radius, -height / 2, Math.sin(a) * radius);
    strut.rotation.y = -a;
    g.add(strut);
  }
  g.userData.height = height;
  return g;
}

function fins(radius, count = 3) {
  const g = new THREE.Group();
  const shape = new THREE.Shape();
  shape.moveTo(0, 0);
  shape.lineTo(0.55, 0);
  shape.lineTo(0.75, 1.3);
  shape.lineTo(0.15, 1.3);
  shape.lineTo(0, 0.9);
  const geo = new THREE.ExtrudeGeometry(shape, { depth: 0.06, bevelEnabled: false });
  for (let i = 0; i < count; i++) {
    const fin = new THREE.Mesh(geo, MAT.stripe);
    const a = (i / count) * Math.PI * 2;
    fin.position.set(Math.cos(a) * radius, 0, Math.sin(a) * radius);
    fin.rotation.y = -a;
    g.add(fin);
  }
  return g;
}

function capsuleStage(radius) {
  const g = new THREE.Group();
  const beacon = new THREE.Mesh(new THREE.SphereGeometry(0.09, 12, 12),
    new THREE.MeshStandardMaterial({ color: 0xff3333, emissive: 0xff2222, emissiveIntensity: 2 }));
  g.add(place(beacon, 0, 0.18));
  const antenna = new THREE.Mesh(new THREE.CylinderGeometry(0.02, 0.02, 0.8, 8), MAT.metalDark);
  g.add(place(antenna, 0.2, 0.8));
  const pack = new THREE.Mesh(new THREE.CylinderGeometry(0.28, 0.34, 0.4, 24), MAT.metalDark);
  g.add(place(pack, 1.0, 0.4));
  const cone = new THREE.Mesh(new THREE.ConeGeometry(radius, 1.3, 32), MAT.white);
  g.add(place(cone, 1.4, 1.3));
  const win = new THREE.Mesh(new THREE.TorusGeometry(radius + 0.006, 0.07, 12, 40), MAT.window);
  win.rotation.x = Math.PI / 2;
  win.position.y = -2.75;
  g.add(win);
  const body = new THREE.Mesh(new THREE.CylinderGeometry(radius, radius, 1.1, 32), MAT.white);
  g.add(place(body, 2.75, 1.1));
  const heat = new THREE.Mesh(new THREE.CylinderGeometry(radius, radius * 1.06, 0.45, 32), MAT.heatshield);
  g.add(place(heat, 3.85, 0.45));
  g.userData.height = 4.3;
  g.userData.beacon = beacon;
  return g;
}

function engineStage(radius, tankCylH, isUpper) {
  const g = new THREE.Group();
  const inter = interstage(radius + 0.04, 0.55);
  g.add(place(inter, 0, 0.55));
  const tk = tank(radius, tankCylH, isUpper ? 1 : 2);
  g.add(place(tk, 0.55, tk.userData.height));
  const eng = engineBell(isUpper ? 0.28 : 0.4, isUpper ? 0.55 : 0.8, 0.85, 0.35);
  const engTop = 0.55 + tk.userData.height;
  g.add(place(eng, engTop, eng.userData.height));
  const engBottom = engTop + eng.userData.height;
  // exhaust plume (hidden until the engine fires)
  const pl = plume(isUpper ? 0.5 : 0.75, isUpper ? 2.4 : 3.4);
  pl.position.y = -(engBottom + 0.15);
  pl.visible = false;
  g.add(pl);
  const light = new THREE.PointLight(0xff9944, 0, 80, 2);
  light.position.y = -(engBottom + 0.5);
  g.add(light);
  if (!isUpper) {
    const f = fins(radius + 0.05, 3);
    f.position.y = -(engTop - 1.0);
    g.add(f);
  }
  g.userData.height = engBottom;
  g.userData.plume = pl;
  g.userData.light = light;
  return g;
}

function plume(radius, length) {
  const geo = new THREE.ConeGeometry(radius, length, 24, 1, true);
  const count = geo.attributes.position.count;
  const colors = new Float32Array(count * 3);
  const pos = geo.attributes.position;
  for (let i = 0; i < count; i++) {
    const y = pos.getY(i); // +length/2 at base (wide end), -length/2 at tip
    const t = THREE.MathUtils.clamp((y + length / 2) / length, 0, 1); // 1 at base
    colors[i * 3] = 1.0;
    colors[i * 3 + 1] = 0.75 - 0.45 * (1 - t);
    colors[i * 3 + 2] = 0.35 - 0.3 * (1 - t);
  }
  geo.setAttribute('color', new THREE.BufferAttribute(colors, 3));
  const mat = new THREE.MeshBasicMaterial({
    vertexColors: true, transparent: true, opacity: 0.9,
    blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
  });
  const mesh = new THREE.Mesh(geo, mat);
  mesh.rotation.x = Math.PI; // cone tip points down (-Y)
  return mesh;
}

// Build the full rocket. Returns { group, stageGroups, engines, beacon, stackHeight }
// stageGroups[i] corresponds to stages[i] (0 = bottom/booster).
export function buildRocket(stages = STOCK_ROCKET) {
  const group = new THREE.Group();
  const stageGroups = [];
  const engines = [];
  let beacon = null;

  for (let i = 0; i < stages.length; i++) {
    const st = stages[i];
    let sg;
    if (st.capsule) sg = capsuleStage(0.8);
    else sg = engineStage(st === stages[1] ? 0.78 : 0.92, st === stages[1] ? 3.2 : 4.5, st === stages[1]);
    if (st.capsule) beacon = sg.userData.beacon;
    stageGroups.push(sg);
    if (sg.userData.plume) {
      engines.push({ stageIndex: i, plume: sg.userData.plume, light: sg.userData.light, nozzle: null });
    }
  }

  // stack from the nose tip (y=0) downward: stage i's top at -(sum of heights below)
  let acc = 0;
  const heights = stageGroups.map((sg) => sg.userData.height);
  const total = heights.reduce((a, b) => a + b, 0);
  for (let i = stages.length - 1; i >= 0; i--) {
    stageGroups[i].position.y = -acc;
    acc += heights[i];
  }
  for (const sg of stageGroups) group.add(sg);

  return { group, stageGroups, engines, beacon, stackHeight: total };
}

// Procedural fallback satellite (used only if the GLB fails to load).
export function buildFallbackSatellite() {
  const g = new THREE.Group();
  const body = new THREE.Mesh(new THREE.BoxGeometry(2.4, 1.6, 1.2),
    new THREE.MeshStandardMaterial({ color: 0xccccdd, metalness: 0.6, roughness: 0.4 }));
  g.add(body);
  const panelGeo = new THREE.BoxGeometry(0.15, 2.6, 3.4);
  const panelMat = new THREE.MeshStandardMaterial({ color: 0x2244aa, metalness: 0.5, roughness: 0.4, emissive: 0x112244, emissiveIntensity: 0.4 });
  const p1 = new THREE.Mesh(panelGeo, panelMat); p1.position.x = -2.6; g.add(p1);
  const p2 = new THREE.Mesh(panelGeo, panelMat); p2.position.x = 2.6; g.add(p2);
  const dish = new THREE.Mesh(new THREE.CylinderGeometry(0.5, 0.5, 0.15, 24),
    new THREE.MeshStandardMaterial({ color: 0xeeeeee, roughness: 0.5 }));
  dish.rotation.x = Math.PI / 2; dish.position.y = 1.2; g.add(dish);
  return g;
}
