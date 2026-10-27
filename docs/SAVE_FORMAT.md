# File formats (JSON, versioned)

- **Blueprint** `*.osp-vessel.json`: `{format:"osp-vessel", version:1, vessel:{name, description, parts:[...], nextId, nextSymmetryGroup}}`.
  Each part is `{id, part, parent(-1=root), parentNode, selfNode, position[3], rotation[x,y,z,w], stage(-1 none), symmetryGroup, resources{id:amount}, fuelLineTarget, thrustLimit}`.
- **Save game** `*.osp-save.json`: `{format:"osp-save", version:1, universalTime, activeVesselId, targetBody, vessels:[{id, name, body, position, velocity, rotation, angularVelocity, pinned, pinnedPosition, pinnedRotation, situation, currentStage, throttle, sas..., nodes, parts (with per-part state)}], progress:{mode, funds, science, unlockedTech, collectedScience, contracts}, launchDesign}`.
  Celestial bodies are fully determined by `universalTime`, so their state is not stored.
- **Settings** `settings.json`: UI scale, quality, render scale, shadows, vsync, volume, mouse, `keyBindings{action:key}`.

## Validation on load

- **Fatal errors:** malformed JSON, a wrong format name, a newer version, duplicate part ids, or parent cycles. These are rejected with a clear error.
- **Repaired with warnings:**
  - unknown parts (removed together with the parts attached below them);
  - out-of-range or NaN resources;
  - invalid rotations;
  - unknown bodies or non-finite vessel states (the vessel is removed);
  - missing sections (defaults are used).
