# Roadmap and known limitations

## Milestones 1–2: done
- Editor, parts, launch, physics, orbit, flight UI and save/load.
- Engines, staging, aerodynamics, prediction, map, maneuver nodes, time warp, landing/recovery, persistence.

## Milestones 3–4: mostly done
- Done: SAS, RCS, legs, parachutes, power, solar, batteries, structural failure, extra part families, multiple planets and moons, SOI transitions, docking, science, crew and probes.

## Milestone 5: partial
- Done: tech tree, contracts, career funds, mod parts loaded from `user://mods/parts/*.json`.
- Missing:
  - [ ] Tutorials and guided missions.
  - [ ] Audio (engine sound, staging, ambience).

## Next priorities
1. Re-entry heating with ablative heat shields (data already has `maxTemperature`).
2. Audio.
3. Wheels and rovers.
4. Cargo bays with drag occlusion inside fairings.
5. Target rendezvous aids: closest-approach markers and relative-velocity display.
6. Drag-handle maneuver gizmo in the map.
7. Higher-quality terrain: textures and biome maps.
8. Tutorials.

## Known limitations (honest status)
- **No heating:** re-entry heat is not simulated.
- **Unloaded vessels in an atmosphere:** vessels beyond 2.5 km of the active vessel are not simulated in an atmosphere; they are removed, with a message.
- **Patched conics:** encounter search can miss very shallow SOI grazes, and N-body effects are not modelled.
- **Fairings:** a fairing does not shield its payload's drag.
- **Contact geometry:** contact uses bounding points, not mesh colliders.
- **Body rotation:** no axial tilt; all bodies spin about +Y.
- **macOS export:** configured but not tested in that environment (no Mac available there).
