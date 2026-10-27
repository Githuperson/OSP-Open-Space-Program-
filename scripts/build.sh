#!/usr/bin/env bash
# Compiles the C# projects (Core library, tests, and the Godot game assembly). Build output goes to bin/ obj/ .godot/ (git-ignored).
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"
require_dotnet
cd "$REPO_ROOT"
echo "== building OSP.Core and tests =="
dotnet build OSP.Core.sln --nologo -c "${CONFIG:-Debug}"
echo "== building the Godot game assembly =="
dotnet build "$GAME_DIR/OSP.csproj" --nologo -c "${CONFIG:-Debug}"
echo "Build finished."
