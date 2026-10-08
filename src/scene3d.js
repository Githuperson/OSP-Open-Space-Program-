// 3D rendering: star, planets (real downloaded textures), atmosphere shells,
// procedural terrain near the ground, launch pad, the rocket, a satellite
// (internet GLB model), trajectory lines and the camera rig.

import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import {
  BODIES, BODY_BY_ID, STAR, HOME, LAUNCH_SITE,
  bodyPos, bodyVel, surfacePos, surfaceDir, childrenOf,
} from './universe.js';
import { buildRocket, buildFallbackSatellite } from './rocket.js';
import { DEG, clamp, TAU } from './util.js';

const DOME_R = 6e11;

// --- GLSL: simplex noise (Ashima) ------------------------------------------
const SIMPLEX_GLSL = /* glsl */`
vec3 mod289(vec3 x){return x - floor(x*(1.0/289.0))*289.0;}
vec4 mod289(vec4 x){return x - floor(x*(1.0/289.0))*289.0;}
vec4 permute(vec4 x){return mod289(((x*34.0)+1.0)*x);}
vec4 taylorInvSqrt(vec4 r){return 1.79284291400159 - 0.85373472095314*r;}
float snoise(vec3 v){
  const vec2 C = vec2(1.0/6.0, 1.0/3.0);
  const vec4 D = vec4(0.0, 0.5, 1.0, 2.0);
  vec3 i = floor(v + dot(v, C.yyy));
  vec3 x0 = v - i + dot(i, C.xxx);
  vec3 g = step(x0.yzx, x0.xyz);
  vec3 l = 1.0 - g;
  vec3 i1 = min(g.xyz, l.zxy);
  vec3 i2 = max(g.xyz, l.zxy);
  vec3 x1 = x0 - i1 + C.xxx;
  vec3 x2 = x0 - i2 + C.yyy;
  vec3 x3 = x0 - D.yyy;
  i = mod289(i);
  vec4 p = permute(permute(permute(i.z + vec4(0.0, i1.z, i2.z, 1.0)) + i.y + vec4(0.0, i1.y, i2.y, 1.0)) + i.x + vec4(0.0, i1.x, i2.x, 1.0));
  float n_ = 0.142857142857;
  vec3 ns = n_ * D.wyz - D.xzx;
  vec4 j = p - 49.0*floor(p*ns.z*ns.z);
  vec4 x_ = floor(j*ns.z);
  vec4 y_ = floor(j - 7.0*x_);
  vec4 x = x_*ns.x + ns.yyyy;
  vec4 y = y_*ns.x + ns.yyyy;
  vec4 h = 1.0 - abs(x) - abs(y);
  vec4 b0 = vec4(x.xy, y.xy);
  vec4 b1 = vec4(x.zw, y.zw);
  vec4 s0 = floor(b0)*2.0 + 1.0;
  vec4 s1 = floor(b1)*2.0 + 1.0;
  vec4 sh = -step(h, vec4(0.0));
  vec4 a0 = b0.xzyw + s0.xzyw*sh.xxyy;
  vec4 a1 = b1.xzyw + s1.xzyw*sh.zzww;
  vec3 p0 = vec3(a0.xy, h.x);
  vec3 p1 = vec3(a0.zw, h.y);
  vec3 p2 = vec3(a1.xy, h.z);
  vec3 p3 = vec3(a1.zw, h.w);
  vec4 norm = taylorInvSqrt(vec4(dot(p0,p0), dot(p1,p1), dot(p2,p2), dot(p3,p3)));
  p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
  vec4 m = max(0.6 - vec4(dot(x0,x0), dot(x1,x1), dot(x2,x2), dot(x3,x3)), 0.0);
  m = m*m;
  return 42.0*dot(m*m, vec4(dot(p0,x0), dot(p1,x1), dot(p2,x2), dot(p3,x3)));
}
`;

const TERRAIN_GLSL = SIMPLEX_GLSL + /* glsl */`
uniform float uAmp; uniform float uFreq; uniform float uSeed; uniform float uFade;
uniform vec2 uFlatCenter; uniform float uFlatR; uniform float uSolid;
uniform vec3 uSunDir; uniform vec3 uLow; uniform vec3 uHigh; uniform vec3 uRock; uniform vec3 uSnow;
varying float vH; varying vec3 vN; varying float vAlpha;
#include <logdepthbuf_pars_vertex>
float terrainH(vec2 xz){
  vec3 p = vec3(xz * uFreq, uSeed);
  float amp = 1.0, freq = 1.0, sum = 0.0;
  for (int i = 0; i < 5; i++) { sum += amp * snoise(p * freq); amp *= 0.5; freq *= 2.0; }
  float raw = sum * 1.4 * uAmp;
  float dFlat = distance(xz, uFlatCenter);
  float flat = 1.0 - smoothstep(uFlatR - 80.0, uFlatR, dFlat);
  raw *= flat;
  return max(raw, 0.0);
}
void main(){
  float h0 = terrainH(position.xy);
  float e = 400.0;
  float hx = terrainH(position.xy + vec2(e, 0.0));
  float hz = terrainH(position.xy + vec2(0.0, e));
  vec3 pos = position;
  pos.z += h0;
  vec3 n = normalize(vec3(hx - h0, hz - h0, e));
  vN = normalize(normalMatrix * n);
  vH = h0;
  vAlpha = mix(uFade * smoothstep(0.0, 120.0, h0), uFade, uSolid);
  gl_Position = projectionMatrix * modelViewMatrix * vec4(pos, 1.0);
  #include <logdepthbuf_vertex>
}
`;

const TERRAIN_FRAG = /* glsl */`
uniform vec3 uSunDir; uniform vec3 uLow; uniform vec3 uHigh; uniform vec3 uRock; uniform vec3 uSnow;
varying float vH; varying vec3 vN; varying float vAlpha;
#include <logdepthbuf_pars_fragment>
void main(){
  vec3 base = mix(uLow, uHigh, smoothstep(0.0, 900.0, vH));
  float slope = 1.0 - clamp(vN.z, 0.0, 1.0);
  base = mix(base, uRock, smoothstep(0.4, 0.8, slope));
  base = mix(base, uSnow, smoothstep(1200.0, 2000.0, vH));
  float sun = max(dot(normalize(vN), uSunDir), 0.0);
  vec3 col = base * (0.35 + 0.75 * sun);
  gl_FragColor = vec4(col, vAlpha);
  #include <logdepthbuf_fragment>
}
`;

const ATMO_SHELL_GLSL = /* glsl */`
uniform vec3 uColor; uniform float uIntensity;
varying vec3 vN; varying vec3 vW;
#include <logdepthbuf_pars_vertex>
void main(){
  vN = normalize(normalMatrix * normal);
  vec4 wp = modelMatrix * vec4(position, 1.0);
  vW = wp.xyz;
  gl_Position = projectionMatrix * viewMatrix * wp;
  #include <logdepthbuf_vertex>
}
`;
const ATMO_SHELL_FRAG = /* glsl */`
uniform vec3 uColor; uniform float uIntensity;
varying vec3 vN; varying vec3 vW;
#include <logdepthbuf_pars_fragment>
void main(){
  vec3 viewDir = normalize(cameraPosition - vW);
  float fres = pow(1.0 - abs(dot(viewDir, vN)), 3.0);
  gl_FragColor = vec4(uColor * fres * uIntensity, fres);
  #include <logdepthbuf_fragment>
}
`;

const SKYDOME_GLSL = /* glsl */`
varying vec3 vWorld;
#include <logdepthbuf_pars_vertex>
void main(){
  vec4 wp = modelMatrix * vec4(position, 1.0);
  vWorld = wp.xyz;
  gl_Position = projectionMatrix * viewMatrix * wp;
  #include <logdepthbuf_vertex>
}
`;
const SKYDOME_FRAG = /* glsl */`
uniform vec3 uPrimaryPos; uniform float uAtmoR; uniform float uSurfaceR; uniform float uHasAtmo;
uniform vec3 uHorizon; uniform vec3 uZenith; uniform vec3 uSunDir; uniform vec3 uSunColor;
varying vec3 vWorld;
#include <logdepthbuf_pars_fragment>
void main(){
  vec3 col = vec3(0.0);
  float alpha = 0.0;
  float d = distance(vWorld, uPrimaryPos);
  if (uHasAtmo > 0.5 && d < uAtmoR) {
    vec3 viewDir = normalize(vWorld - cameraPosition);
    vec3 up = normalize(cameraPosition - uPrimaryPos);
    float heightFrac = clamp((d - uSurfaceR) / max(uAtmoR - uSurfaceR, 1.0), 0.0, 1.0);
    float density = exp(-heightFrac * 4.0);
    vec3 base = mix(uZenith, uHorizon, pow(clamp(dot(viewDir, up), 0.0, 1.0), 0.55));
    float sunDot = max(dot(viewDir, uSunDir), 0.0);
    vec3 glow = uSunColor * (pow(sunDot, 350.0) * 3.0 + pow(sunDot, 24.0) * 0.35);
    col = base * (0.2 + 1.1 * density) + glow;
    alpha = clamp(1.0 - heightFrac, 0.0, 1.0);
    alpha = alpha * alpha * 0.95;
  }
  gl_FragColor = vec4(col, alpha);
  #include <logdepthbuf_fragment>
}
`;

// atmosphere tint per body (sky colors)
const SKY_TINT = {
  kerra: { horizon: new THREE.Color(0x8fb8e8), zenith: new THREE.Color(0x1d4e89), glow: new THREE.Color(0xffe8c8) },
  violet: { horizon: new THREE.Color(0xc9a0e8), zenith: new THREE.Color(0x4a2d78), glow: new THREE.Color(0xffe0f0) },
  fasius: { horizon: new THREE.Color(0xe8c890), zenith: new THREE.Color(0x6b4a2d), glow: new THREE.Color(0xfff0d8) },
  oceana: { horizon: new THREE.Color(0x90c8e8), zenith: new THREE.Color(0x1d5e89), glow: new THREE.Color(0xffe8c8) },
  aurelia: { horizon: new THREE.Color(0xe8d8a0), zenith: new THREE.Color(0x6b5a2d), glow: new THREE.Color(0xfff0d8) },
  nerune: { horizon: new THREE.Color(0x7090e0), zenith: new THREE.Color(0x1d2d78), glow: new THREE.Color(0xd8e8ff) },
  urelle: { horizon: new THREE.Color(0xa0d8e8), zenith: new THREE.Color(0x2d6b78), glow: new THREE.Color(0xe8ffff) },
};

function makeGlowTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 256;
  const ctx = c.getContext('2d');
  const g = ctx.createRadialGradient(128, 128, 0, 128, 128, 128);
  g.addColorStop(0, 'rgba(255,240,210,1)');
  g.addColorStop(0.25, 'rgba(255,210,140,0.55)');
  g.addColorStop(0.6, 'rgba(255,160,80,0.18)');
  g.addColorStop(1, 'rgba(255,140,60,0)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, 256, 256);
  const tex = new THREE.CanvasTexture(c);
  return tex;
}

export class Scene3D {
  constructor(container) {
    this.container = container;
    this.renderer = new THREE.WebGLRenderer({ antialias: true, logarithmicDepthBuffer: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(container.clientWidth, container.clientHeight);
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    container.appendChild(this.renderer.domElement);

    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(60, container.clientWidth / container.clientHeight, 0.5, 1e13);

    this.camMode = 'orbital'; // orbital | chase
    this.camAz = 0;
    this.camEl = 0.35;
    this.camDist = 60;
    this._chasePos = new THREE.Vector3();
    this._chaseLook = new THREE.Vector3();

    this.texLoader = new THREE.TextureLoader();
    this._loadSky();
    this._buildLights();
    this._buildStar();
    this._buildSkyDome();
    this.planetMeshes = {};
    this._buildPlanets();
    this._buildTerrain();
    this._buildLaunchPad();
    this.rocket = null;
    this.debrisMeshes = new Map();
    this.satellite = null;
    this._buildSatellite();
    this.trajLine = null;
    this.trajLineNode = null;
    this._buildTrajLines();
    this._camTarget = new THREE.Vector3();

    window.addEventListener('resize', () => this.onResize());
  }

  onResize() {
    const w = this.container.clientWidth, h = this.container.clientHeight;
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(w, h);
  }

  // ------------------------------------------------------------------ setup

  _loadSky() {
    this.texLoader.load('textures/stars.jpg', (tex) => {
      tex.mapping = THREE.EquirectangularReflectionMapping;
      tex.colorSpace = THREE.SRGBColorSpace;
      this.scene.background = tex;
    });
  }

  _buildLights() {
    this.starLight = new THREE.PointLight(0xfff1dd, 2.5e20, 0, 2);
    this.scene.add(this.starLight); // star sits at the origin
    this.scene.add(new THREE.AmbientLight(0x334466, 0.4));
  }

  _buildStar() {
    const b = STAR;
    const tex = this.texLoader.load(b.tex);
    tex.colorSpace = THREE.SRGBColorSpace;
    const mesh = new THREE.Mesh(
      new THREE.SphereGeometry(b.radius, 48, 32),
      new THREE.MeshBasicMaterial({ map: tex })
    );
    this.scene.add(mesh);
    const glow = new THREE.Sprite(new THREE.SpriteMaterial({
      map: makeGlowTexture(), transparent: true, blending: THREE.AdditiveBlending, depthWrite: false,
    }));
    glow.scale.set(b.radius * 9, b.radius * 9, 1);
    this.scene.add(glow);
  }

  _buildSkyDome() {
    const geo = new THREE.SphereGeometry(DOME_R, 32, 24);
    this.skyMat = new THREE.ShaderMaterial({
      vertexShader: SKYDOME_GLSL, fragmentShader: SKYDOME_FRAG,
      uniforms: {
        uPrimaryPos: { value: new THREE.Vector3() },
        uAtmoR: { value: 0 }, uSurfaceR: { value: 1 }, uHasAtmo: { value: 0 },
        uHorizon: { value: new THREE.Color(0x8fb8e8) },
        uZenith: { value: new THREE.Color(0x1d4e89) },
        uSunDir: { value: new THREE.Vector3(-1, 0, 0) },
        uSunColor: { value: new THREE.Color(0xffe8c8) },
      },
      side: THREE.BackSide, transparent: true, depthWrite: false,
    });
    this.scene.add(new THREE.Mesh(geo, this.skyMat));
  }

  _buildPlanets() {
    for (const b of BODIES) {
      if (b.type === 'star' || !b.parent) continue;
      const entry = { body: b, spin: 0 };
      const tex = this.texLoader.load(b.tex);
      tex.colorSpace = THREE.SRGBColorSpace;
      const mat = new THREE.MeshStandardMaterial({ map: tex, roughness: 0.9, metalness: 0 });
      if (b.tint && b.tint !== 0xffffff) mat.color.set(b.tint);
      if (b.nightTex) {
        const night = this.texLoader.load(b.nightTex);
        night.colorSpace = THREE.SRGBColorSpace;
        mat.emissiveMap = night;
        mat.emissive = new THREE.Color(0xffffff);
        mat.emissiveIntensity = 0.85;
      }
      const mesh = new THREE.Mesh(new THREE.SphereGeometry(b.radius, 64, 48), mat);
      mesh.rotation.order = 'YXZ';
      this.scene.add(mesh);
      entry.mesh = mesh;

      if (b.cloudTex) {
        const ctex = this.texLoader.load(b.cloudTex);
        const cloud = new THREE.Mesh(
          new THREE.SphereGeometry(b.radius * 1.004, 48, 32),
          new THREE.MeshStandardMaterial({
            color: 0xffffff, alphaMap: ctex, transparent: true, opacity: 0.9,
            roughness: 1, depthWrite: false, polygonOffset: true, polygonOffsetFactor: -1,
          })
        );
        this.scene.add(cloud);
        entry.clouds = cloud;
      }

      if (b.atmo) {
        const tint = SKY_TINT[b.id] || SKY_TINT.kerra;
        const shell = new THREE.Mesh(
          new THREE.SphereGeometry(b.radius * 1.012, 48, 32),
          new THREE.ShaderMaterial({
            vertexShader: ATMO_SHELL_GLSL, fragmentShader: ATMO_SHELL_FRAG,
            uniforms: {
              uColor: { value: tint.horizon.clone() },
              uIntensity: { value: 1.6 },
            },
            side: THREE.FrontSide, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false,
          })
        );
        this.scene.add(shell);
        entry.atmo = shell;
      }

      if (b.rings) {
        const ringTex = this.texLoader.load('textures/saturn_ring_alpha.png');
        const ring = new THREE.Mesh(
          new THREE.RingGeometry(b.radius * 1.35, b.radius * 2.15, 160, 4),
          new THREE.MeshBasicMaterial({
            color: 0xd8c49a, alphaMap: ringTex, transparent: true, side: THREE.DoubleSide,
            opacity: 0.9, depthWrite: false,
          })
        );
        ring.rotation.x = (b.axialTilt * DEG) - Math.PI / 2;
        this.scene.add(ring);
        entry.ring = ring;
      }

      this.planetMeshes[b.id] = entry;
    }
  }

  _buildTerrain() {
    const size = 60e3;
    const geo = new THREE.PlaneGeometry(size, size, 128, 128);
    this.terrainMat = new THREE.ShaderMaterial({
      vertexShader: TERRAIN_GLSL, fragmentShader: TERRAIN_FRAG,
      uniforms: {
        uAmp: { value: 900 }, uFreq: { value: 1 / 9000 }, uSeed: { value: 7.3 },
        uFade: { value: 1 }, uFlatCenter: { value: new THREE.Vector2(0, 0) },
        uFlatR: { value: 600 }, uSolid: { value: 0 },
        uSunDir: { value: new THREE.Vector3(-1, 0, 0) },
        uLow: { value: new THREE.Color(0x3d6b3f) }, uHigh: { value: new THREE.Color(0x7a9a5a) },
        uRock: { value: new THREE.Color(0x6b6259) }, uSnow: { value: new THREE.Color(0xe8ecef) },
      },
      transparent: true, depthWrite: false,
    });
    this.terrain = new THREE.Mesh(geo, this.terrainMat);
    this.terrain.rotation.x = -Math.PI / 2; // local +Z (displacement) becomes +Y
    this.terrain.visible = false;
    this.scene.add(this.terrain);
  }

  _buildLaunchPad() {
    const g = new THREE.Group();
    const concrete = new THREE.MeshStandardMaterial({ color: 0x8a8f96, roughness: 0.9 });
    const red = new THREE.MeshStandardMaterial({ color: 0xb03a2e, roughness: 0.7 });
    const pad = new THREE.Mesh(new THREE.CylinderGeometry(160, 170, 12, 40), concrete);
    pad.position.y = -6;
    g.add(pad);
    const inner = new THREE.Mesh(new THREE.CylinderGeometry(60, 60, 4, 32), new THREE.MeshStandardMaterial({ color: 0x6a6f76, roughness: 0.9 }));
    inner.position.y = 2;
    g.add(inner);
    // tower: 4 legs + cross beams + crane arm
    for (let i = 0; i < 4; i++) {
      const a = (i / 4) * Math.PI * 2 + Math.PI / 4;
      const leg = new THREE.Mesh(new THREE.BoxGeometry(5, 58, 5), red);
      leg.position.set(Math.cos(a) * 55, 17, Math.sin(a) * 55);
      g.add(leg);
      const leg2 = new THREE.Mesh(new THREE.BoxGeometry(5, 58, 5), concrete);
      leg2.position.set(Math.cos(a) * 70, 17, Math.sin(a) * 70);
      g.add(leg2);
      for (let k = 1; k <= 3; k++) {
        const beam = new THREE.Mesh(new THREE.BoxGeometry(20, 4, 4), concrete);
        beam.position.set(Math.cos(a) * 62.5, k * 16, Math.sin(a) * 62.5);
        beam.rotation.y = -a;
        g.add(beam);
      }
    }
    const top = new THREE.Mesh(new THREE.BoxGeometry(60, 5, 60), red);
    top.position.y = 40;
    g.add(top);
    const arm = new THREE.Mesh(new THREE.BoxGeometry(46, 4, 4), red);
    arm.position.set(30, 42, 0);
    g.add(arm);
    // hold-down clamps
    for (let i = 0; i < 3; i++) {
      const a = (i / 3) * Math.PI * 2;
      const clampM = new THREE.Mesh(new THREE.BoxGeometry(6, 10, 6), red);
      clampM.position.set(Math.cos(a) * 14, 5, Math.sin(a) * 14);
      g.add(clampM);
    }
    // warning lights
    for (let i = 0; i < 4; i++) {
      const a = (i / 4) * Math.PI * 2;
      const l = new THREE.Mesh(new THREE.SphereGeometry(1.6, 10, 10),
        new THREE.MeshStandardMaterial({ color: 0xff2222, emissive: 0xff2222, emissiveIntensity: 3 }));
      l.position.set(Math.cos(a) * 160, 4, Math.sin(a) * 160);
      g.add(l);
    }
    this.launchPad = g;
    this.scene.add(g);
  }

  _buildSatellite() {
    // SAT-1: a real GLB model (downloaded) in a Kerra orbit; rendezvous target.
    const satBody = {
      id: 'sat1', name: 'SAT-1', parent: 'kerra',
      a: HOME.radius + 850e3, e: 0.003, inc: 28, raan: 45, argp: 0, m0: 200,
      mu: 0, radius: 6,
    };
    satBody.period = TAU * Math.sqrt((satBody.a ** 3) / HOME.mu);
    this.satBody = satBody;
    const marker = new THREE.Mesh(
      new THREE.SphereGeometry(30, 12, 12),
      new THREE.MeshStandardMaterial({ color: 0x66ffcc, emissive: 0x33ffaa, emissiveIntensity: 2 })
    );
    this.scene.add(marker);
    this.satMarker = marker;
    const loader = new GLTFLoader();
    loader.load('models/satellite.glb', (gltf) => {
      const obj = gltf.scene;
      const box = new THREE.Box3().setFromObject(obj);
      const size = box.getSize(new THREE.Vector3());
      const center = box.getCenter(new THREE.Vector3());
      const scale = 12 / Math.max(size.x, size.y, size.z);
      obj.position.sub(center);
      obj.scale.setScalar(scale);
      this.scene.add(obj);
      this.satellite = obj;
    }, undefined, () => {
      const obj = buildFallbackSatellite();
      this.scene.add(obj);
      this.satellite = obj;
    });
  }

  _buildTrajLines() {
    const mk = (color, dashed) => {
      const mat = dashed
        ? new THREE.LineDashedMaterial({ color, transparent: true, opacity: 0.95, dashSize: 4e4, gapSize: 2.5e4 })
        : new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.9 });
      const line = new THREE.Line(new THREE.BufferGeometry(), mat);
      line.frustumCulled = false;
      this.scene.add(line);
      return line;
    };
    this.trajLine = mk(0x58b6ff, false);
    this.trajLineNode = mk(0xffa726, true);
    this.trajLine.visible = true;
    this.trajLineNode.visible = false;
  }

  setTrajectory(pred, predNode) {
    const setGeo = (line, pred) => {
      const pts = [];
      if (pred) {
        for (const seg of pred.segments) {
          for (const p of seg.points) pts.push(p.r.x, p.r.y, p.r.z);
        }
        if (pred.impact) {
          const r = pred.impact.r;
          pts.push(r.x, r.y, r.z);
        }
      }
      if (pts.length < 6) { line.visible = false; return; }
      line.geometry.dispose();
      line.geometry = new THREE.BufferGeometry();
      line.geometry.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
      line.visible = true;
    };
    setGeo(this.trajLine, pred);
    setGeo(this.trajLineNode, predNode);
    this.trajLineNode.visible = !!predNode;
    if (this.trajLineNode.visible && this.trajLineNode.material.isLineDashedMaterial) {
      this.trajLineNode.computeLineDistances();
    }
    if (this.trajLine.material.isLineDashedMaterial) this.trajLine.computeLineDistances();
  }

  // ------------------------------------------------------------------ rocket

  attachRocket(rocket) {
    this.rocket = rocket;
    this.scene.add(rocket.group);
  }

  // sync staging: detach jettisoned stages into debris objects
  syncStaging(sim) {
    if (!this.rocket) return;
    const V = sim.vessel;
    for (let i = 0; i < this.rocket.stageGroups.length; i++) {
      const sg = this.rocket.stageGroups[i];
      const intact = i >= V.stageIndex;
      const attached = sg.parent === this.rocket.group;
      if (intact && !attached) {
        // re-attach (e.g. after revert): restore the local stack transform
        this.rocket.group.add(sg);
        sg.position.set(0, sg.userData.stackY ?? 0, 0);
        sg.quaternion.identity();
        sg.scale.setScalar(1);
      } else if (!intact && attached) {
        // jettison: capture world transform BEFORE detaching, hand to debris
        sg.updateWorldMatrix(true, false);
        const worldPos = new THREE.Vector3().setFromMatrixPosition(sg.matrixWorld);
        const worldQuat = new THREE.Quaternion().setFromRotationMatrix(sg.matrixWorld);
        this.rocket.group.remove(sg);
        sg.position.copy(worldPos);
        sg.quaternion.copy(worldQuat);
        this.scene.add(sg);
        const debris = sim.debris.find((d) => d.stageIndex === i && !d.obj);
        if (debris) {
          debris.obj = sg;
          this.debrisMeshes.set(debris, sg);
        }
      }
    }
  }

  updateRocket(sim, realDt) {
    if (!this.rocket) return;
    const V = sim.vessel;
    this.rocket.group.position.copy(V.r);
    this.rocket.group.quaternion.copy(V.q);
    // plumes + engine lights
    const active = new Set(V.activeEngines().map((e) => e.stage));
    let anyThrust = false;
    for (const eng of this.rocket.engines) {
      const on = active.has(eng.stageIndex) && V.throttle > 0.02 && V.stages[eng.stageIndex].fuelLeft > 0;
      const target = on ? V.throttle : 0;
      eng.plume.userData.t = (eng.plume.userData.t ?? 0) + (target - (eng.plume.userData.t ?? 0)) * Math.min(1, realDt * 12);
      const t = eng.plData?.t ?? eng.plume.userData.t;
      eng.plume.visible = t > 0.02;
      const flick = 0.85 + 0.15 * Math.sin(sim.time * 60 + eng.stageIndex * 7);
      eng.plume.scale.set(1, (0.4 + t * flick) * 1.0 + t * 0.6, 1);
      eng.plume.material.opacity = 0.9 * t;
      eng.light.intensity = 60 * t * t;
      if (t > 0.02) anyThrust = true;
    }
    // beacon blink
    if (this.rocket.beacon && V.gearLights) {
      this.rocket.beacon.material.emissiveIntensity = (Math.sin(sim.time * 5) > 0 ? 2.2 : 0.25);
    }
    return anyThrust;
  }

  // ------------------------------------------------------------------ update

  update(sim, realDt, preview, previewNode) {
    const t = sim.ut;
    const V = sim.vessel;

    // planets
    for (const b of BODIES) {
      if (b.type === 'star' || !b.parent) continue;
      const e = this.planetMeshes[b.id];
      const p = bodyPos(b, t);
      e.mesh.position.copy(p);
      e.spin = TAU * (t / b.rotPeriod);
      e.mesh.rotation.y = e.spin;
      e.mesh.rotation.x = b.axialTilt * DEG;
      if (e.clouds) {
        e.clouds.position.copy(p);
        e.clouds.rotation.y = e.spin * 1.05;
        e.clouds.rotation.x = b.axialTilt * DEG;
      }
      if (e.atmo) e.atmo.position.copy(p);
      if (e.ring) e.ring.position.copy(p);
    }

    // sky dome uniforms
    const primary = V.primary || STAR;
    const pp = bodyPos(primary, t);
    this.skyMat.uniforms.uPrimaryPos.value.copy(pp);
    if (primary.atmo) {
      this.skyMat.uniforms.uHasAtmo.value = 1;
      this.skyMat.uniforms.uAtmoR.value = primary.radius + primary.atmo.top;
      this.skyMat.uniforms.uSurfaceR.value = primary.radius;
      const tint = SKY_TINT[primary.id] || SKY_TINT.kerra;
      this.skyMat.uniforms.uHorizon.value.copy(tint.horizon);
      this.skyMat.uniforms.uZenith.value.copy(tint.zenith);
      this.skyMat.uniforms.uSunColor.value.copy(tint.glow);
    } else {
      this.skyMat.uniforms.uHasAtmo.value = 0;
    }
    this.skyMat.uniforms.uSunDir.value.copy(pp).negate().normalize();

    // terrain
    this._updateTerrain(sim);

    // launch pad (rotates with Kerra)
    const padDir = surfaceDir(HOME, LAUNCH_SITE.lat, LAUNCH_SITE.lon, t);
    const padPos = padDir.clone().multiplyScalar(HOME.radius).add(bodyPos(HOME, t));
    this.launchPad.position.copy(padPos);
    this.launchPad.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), padDir);

    // rocket + staging
    this.syncStaging(sim);
    this.updateRocket(sim, realDt);

    // debris
    for (const d of sim.debris) {
      const m = this.debrisMeshes.get(d);
      if (m) {
        m.position.copy(d.r);
        m.rotation.set(d.rot.x, d.rot.y, d.rot.z);
      }
    }
    // prune debris meshes whose debris is gone
    for (const [d, m] of [...this.debrisMeshes.entries()]) {
      if (!sim.debris.includes(d)) {
        this.scene.remove(m);
        this.debrisMeshes.delete(d);
      }
    }

    // satellite
    const satRel = bodyPos(this.satBody, t);
    this.satMarker.position.copy(satRel);
    if (this.satellite) {
      this.satellite.position.copy(satRel);
      this.satellite.rotation.y += realDt * 0.4;
    }

    // trajectory lines
    this.setTrajectory(preview, previewNode);

    // camera
    this._updateCamera(sim, realDt);
  }

  _updateTerrain(sim) {
    const V = sim.vessel;
    const primary = V.primary;
    if (!primary || primary.type === 'star') { this.terrain.visible = false; return; }
    const rel = V.r.clone().sub(bodyPos(primary, sim.ut));
    const alt = rel.length() - primary.radius;
    if (alt > 30e3 && !V.landed) { this.terrain.visible = false; return; }
    // position the patch under the vessel
    const up = rel.clone().normalize();
    const surfPoint = up.clone().multiplyScalar(primary.radius).add(bodyPos(primary, sim.ut));
    this.terrain.position.copy(surfPoint);
    this.terrain.quaternion.setFromUnitVectors(new THREE.Vector3(0, 0, 1), up);
    // uniforms
    const u = this.terrainMat.uniforms;
    u.uAmp.value = Math.min(900, primary.radius * 0.004);
    u.uFreq.value = 1 / (primary.radius * 0.02);
    u.uSeed.value = (primary.mu % 97) / 7 + 1.3;
    u.uSolid.value = primary.ocean ? 0 : 1;
    u.uFade.value = V.landed ? 1 : clamp((30e3 - alt) / 25e3, 0.15, 1);
    u.uSunDir.value.copy(bodyPos(primary, sim.ut)).negate().normalize();
    if (primary.ocean) {
      u.uLow.value.set(0x2d5a3f); u.uHigh.value.set(0x5a7a4d);
      u.uRock.value.set(0x5a524b); u.uSnow.value.set(0xe8ecef);
    } else {
      u.uLow.value.set(0x4a4038); u.uHigh.value.set(0x6b6156);
      u.uRock.value.set(0x5a524b); u.uSnow.value.set(0xc8ccd0);
    }
    // flatten under the launch pad (only meaningful on Kerra)
    if (primary.id === HOME.id) {
      const padWorld = surfacePos(HOME, LAUNCH_SITE.lat, LAUNCH_SITE.lon, 0, sim.ut);
      const inv = this.terrain.quaternion.clone().invert();
      const local = padWorld.clone().sub(this.terrain.position).applyQuaternion(inv);
      u.uFlatCenter.value.set(local.x, local.y);
      u.uFlatR.value = 500;
    } else {
      u.uFlatR.value = 0.001;
    }
    this.terrain.visible = true;
  }

  _updateCamera(sim, realDt) {
    const V = sim.vessel;
    const shipPos = V.r;
    const target = this._camTarget.copy(shipPos);
    const cam = this.camera;

    if (V.landed) {
      // surface chase: behind and above the ship
      const nose = V.nose();
      const up = V.r.clone().sub(bodyPos(V.primary, sim.ut)).normalize();
      const back = nose.clone().negate().multiplyScalar(this.camDist * 0.7)
        .add(up.clone().multiplyScalar(this.camDist * 0.35));
      const camPos = shipPos.clone().add(back);
      cam.position.lerp(camPos, 1 - Math.pow(0.001, realDt));
      cam.up.copy(up);
      cam.lookAt(target);
      return;
    }

    if (this.camMode === 'chase') {
      const primary = V.primary;
      const relv = V.v.clone().sub(bodyVel(primary, sim.ut));
      const velDir = relv.lengthSq() > 1 ? relv.normalize() : V.nose();
      const up = V.r.clone().sub(bodyPos(primary, sim.ut)).normalize();
      const back = velDir.clone().negate().multiplyScalar(this.camDist * 0.9)
        .add(up.clone().multiplyScalar(this.camDist * 0.28));
      const camPos = shipPos.clone().add(back);
      cam.position.lerp(camPos, 1 - Math.pow(0.001, realDt));
      cam.up.copy(up);
      this._chaseLook.lerp(shipPos.clone().add(velDir.clone().multiplyScalar(this.camDist * 0.2)), 1 - Math.pow(0.001, realDt));
      cam.lookAt(this._chaseLook);
      return;
    }

    // orbital (free) camera around the ship
    const el = clamp(this.camEl, -1.45, 1.45);
    const off = new THREE.Vector3(
      Math.cos(el) * Math.sin(this.camAz),
      Math.sin(el),
      Math.cos(el) * Math.cos(this.camAz)
    ).multiplyScalar(this.camDist);
    cam.position.copy(shipPos).add(off);
    cam.up.set(0, 1, 0);
    cam.lookAt(target);
  }

  render() {
    this.renderer.render(this.scene, this.camera);
  }
}
