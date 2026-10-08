# OSP — Open Space Program

A KSP-style space flight simulator that runs entirely in the browser: a
procedural rocket, a 13-body planetary system with real textures, patched-conics
orbital physics, a 2D map view, a canvas navball, and a real orbit planner
(Lambert solver + porkchop plot + maneuver nodes).

![stack](https://img.shields.io/badge/three.js-0.186-blue) ![stack](https://img.shields.io/badge/vite-8.3-purple) ![physics](https://img.shields.io/badge/physics-patched%20conics-green)

## Run it

```bash
npm install
npm run dev        # dev server on http://localhost:5173
npm run build      # production build to dist/
npm test           # physics + planner + full launch-to-orbit tests
```

Open the printed URL, dismiss the help screen, throttle up (**Z**), and stage
(**Space**). Reach orbit, visit Munara, or rendezvous with the SAT-1 satellite.

## Controls

| Key | Action |
| --- | --- |
| `Z` / `X` | Throttle 100% / 0% (`Shift`/`Ctrl` to adjust) |
| `Space` | Stage |
| `W` `A` `S` `D` | Pitch / yaw |
| `Q` `E` | Roll |
| `T` | Toggle SAS (stability assist) |
| `,` `.` | Time warp down / up (×1 … ×10000) |
| `M` | Map view (drag = pan, wheel = zoom, click = target) |
| `C` | Camera mode (free orbit / chase) |
| `N` | Add maneuver node |
| `Y` | Cycle target |
| `P` | Orbit planner |
| `G` | Lights |
| `Esc` | Pause |

## Features

- **Real orbital physics** — universal-variable Kepler propagation, patched
  conics with SOI transitions (hysteresis), velocity-Verlet integration,
  rocket equation, staging, exponential atmospheres with drag + dynamic
  pressure (max-Q), aerodynamic stability, reaction-wheel SAS (10 modes),
  maneuver-node auto-burn, time warp with auto-drop, soft landing / crash
  detection, debris from jettisoned stages.
- **Planetary system** — star Solara and 7 planets (Cinder, Kerra, Violet,
  Fasius, Aurelia, Nerune, Urelle) with 4 moons (Munara, Oceana, Grayne,
  Tarn), each on a hierarchical Kepler ephemeris with axial tilt, day/night
  city lights, cloud layers, Saturn-style rings, and Fresnel atmosphere shells.
- **3D rendering (three.js)** — real planet textures (downloaded from public
  solar-system texture packs), an equirectangular starfield, physically-based
  point-light sun, procedural simplex terrain near the surface, a launch pad,
  a GLB satellite model (`public/models/satellite.glb`), trajectory lines
  (current orbit + post-maneuver preview), and a logarithmic depth buffer so
  planetary-scale scenes render correctly.
- **2D map view** — pan/zoom, all body orbits and SOI spheres, Ap/Pe markers,
  maneuver nodes, target line with closest-approach readout, impact markers,
  and click-to-target.
- **Navball** — gnomonic-projected attitude display with prograde/retrograde,
  radial, maneuver-node and target markers.
- **Orbit planner** — universal-variable **Lambert solver** for arbitrary
  transfers (moons and interplanetary), a **porkchop plot** (departure date ×
  flight time, colored by total Δv, click a cell to inspect a window), and a
  **maneuver node editor** with prograde/radial/normal sliders, warp-to-node,
  and "add as node" from any computed transfer.
- **HUD** — time, warp, altitude, velocity, Ap/Pe, time-to-apsides,
  inclination, eccentricity, period, mass, Δv, TWR, throttle, max-Q, stage,
  target info, and a message log.

## The rocket

A 3-stage stack (booster → upper stage → capsule), 14.4 t at liftoff,
TWR ≈ 1.7, ≈ 5460 m/s of Δv. Enough for Kerra orbit with margin, a Munara
transfer (~1900 m/s), or a Violet interplanetary trip (~3000 m/s).

## Physics notes

- The atmosphere **rotates with the planet** — drag and aerodynamic torques
  act on the velocity relative to the local airflow, not the inertial frame.
- Landing/crash checks use the velocity relative to the **rotating surface**.
- Orbits use a **Y-up** convention (ecliptic = XZ plane, +Y = north) shared by
  `universe.js` and `conics.js`.
- Time warp uses adaptive substeps (`~orbit period / 60`, clamped), so warp
  stays accurate near planets and fast in deep space.

## Tests

`npm test` runs three suites (all headless, no browser needed):

1. `test/smoke.mjs` — ephemeris closed orbits, Kepler propagation conservation,
   orbital-element round-trips, Lambert short/long-way solutions, SOI logic,
   trajectory prediction with maneuver nodes, staging/fuel/Δv.
2. `test/planner.mjs` — Lambert transfers to a moon and to another planet,
   porkchop scan coverage and quality, Δv component round-trips.
3. `test/launch.mjs` — a full end-to-end flight: a scripted autopilot launches
   from the pad, gravity-turns, stages (spawning debris), and circularizes into
   a stable Kerra orbit.

## Assets

Planet/sun/star textures are from public GitHub texture packs
(`astrabros/solarsystem-textures-8k`, `homer-jay/solar-system-textures`,
`mrdoob/three.js` examples), downscaled for size. The satellite model is
`kane-exe/satellite-glb`. All assets live in `public/` and are served
as-is; the rocket itself is built procedurally so it can stage and jettison.
