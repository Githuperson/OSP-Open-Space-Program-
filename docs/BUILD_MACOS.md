# Building and running on macOS

Verified by the author: the Core library, tests, the Godot project build and the game running under Godot 4.3 .NET on Linux. **Not verified on macOS** (no Mac available) — the commands below are the standard Godot/.NET procedure and the scripts wrap them.

## 1. Requirements
| Tool | Version | Install |
|---|---|---|
| macOS | 11+ (Apple Silicon or Intel) | – |
| .NET SDK | 8.0 or newer | `brew install --cask dotnet-sdk` or <https://dotnet.microsoft.com/download> |
| Godot **.NET** editor | 4.3 stable | `brew install --cask godot-mono` or <https://godotengine.org/download/macos> (choose ".NET") |
| Xcode command-line tools | any | `xcode-select --install` (git, codesign) |

`./scripts/setup-mac.sh` checks all of this without changing anything.

## 2. Run from source
```bash
./scripts/test.sh          # simulation tests, ~5 s, needs only .NET
./scripts/run.sh           # builds game/OSP.csproj then launches the game
./scripts/run.sh --editor  # opens the Godot editor on game/project.godot
```
or in the editor: **Project → Open** `game/project.godot`, click **Build** (🔨) once, **Play** (▶). The first Godot start imports assets (a few seconds).

If your Godot is installed elsewhere: `GODOT="/path/to/Godot.app/Contents/MacOS/Godot" ./scripts/run.sh`.

## 3. Export a distributable app (universal: arm64 + x86_64)
1. Open the editor once and install the export templates: **Editor → Manage Export Templates → Download and Install** (4.3 *.NET* templates).
2. `./scripts/export-mac.sh` → produces `build/OSP.zip` containing `OSP.app` (ad-hoc signed by Godot's exporter; `build/` is git-ignored).
   Equivalent manual command: `godot --headless --path game --export-release "macOS" ../build/OSP.zip`.
3. Unzip, drag `OSP.app` to Applications. Because it is not notarised, the first launch needs **right-click → Open**.
For a smaller Apple-Silicon-only build change `binary_format/architecture` to `"arm64"` in `game/export_presets.cfg` (or in the editor: Project → Export → macOS).

If the exporter reports a problem with `export_presets.cfg`, recreate the preset in the editor (Project → Export → Add… → macOS), set *Include filter* to `data/*.json, data/*/*.json` (the game data are plain JSON files that must be packaged) and export from there.

## 4. Performance on 8 GB Apple Silicon
* Default quality is **Medium**; **Settings → Graphics quality** and **3D render resolution** (50–100 %) trade detail for speed. Low quality halves terrain detail, disables most shadows and samples the atmosphere less.
* Memory: terrain chunks are capped (~30 MB of meshes), parts are shared meshes, trajectory prediction runs at 4 Hz. Typical working set well under 1 GB.
* The renderer is Godot's Forward+ (Metal via MoltenVK on macOS).

## 5. Troubleshooting
| Symptom | Fix |
|---|---|
| `dotnet: command not found` | install the SDK; open a new Terminal |
| Godot shows *"Failed to load .NET assemblies"* / script not found | click **Build** in the editor (or `./scripts/build.sh`); make sure you installed the **.NET** edition, not the standard one |
| `error NU1101 … Godot.NET.Sdk` | first build needs internet to restore NuGet packages; check proxy/VPN |
| macOS says the app is damaged / unidentified developer | `xattr -dr com.apple.quarantine /Applications/Godot_mono.app` (or right-click → Open) |
| Export fails with *"No export template found"* | install export templates (step 3.1); versions must match 4.3 |
| Export fails with codesign errors | In Project → Export → macOS set *Codesign* to *Disabled* (and run unsigned with right-click → Open) |
| Black window or very low FPS | lower *3D render resolution* and quality; ensure the GPU supports Metal (any Mac since 2012) |
| Buttons do nothing in the VAB | the 3D viewport must have focus; click once inside the window |
| Want to reset everything | delete `~/Library/Application Support/Godot/app_userdata/OSP - Open Space Program/` (this removes your saves) |
