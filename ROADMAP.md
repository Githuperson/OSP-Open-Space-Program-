# Roadmap and status

Legend: ✅ working and tested · 🟡 partial · ⬜ planned. Nothing marked ✅ is a stub.

## Milestone 1 — Playable foundation: **complete**
| Requirement | Status | Evidence |
|---|---|---|
| Start menu | ✅ | `game/scripts/Menu/MainMenu.cs` |
| 3D rocket-building scene, distinct attachable 3D parts, vehicle hierarchy | ✅ | `Editor/EditorScene*.cs`; `--selftest=editor` places parts through real mouse events |
| Mass / thrust / fuel / TWR / Δv calculations | ✅ | `VehicleAnalysis`; test `DeltaVMatchesTsiolkovsky` |
| Launch transition, controllable vessel, gravity + thrust | ✅ | `FlightScene`; `--selftest=flight` (key presses → ignition → climb) |
| Small celestial system, stable orbital insertion | ✅ | `system.json`; test `RocketReachesStableOrbit` (75 × 140 km) |
| Readable flight UI with simulation-derived telemetry | ✅ | HUD, navball, orbit panel (no hard-coded values) |
| Save / load of designs (and complete flights) | ✅ | `SaveTests` (round trip, continuation, 11 corruption cases) |
| macOS build procedure | 🟡 | documented + scripted (`docs/BUILD_MACOS.md`); **not executed on a Mac** |

## Milestone 2 — Core KSP-style flight loop: **mostly complete**
| Item | Status |
|---|---|
| Multiple engines, fuel consumption, mass change | ✅ (6 liquid + 2 solid engines) |
| Staging and booster separation | ✅ |
| Atmospheric flight and aerodynamic forces | ✅ (per-part drag with occlusion, fins, control surfaces, transonic rise) |
| Orbit prediction, Ap/Pe, map view | ✅ |
| Basic manoeuvre nodes | ✅ numeric editor; 🟡 no draggable 3D handles |
| Time acceleration (physics 4×, rails to 100 000×) with safety stops | ✅ |
| Landing and recovery | ✅ legs, parachutes, crashes, recovery summary screen (no funds yet) |
| Persistent spacecraft state | ✅ |

## Milestone 3 — Expanded spacecraft systems: **partly done**
✅ reaction wheels, RCS, landing legs, parachutes, electric power (solar/battery/eclipse), structural failure, collision damage, more part families (35 parts).
🟡 terrain/atmosphere quality (procedural, good enough to fly and land; no real clouds/water waves).
⬜ aerodynamic heating and re-entry damage · fuel lines & crossfeed priorities · fairings & cargo bays · struts · engine spool-up and ignition limits · part-to-part collision during staging.

## Milestone 4 — Exploration: **foundations in place**
✅ four bodies, SoI transitions, patched-conic transfer prediction (a Lumen encounter is unit-tested), Hohmann helper in Core.
⬜ more planets/moons, interplanetary transfer planner (porkchop/phase-angle helper) · docking ports, rendezvous tools (target-relative navball modes, closest-approach markers) · science instruments & experiments by biome (`TerrainModel.Biome` exists) · crew assignment, probes beyond power draw.

## Milestone 5 — Full sandbox
⬜ career mode, funds, contracts, research tree (`techNode` field and `ProgressSave` are reserved) · expanded catalogue · better procedural terrain (erosion, craters, biomes textures) · tutorials · audio & polished effects · modding packages · performance work (GPU-instanced vegetation-free terrain is already cheap; next: chunk meshes on a shared index buffer with LOD stitching, async prediction thread).

## Known limitations (also shown in-game under *Feature status*)
* Ocean is a firm surface (no buoyancy; splashdown is as hard as ground, tolerance ×2.5).
* Orbit prediction ignores thrust (it predicts the *current* coast); the "predicted impact" warning is hidden while engines burn.
* One launch site (Pioneer Field); fixed morning start time.
* Joint-failure thresholds are game-balance values, not engineering data; there is no vibration/part-to-part collision.
* Radial parts do not shield each other aerodynamically.
* Map orbit lines are 1-pixel lines; manoeuvre nodes are edited numerically.
* Audio is not implemented.
* The macOS build/export is documented but unverified (see README).

## Backlog priorities (next sessions)
1. Execute and fix the macOS build on real hardware; add a CI job (GitHub Actions macOS runner) running `./scripts/test.sh` and a headless Godot import.
2. Fuel lines + decoupler crossfeed rules.
3. Docking ports + target-relative SAS modes + rendezvous aids.
4. Re-entry heating.
5. Science instruments and the career loop.
