#!/usr/bin/env bash
# Shared helpers for the OSP scripts. Source this file; it never modifies anything outside the repository.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DIR="$REPO_ROOT/game"

# Locate the Godot .NET ("Mono") editor binary. Override with: GODOT=/path/to/Godot ./scripts/run.sh
find_godot() {
  if [[ -n "${GODOT:-}" && -x "${GODOT}" ]]; then echo "$GODOT"; return 0; fi
  local candidates=(
    "/Applications/Godot_mono.app/Contents/MacOS/Godot"
    "/Applications/Godot.app/Contents/MacOS/Godot"
    "$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot"
    "$(command -v godot-mono 2>/dev/null || true)"
    "$(command -v godot 2>/dev/null || true)"
  )
  for c in "${candidates[@]}"; do
    if [[ -n "$c" && -x "$c" ]]; then echo "$c"; return 0; fi
  done
  return 1
}

require_dotnet() {
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: the .NET SDK is not installed (need 8.0 or newer)." >&2
    echo "       macOS:  brew install --cask dotnet-sdk    (or download from https://dotnet.microsoft.com/download)" >&2
    exit 1
  fi
  local v major
  v="$(dotnet --version)"; major="${v%%.*}"
  if (( major < 8 )); then
    echo "error: .NET SDK $v found, but 8.0 or newer is required." >&2
    exit 1
  fi
}

require_godot() {
  GODOT_BIN="$(find_godot)" || {
    echo "error: Godot 4.3 .NET (Mono) editor not found." >&2
    echo "       macOS:  brew install --cask godot-mono" >&2
    echo "       or download 'Godot 4.3 .NET' from https://godotengine.org/download/macos and put it in /Applications," >&2
    echo "       or set GODOT=/full/path/to/Godot" >&2
    exit 1
  }
  local ver
  ver="$("$GODOT_BIN" --version 2>/dev/null || true)"
  if [[ "$ver" != *mono* && "$ver" != *dotnet* ]]; then
    echo "warning: '$GODOT_BIN' reports version '$ver' which does not look like the .NET edition." >&2
  fi
}
