# Open Space Program (OSP)

**THIS IS MADE BY AI.** This project was made by AI for testing purposes. Feel free to copy it, edit it or do anything with it; it has no license.

OSP is an original 3D spaceflight sandbox inspired by the depth of Kerbal Space Program. You assemble rockets from data-driven 3D parts and launch them from the planet Aster. Physics carries them to orbit, to Aster's moons, and to other planets of the Calyx system. All names, models, data and code are original.

- **Engine:** Godot 4.4.1 (.NET edition), C# on .NET 8.
- **Simulation:** a custom double-precision core in `src/Core`, independent of Godot and unit-tested.
- **Platforms:** macOS, with Apple Silicon first (Metal) and Intel supported (Vulkan via MoltenVK). Linux and Windows work too.
- **Online access:** none. The game runs completely offline.

## What works now

| Area | Status |
|---|---|
| Vehicle editor | 49 parts with distinct procedural 3D meshes. Stack-node snapping, surface attachment, radial symmetry (1–8), angle snap, rotation, pick-up/move, duplicate, delete, undo/redo. Structure tree, staging editor, and live mass, cost, Δv per stage (vacuum and sea level), TWR, CoM and CoT. Save, load, duplicate, rename and delete blueprints. |
| Rocket physics | 6-DOF rigid body with inertia tensor. Each engine has its own thrust and Isp(pressure), and its own fuel use with changing mass. Gimbal steering, reaction wheels and RCS. Per-part drag with stack shielding, fin lift, control surfaces and parachutes. Terrain and ocean contact with crash tolerance. Joint-load breakup. |
| Orbits | Universal-variable Kepler propagation and patched conics with SOI transitions. Ap/Pe, period, inclination and eccentricity. Maneuver nodes with burn-time estimates. Rails time warp up to 100000× with altitude and atmosphere restrictions; physics warp 2–4×. |
| Planetary system | Star Calyx; planets Cindra, Aster (atmosphere, oceans) and Ferrus (thin atmosphere); moons Tessar, Vesk and Ket. Procedural LOD terrain is the same function used for collision. |
| Flight UI | Navball with prograde, retrograde, normal, radial, maneuver and target markers. Telemetry, orbit data, staging stack, resources, Δv, SAS modes, map view with predicted trajectories and node editor, science panel, recovery. |
| Staging and docking | Auto-staging; boosters and stages separate into independent vessels; fuel lines with asparagus draining; fairings; docking and undocking. |
| Progression | Sandbox, Science (tech tree) and Career (funds, contracts, part costs, recovery refunds). Experiments depend on body, situation and biome. |
| Saves | Versioned, validated JSON: blueprints, full flight state, progression, settings and key bindings. |

### Not done yet (see `docs/ROADMAP.md`)

- Re-entry heating.
- Audio.
- Wheels and rovers.
- Cargo bays.
- Tutorials.
- Simulating unloaded vessels inside an atmosphere. They are removed, and the game tells you when that happens.

## Requirements (macOS)

1. **Godot 4.4.1 .NET**, from https://godotengine.org/download/archive/4.4.1-stable/. Get the *.NET* build for macOS (universal), then move `Godot_mono.app` to `/Applications`.
2. **.NET SDK 8.0**, from https://dotnet.microsoft.com/download/dotnet/8.0. Use the Arm64 installer on Apple Silicon and x64 on Intel. Check it with `dotnet --version` in Terminal.
3. **Export templates** (only for building a `.app`): Godot editor → *Editor → Manage Export Templates → Download and Install* (4.4.1 mono).

## Run from the editor

1. Open Godot_mono.app → *Import* → select this folder's `project.godot`.
2. Press **Build** (the hammer icon, top right) once, then **Run** (F5).

## Build, test and export from Terminal

```bash
cd OSP-Open-Space-Program-
dotnet test tests/OSP.Tests                       # 59 simulation tests, no Godot needed
GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot
"$GODOT" --headless --path . --import             # first run: import assets
"$GODOT" --path .                                 # play
"$GODOT" --headless --path . -- --smoke-test      # automated launch-to-orbit check (exit code 0 = pass)
scripts/build_macos.sh "$GODOT"                   # tests + smoke test + export build/macos/OpenSpaceProgram.zip
```

After exporting, unzip the archive, then right-click **Open Space Program.app** → *Open*. The first launch needs this because the app is ad-hoc signed and not notarised.

User data (vessels, saves, settings) is stored in `~/Library/Application Support/Godot/app_userdata/Open Space Program/`.

## How to play (quick start)

1. Choose **New Sandbox Game**, then click **Load…** and pick *Pathfinder Orbiter*. You can also build your own rocket: pod, then tanks, then an engine.
2. Click **LAUNCH**, then press **Z** for full throttle and **Space** to stage.
3. Above about 1 km, tip gently east with **D** and follow prograde. Cut the throttle (**X**) when the apoapsis reaches about 80 km.
4. Coast to the apoapsis (**.** warps time), then burn prograde until the periapsis is above 70 km. **M** opens the map; click the orbit to add a maneuver node.
5. **F1** shows every control. Key bindings can be changed in Settings.

## Troubleshooting

- **"dotnet not found" / build fails in the editor:** install .NET SDK 8, restart Godot, and make sure `dotnet --info` lists an 8.0 SDK. On Apple Silicon use the Arm64 SDK.
- **"No export template found":** install the 4.4.1 **mono** export templates (see Requirements).
- **App "is damaged" or won't open:** run `xattr -cr "Open Space Program.app"`, then right-click → Open.
- **Low frame rate on 8 GB Macs:** Settings → Graphics quality *Low*, 3D render scale 70%, shadows off.
- **Broken part/save files** are reported in the menu and the log rather than crashing; invalid entries are skipped.

## Project layout

```
project.godot, OSP.csproj     Godot project and C# project
src/Core/                     engine-independent simulation (math, orbits, parts, vehicles, flight, persistence, science)
src/Game/                     Godot layer (rendering, editor, flight, map, UI, menus, smoke test)
data/                         system.json, parts/*.json, vessels/*.json, techtree.json, contracts.json
shaders/, scenes/             shaders and scene files
tests/OSP.Tests/              xUnit tests (compile src/Core directly)
docs/                         PHYSICS.md, SAVE_FORMAT.md, ROADMAP.md, MODDING.md
scripts/                      build_macos.sh, run_tests.sh
```

Generated output (`.godot/`, `bin/`, `obj/`, `build/`) is git-ignored. The stock vessels are regenerated with `OSP_WRITE_STOCK=1 dotnet test tests/OSP.Tests --filter StockCraft`. Part meshes are built procedurally at runtime from `data/parts/*.json`, so there are no binary model assets to regenerate.

## What was verified

These checks were run on Linux, in the development container:

- `dotnet test`: all simulation tests pass.
- The Godot 4.4.1 .NET build compiles.
- The headless smoke test passes: menu → editor → launch → gravity turn → orbit (73 × 110 km) → 50× rails warp with no change to the orbit → map maneuver prediction → save/load.
- The game renders correctly under Vulkan (lavapipe) via Xvfb.

**A macOS build has not been produced or run in that environment.** The macOS steps above are the standard Godot .NET workflow, but they are untested there.
