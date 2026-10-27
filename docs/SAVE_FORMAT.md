# Save formats

All persistence is JSON (UTF-8, camelCase), written under Godot's `user://` directory (macOS: `~/Library/Application Support/Godot/app_userdata/OSP - Open Space Program/`):

| Content | Path | Kind tag |
|---|---|---|
| Vehicle designs | `designs/<name>.osp-vehicle.json` | `osp-vehicle` |
| Saved flights | `saves/<name>.osp-save.json` | `osp-save` |
| Settings & key bindings | `settings.json` | – |
| Screenshots (F12) | `screenshots/*.png` | – |

No accounts or network are used.

## Versioning rules
Each file has `kindTag` and an integer `formatVersion`. Loading **rejects** (with a readable `DataException`, never a half-built object): wrong kind, `formatVersion` newer than the build understands, versions below 1, malformed JSON, non-finite numbers, wrong-length arrays, unknown parts/bodies/resources, dangling parent references, duplicate ids, more or fewer than one root, bad enum values, negative resource amounts, an unknown active craft id, negative universal time. Amounts above a tank's capacity are clamped. To change the format: bump `CurrentVersion`, keep a migration for the previous version in `SaveSerializer`, and add a test.

## Vehicle file (`formatVersion` 1)
```json
{ "kindTag": "osp-vehicle", "formatVersion": 1,
  "design": { "name": "Pathfinder-1", "description": "", "nextPartId": 12, "nextSymmetryGroup": 2,
    "parts": [
      { "id": 1, "partId": "pod_aster", "parentId": -1, "mode": 0, "stage": -1 },
      { "id": 3, "partId": "tank_s2", "parentId": 2, "mode": 1, "parentNode": "bottom", "childNode": "top" },
      { "id": 9, "partId": "fin_delta", "parentId": 6, "mode": 2, "childNode": "srf", "angleDeg": 90, "offsetY": -1.0, "symmetryGroup": 1 } ] } }
```
`mode`: 0 root, 1 node-to-node (stack/socket), 2 radial surface mount at `angleDeg` (around the parent's +Y) and `offsetY`. Only attachment parameters are stored; positions and rotations are recomputed on load, so a design cannot contain an inconsistent geometry. The loader also runs the full validator (node sizes, free nodes, radial acceptance).

## Flight save (`formatVersion` 1)
```json
{ "kindTag": "osp-save", "formatVersion": 1, "name": "quicksave", "savedAtUtc": "…", "universalTime": 276.0,
  "activeCraftId": 1, "nextCraftId": 3, "progress": { "gameMode": "sandbox", … },
  "crafts": [ { "id": 1, "name": "Pathfinder-1", "bodyId": "aldara", "mode": 1,        // 0 physics, 1 rails, 2 landed
      "position": [x,y,z], "velocity": [x,y,z],                 // body-centred inertial frame, metres, m/s
      "orientation": [w,x,y,z], "angularVelocity": [x,y,z],     // craft -> inertial quaternion; craft-frame rad/s
      "landedLocalPosition": […], "landedLocalOrientation": […], // used when mode == 2
      "currentStage": 0, "throttle": 0, "sasEnabled": true, "sasMode": 1, "rcsEnabled": false,
      "parts": [ { "id": 1, "partId": "pod_aster", "parentId": -1, "mode": 0, "stage": -1,
                   "position": [x,y,z], "rotation": [w,x,y,z],       // craft frame (explicit: split craft keep their frame)
                   "resources": { "ElectricCharge": 80 }, "ignited": false, "flamedOut": false,
                   "legsDown": false, "legDeploy": 0, "panelsOut": false, "solarDeploy": 0, "chute": 0, "chuteProgress": 0 } ],
      "maneuvers": [ { "id": 1, "time": 855.0, "prograde": 650, "normal": 0, "radial": 0 } ] } ] }
```
All simulation state needed to continue deterministically is stored (the tests continue a restored flight for 5 s and compare it with the original within 1 m). Not stored (re-derived): mass properties, aerodynamic exposure, gimbal deflection, plume intensity.

`progress` reserves fields for career/science (funds, science, unlocked tech nodes, completed objectives); the fields round-trip but nothing uses them yet.

## Settings
`graphicsQuality` (0 low, 1 medium, 2 high), `renderScale`, `mouseSensitivity`, `fieldOfView`, `vsync`, `fullscreen`, `keyBindings` (action id → Godot key name). Corrupt settings are ignored with a warning and defaults are used.
