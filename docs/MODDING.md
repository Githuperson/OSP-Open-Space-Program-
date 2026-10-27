# Adding content with data only

## A new part
Add an object to any `game/data/parts/*.json` (or create a new file there; all `*.json` are loaded). Minimal liquid engine:

```json
{ "id": "eng_my", "name": "My Engine", "category": "Engines", "description": "…",
  "dryMass": 800, "cost": 900,
  "shape": { "kind": "cylinder", "radius": 0.625, "length": 1.5, "radiusTop": 0.625, "radiusBottom": 0.45 },
  "nodes": [ { "id": "top",    "kind": "stack", "position": [0, 0.75, 0],  "direction": [0, 1, 0],  "size": 1 },
             { "id": "bottom", "kind": "stack", "position": [0, -0.75, 0], "direction": [0, -1, 0], "size": 1 } ],
  "engine": { "thrustVac": 300000, "ispVac": 320, "ispAtm": 270, "gimbalDeg": 2,
              "nozzlePosition": [0, -0.75, 0], "thrustDirection": [0, 1, 0], "plumeLength": 4, "plumeRadius": 0.45 },
  "visual": { "elements": [ { "type": "lathe", "material": "engine", "segments": 32,
                              "profile": [[-0.75, 0.45], [-0.3, 0.25], [0.2, 0.25], [0.35, 0.625], [0.75, 0.625]] } ] } }
```
* **Units**: metres, kilograms, newtons, seconds. Part frame: `+Y` is the nose, origin at the centre of the shape.
* **Node kinds**: `stack` (mates end-to-end with a node of the same `size`: 0 = 0.625 m, 1 = 1.25 m, 2 = 2.5 m, 3 = 3.75 m); `surface` (a radially-mounted part's mounting face, direction pointing at the surface it mounts on); `socket` (a fixed receptacle that accepts a `surface` node, e.g. the outer face of a radial decoupler).
* **Modules** (all optional, combine freely): `engine`, `decoupler` (`cutNode`, `separationImpulse`), `command` (`crewCapacity`, `probe`), `reactionWheel`, `rcs`, `solar`, `parachute`, `leg`, `aeroSurface`; `resources` (`Propellant`, `SolidFuel`, `Monopropellant`, `ElectricCharge`; amounts in kg, EC in units); `acceptsRadial`, `blocksFlow`, `crashTolerance` (m/s), `jointStrength` (N), `jointTorque` (N·m), `aero` coefficients.
* **Visual elements**: `lathe` (profile `[y, r]` pairs bottom→top), `cylinder`, `cone`, `box`, `sphere`; `material` ∈ `white grey dark orange red yellow metal engine gold heatshield glass solar chute pad steel` (add more in `Materials.cs`); optional `hinge {pivot, axis, angleDeg, driver: leg|solar}` animates with deployment; `tag: canopy|pack` for parachutes.
* The loader validates everything (`PartCatalog.Add`): ids unique, positive masses, valid node kinds, engine data, decoupler cut node exists. A mistake produces a clear error at startup instead of a silent failure.

## A new celestial body
Add to `game/data/system.json` with `parentId`, `mu` (m³/s²), `radius`, `rotationPeriod`, `orbitDef` (a, e, i, lan, argPeriapsis, mean anomaly at t = 0), optional `atmosphere` and `terrain` (seed, amplitude, frequencies, palette, `hasOcean`, `craterStrength`) and `launchSites`. The sphere of influence is derived. The map, prediction, warp limits and renderer pick it up automatically.

## Example vehicles
Save a design in the assembly building; copy `…/designs/<name>.osp-vehicle.json` from the user data directory into `game/data/vehicles/` to ship it as an example.

## Tests for new content
`PartCatalog` validation runs in every test that loads the catalog. For a new engine consider adding a Δv-vs-Tsiolkovsky assertion like `DeltaVMatchesTsiolkovsky`.
