# Architecture

## Guiding decisions

1. **The simulation is a plain .NET library (`src/OSP.Core`) with no Godot dependency.** Everything that decides *what happens* — orbits, forces, fuel, staging, structural failure, saving — lives there, runs headless, and is covered by unit tests that compare against analytic results. Godot only *renders* the state and turns input into control values.
2. **Double precision everywhere authoritative.** Positions, velocities, time and orbital elements are `double`. Godot scene transforms are `float`, so the game uses a **floating origin**: every frame everything is expressed relative to the active vessel (or camera) in `double` and only then converted to `float` (see below).
3. **Own rigid-body physics instead of Godot's physics engine.** Orbital flight needs 1e6 m coordinates, stable time warp, deterministic staging and testable results; a general-purpose engine would make those harder. Godot physics shapes are used where they help: part pick colliders in the assembly scene. (Documented deviation from "integrate with the engine's real-time physics where appropriate": the engine integration point is rendering/UI/input, not dynamics.)
4. **Data-driven content.** Parts (`game/data/parts/*.json`), the planetary system (`system.json`), and example vehicles are data. New engines/tanks/bodies need no engine code (see MODDING.md). Part *models* are declared in the same JSON (lathe profiles, boxes, cylinders, hinged elements) and built into real `ArrayMesh` geometry at runtime.

## Modules (namespaces)

| Module | Location | Responsibility |
|---|---|---|
| Math | `OSP.Core.Mathx` | `Vec3d`, `QuatD`, `Mat3d` (double precision), helpers |
| Orbits | `OSP.Core.Orbits` | `Orbit` (Keplerian elements ↔ state), `KeplerPropagator` (universal variables), `OrbitMath` (v_circ, v_esc, vis-viva, Hohmann, SoI) |
| Bodies | `OSP.Core.Bodies` | `CelestialBody`, `StarSystem` (patched conics), `AtmosphereDef`, `TerrainModel` + `Noise3` (procedural height field = render and collision source of truth) |
| Parts | `OSP.Core.Parts` | `PartDefinition` + modules (engine, decoupler, command, reaction wheel, RCS, solar, parachute, leg, aero surface), `PartCatalog` + validation |
| Vehicles | `OSP.Core.Vehicles` | `VehicleDesign` (tree of `PartInstance`), `AttachGeometry`, `VehicleEditor` (snap candidates, symmetry, subtree move/duplicate), `VehicleValidator`, `StagingPlanner`, `VehicleAnalysis` (mass, TWR, per-stage Δv using the real propulsion code) |
| Flight | `OSP.Core.Flight` | `Craft` (runtime vehicle, resource flow, split/detach), `CraftPhysics` (integration), `Systems/*` (propulsion, control, aero, deployables/power, contact, structure), `StagingController`, `FlightSimulation` (universe, warp, SoI, modes), `TrajectoryPredictor`, `ManeuverNode` |
| Save | `OSP.Core.Save` | versioned JSON for designs, flights, settings; strict validation |
| Game rendering | `game/scripts/Rendering` | `PlanetView` (quadtree LOD terrain + atmosphere), `PartModel`/`MeshFactory` (procedural meshes), materials, sky/sun, effects |
| Game flight | `game/scripts/Flight` | `FlightScene` (loop, floating origin, input), HUD, `NavBall`, map view, `CraftView`, plumes, launch pad |
| Game editor | `game/scripts/Editor` | Vehicle Assembly Building |
| Game UI/state | `game/scripts/UI`, `GameState.cs`, `Bindings.cs`, `Menu` | theme, dialogs, settings, key bindings, scene flow, storage under `user://` |

Dependencies point one way: `game → OSP.Core`; inside Core: `Mathx ← Orbits/Bodies ← Parts ← Vehicles ← Flight ← Save`. There are no cycles.

## Frames and units
All SI (m, s, kg, N, Pa, kg/m³, kg/s). Details in PHYSICS.md. Y is "north"; the ecliptic is the XZ plane. A craft's state is in the **non-rotating body-centred frame of the sphere-of-influence (SoI) body it is currently in**; transitions convert exactly between frames.

## Floating origin (rendering precision)
Planets are 600 000 m in radius and 1.2e10 m from the star. `float` has ~7 digits, so nothing is ever placed in the scene at those coordinates. Each frame `FlightScene.UpdateRender`:
1. takes the active vessel's position as the render origin (the vessel is at the scene origin);
2. computes each body's centre relative to that origin in `double`;
3. terrain chunks are meshes whose vertices are relative to the chunk centre (≤ a few km), and each chunk node's global position is `(bodyRel + R_body · chunkCentre)` computed in `double` and cast to `float` *after* the large terms cancel;
4. bodies farther than 200 km from the camera's nearest surface point are scaled about the camera with a logarithmic compression of distance. Uniform scaling about the eye preserves apparent size, and the compression is monotonic so occlusion order is preserved. This keeps everything inside the near/far range (0.3 m … 4·10⁶ m) that Godot's frustum/light culling can handle.

## Terrain level of detail and memory
`PlanetView` renders each body as six cube faces of a quadtree. Chunks are 48×48 quads (+ skirts to hide cracks). A node splits when the camera is closer than `SplitFactor × chunkSize` (2.2 default, 1.6 low, 3.0 high); nodes behind the horizon or outside the view cone (deeper than level 4) are skipped; chunk meshes are built on worker threads from the *same* `TerrainModel` used for collision, installed at most 3 per frame, and cached with eviction (≈ 320–420 chunks, ~30 MB). If a chunk is not ready, the nearest ready ancestor is drawn, so there are no holes. Only the bodies near the camera need detail; far bodies are a handful of coarse chunks.

## Simulation modes (performance)
Each craft is in one of: **Physics** (fixed 0.02 s step, full forces), **Rails** (analytic Kepler propagation, any step size), **Landed** (locked to the rotating surface, zero cost). The active vessel is Physics whenever it is thrusting, in atmosphere, touching ground or at 1×–4× warp; unpowered vacuum coasting at ≥5× warp uses Rails. Separated stages are Physics only while near the player in atmosphere/contact; otherwise Rails. Debris beyond 24 pieces is culled.

## Threading
Only terrain chunk generation uses worker threads (pure functions over immutable data). The simulation and all Godot node access run on the main thread.

## Testing and diagnostics
* `tests/OSP.Core.Tests` — 76 tests: analytic orbital mechanics, propulsion, delta-v vs Tsiolkovsky, staging order/momentum, aerodynamics, parachute terminal velocity, landing/crash, structural failure, warp stability, save/load and corruption cases, an orbital-insertion flight, example vehicles.
* Godot-side self tests (developer options after `--`): `--selftest=editor` (scripted mouse placement/undo/save through real input events), `--selftest=flight` (key presses → ignition, climb, F5 quicksave, pause, map), `--selftest=planet` (terrain/atmosphere screenshots), `--osp-scene=… --osp-shot=file@frames`, `--osp-autopilot`, `--osp-save=<example>`, `--osp-map`.
