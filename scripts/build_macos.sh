#!/usr/bin/env bash
# Builds and exports Open Space Program for macOS from Terminal.
# Requires: Godot 4.4.1 .NET (mono) editor, .NET SDK 8, and Godot 4.4.1 .NET export templates.
# Usage:  scripts/build_macos.sh [path/to/Godot.app/Contents/MacOS/Godot]
set -euo pipefail
cd "$(dirname "$0")/.."
GODOT="${1:-${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}}"
if [ ! -x "$GODOT" ]; then echo "Godot .NET not found at '$GODOT'. Pass its path as the first argument." >&2; exit 1; fi
command -v dotnet >/dev/null || { echo "dotnet (.NET SDK 8) is not on PATH." >&2; exit 1; }

echo "== Running simulation tests"
dotnet test tests/OSP.Tests
echo "== Building C# assemblies"
dotnet build OSP.csproj -c ExportRelease
echo "== Importing project"
"$GODOT" --headless --path . --import
echo "== Headless smoke test (launch to orbit)"
"$GODOT" --headless --path . -- --smoke-test
echo "== Exporting macOS app"
mkdir -p build/macos
"$GODOT" --headless --path . --export-release "macOS" build/macos/OpenSpaceProgram.zip
echo "Done: build/macos/OpenSpaceProgram.zip (unzip to get Open Space Program.app)"
