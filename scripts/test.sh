#!/usr/bin/env bash
# Runs the automated simulation tests (xUnit). Needs only the .NET SDK, not Godot. Works offline after the first restore.
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"
require_dotnet
cd "$REPO_ROOT"
dotnet test OSP.Core.sln --nologo "$@"
