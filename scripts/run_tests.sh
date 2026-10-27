#!/usr/bin/env bash
# Runs the simulation unit tests and, if GODOT is set, the in-engine smoke test.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test tests/OSP.Tests
if [ -n "${GODOT:-}" ]; then "$GODOT" --headless --path . --import && "$GODOT" --headless --path . -- --smoke-test; fi
