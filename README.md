# OSP — Open Space Program

**An original 3D spaceflight sandbox** built with **Godot 4.3 (.NET)** and **C#**, inspired by the freedom and depth of games like Kerbal Space Program. All names, models, textures (procedural) and code are original.

> **This is made by AI for testing purposes** — feel free to copy it, edit it or do anything with it; it has no license.

You build rockets from distinct 3D parts, launch them from a pad on a rotating planet with an atmosphere, stage boosters and engines, fly a gravity turn into orbit, plan manoeuvres in a patched-conic map, and land (or crash) on procedurally generated terrain — on `Aldara`, its moon `Lumen`, or (with the right Δv) the desert world `Cinder`.

![status](https://img.shields.io/badge/status-milestone%201%20%2B%20most%20of%20milestone%202-blue)

## What actually works (and what does not)

Everything on this list is implemented, exercised by automated tests and/or run in the real game; see [ROADMAP.md](ROADMAP.md) and the in-game **Feature status** screen for the complete, honest matrix.

| Area | Working now |
|---|---|
| Assembly | 3D building scene, 35 data-driven parts in 11 categories, stack/radial/socket nodes with snapping, symmetry (×2…×8), undo/redo, move/duplicate/delete, hierarchy tree, staging editor, live mass/TWR/Δv, save/load/duplicate/rename/delete designs |
| Flight physics | Custom double-precision rigid body: thrust (pressure-dependent Isp), gimbal, drag + lift per part, reaction wheels, RCS, SAS (hold/prograde/…/manoeuvre), gravity, terrain collision, crash damage, joint failure, landing legs, parachutes, solar/battery power |
| Staging | Decouplers (stack + radial), solid and liquid engines, independent simulation of separated stages, momentum-conserving separation |
| Orbits | Universal-variable Kepler propagation, Ap/Pe/period/inclination, sphere-of-influence transitions (patched conics), trajectory prediction, manoeuvre nodes with burn-time estimate, time warp 1×–100 000× with automatic safety stops |
| World | Star **Vesper**, planets **Aldara** (ocean, atmosphere, launch site) and **Cinder**, moon **Lumen**; procedural cube-sphere terrain with level of detail, atmospheric scattering, starfield |
| UI | Aerospace HUD, navball, telemetry from the simulation, map view, pause/settings/key rebinding, controls reference |
| Persistence | Versioned JSON saves of designs and complete flights (positions, velocities, resources, stage state, manoeuvre nodes) |

**Not implemented yet** (not faked anywhere): docking, fairings/cargo bays/fuel lines, science instruments, crew/careers/contracts, tutorials, audio, re-entry heating damage, more planets. They are tracked in the roadmap.

## Quick start on macOS (Apple Silicon or Intel)

You need **Terminal**, the **.NET SDK 8.0+**, and the **Godot 4.3 .NET ("Mono") editor**. No account, no internet needed to play.

```bash
# 1. Install the tools (skip what you already have)
brew install --cask dotnet-sdk
brew install --cask godot-mono          # or download "Godot 4.3 .NET" from https://godotengine.org/download/macos

# 2. Get the source and check your environment
git clone https://github.com/Githuperson/OSP-Open-Space-Program-.git && cd OSP-Open-Space-Program-
./scripts/setup-mac.sh                  # read-only check, tells you what is missing

# 3. Run the automated tests (needs only the .NET SDK)
./scripts/test.sh

# 4. Play
./scripts/run.sh                        # builds, then launches the game
./scripts/run.sh --editor               # open the project in the Godot editor instead
```

Prefer clicking? Open Godot, **Import** `game/project.godot`, press **Build** (hammer icon, top right) once, then **Play**.
First launch: if macOS blocks Godot, right-click the app → **Open**.

Exporting a standalone `.app` and troubleshooting: see [docs/BUILD_MACOS.md](docs/BUILD_MACOS.md).

> **Honesty note about verification.** The development environment for this repository was Linux. The 3D game was built and run there with the real Godot 4.3 .NET editor (software Vulkan renderer, screenshots and scripted-input self tests), and the simulation has 76 passing unit/integration tests. The **macOS build and export steps were not executed** by the author (no Mac available), so they are documented and scripted but unverified. Godot 4.3 and .NET 8 officially support macOS on Apple Silicon and Intel.

## Your first flight (5 minutes)

1. Main menu → **Example Vehicles** → **Pathfinder-1** (opens in the Vehicle Assembly Building).
2. Look at the right panel: stage list, TWR 1.41, Δv ≈ 4 980 m/s in vacuum — enough for orbit. Press **LAUNCH**.
3. On the pad press **Space** (ignite stage 1) and **Z** (full throttle).
4. At ~800 m tap **S**/**W** to tip the nose slightly east (heading 90°), then press **2** (SAS *Prograde*) so the rocket follows its velocity through a gravity turn.
5. Keep burning until the **apoapsis** (top-right orbit panel) is ~90 km, cut the throttle with **X**, coast.
6. Shortly before apoapsis, point prograde (SAS is already prograde) and burn again until **periapsis** is above 70 km. You are in orbit. Press **M** for the map and plan a manoeuvre node with **+ at Ap**.

*(The automated test `RocketReachesStableOrbit` does exactly this with a script and reaches a 75 × 140 km orbit.)* A ready-made orbiting save is under **Load Saved Flight → Pathfinder 1 in orbit**.

### Default controls
Pitch **W/S**, yaw **A/D**, roll **Q/E**, throttle **Shift/Ctrl**, full/cut **Z/X**, stage **Space**, SAS **T** (modes **1–8**), RCS **R** (translate **H N I K J L**), gear **G**, panels **U**, parachutes **P**, map **M**, warp **. , /**, camera **right-drag / wheel / V**, quicksave/load **F5/F9**, controls help **F1**. All flight keys can be rebound in *Settings*.

## Repository layout

```
OSP.Core.sln                 .NET solution for the engine-independent simulation + tests
src/OSP.Core/                The simulation (no Godot dependency): math, orbits, bodies, parts, vehicles, flight, save
tests/OSP.Core.Tests/        xUnit tests (analytic physics checks, integration flights, save/load)
game/                        The Godot 4.3 .NET project (open game/project.godot)
  data/system.json           Planetary system definition (bodies, orbits, atmospheres, terrain)
  data/parts/*.json          Part catalogue (edit/add parts without touching code)
  data/vehicles, data/saves  Example designs and an orbiting example flight
  scripts/                   Rendering (planets, parts, plumes), editor, flight scene, HUD, map, UI
  shaders/                   Terrain, atmosphere scattering, plume, navball, starfield shaders
scripts/                     build / test / run / export helpers for macOS
docs/                        ARCHITECTURE, PHYSICS, SAVE_FORMAT, MODDING, BUILD_MACOS
```

## Documentation
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — modules, data flow, floating origin, performance decisions
- [docs/PHYSICS.md](docs/PHYSICS.md) — every equation, unit, frame, integrator and approximation
- [docs/SAVE_FORMAT.md](docs/SAVE_FORMAT.md) — versioned save and design formats
- [docs/MODDING.md](docs/MODDING.md) — adding parts, bodies and example vehicles with data only
- [docs/BUILD_MACOS.md](docs/BUILD_MACOS.md) — setup, export, troubleshooting
- [ROADMAP.md](ROADMAP.md) — milestones, status, known limitations

## Regenerating generated content
* **Part models** are built at runtime from the `visual` blocks in `game/data/parts/*.json` — nothing to regenerate.
* **Terrain** is generated procedurally from `game/data/system.json` (seed + parameters) every run; nothing is stored.
* **Example vehicles and the example orbit save** are produced by tests: `OSP_WRITE_EXAMPLES=1 ./scripts/test.sh` rewrites `game/data/vehicles/*.json` and `game/data/saves/*.json` from the definitions in `tests/OSP.Core.Tests/ExampleVehicleTests.cs`.

## Dependencies (all free, no network at runtime)
| What | Version | Used for |
|---|---|---|
| Godot Engine, .NET edition | 4.3 stable | rendering, UI, input, scenes |
| .NET SDK | 8.0+ | compiling C#; run tests |
| `Godot.NET.Sdk` (NuGet) | 4.3.0 | restored automatically by `dotnet build` |
| xUnit, Microsoft.NET.Test.Sdk (NuGet) | 2.9 / 17.11 | tests only |

The first `dotnet build`/`test` needs internet to restore these NuGet packages once; afterwards everything works offline.
