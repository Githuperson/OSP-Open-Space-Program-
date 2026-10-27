#!/usr/bin/env bash
# Exports a macOS .app (universal: Apple Silicon + Intel) to ./build/OSP.zip using the "macOS" preset in game/export_presets.cfg.
# Requires the Godot export templates (Editor > Manage Export Templates) and the .NET SDK.
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"
require_dotnet; require_godot
mkdir -p "$REPO_ROOT/build"
OUT="$REPO_ROOT/build/OSP.zip"
if [[ -e "$OUT" ]]; then
  echo "note: $OUT already exists and will be replaced."
fi
# Godot needs the C# solution built once so that the export can reference the assembly.
dotnet build "$GAME_DIR/OSP.csproj" --nologo -c ExportRelease >/dev/null 2>&1 || dotnet build "$GAME_DIR/OSP.csproj" --nologo -c Release
"$GODOT_BIN" --headless --path "$GAME_DIR" --export-release "macOS" "$OUT"
echo "Exported: $OUT   (unzip and move OSP.app to /Applications; on first launch use right-click > Open if Gatekeeper complains)"
