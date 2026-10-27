#!/usr/bin/env bash
# Builds the C# assembly and launches the game directly (no editor UI). Pass --editor to open the Godot editor instead.
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"
require_dotnet; require_godot
dotnet build "$GAME_DIR/OSP.csproj" --nologo -c Debug >/dev/null
if [[ "${1:-}" == "--editor" ]]; then
  exec "$GODOT_BIN" --editor --path "$GAME_DIR"
else
  exec "$GODOT_BIN" --path "$GAME_DIR" "$@"
fi
