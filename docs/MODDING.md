# Adding parts

Put a JSON file into `data/parts/` (stock) or `user://mods/parts/` (mods), using the same schema as the existing files.

A part needs:
- `id`, `name`, `category`, `mass` (kg), and at least one `mesh` primitive (`lathe`, `cylinder`, `cone`, `box`, `prism`, `sphere`, `torus`, with `material`, `offset`, `rotation`, `repeat`, `group`);
- `attachNodes` and/or `surfaceAttach`.

Optional fields: `resources`, `drag`, `breakingForce`, `crashTolerance`, `tech`.

Behaviour comes from `modules`: Command, Engine, Decoupler, Parachute, LandingLeg, ReactionWheel, RCS, SolarPanel, LiftingSurface, FuelLine, Antenna, Experiment, DockingPort, Jettison.

Invalid parts are skipped and listed on the main menu. The unit tests validate every stock part.
