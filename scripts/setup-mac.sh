#!/usr/bin/env bash
# Checks (and explains how to install) everything needed to build and run OSP on macOS. Changes nothing.
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"
echo "== OSP environment check =="
echo "Architecture : $(uname -m)  (arm64 = Apple Silicon, x86_64 = Intel)"
echo "macOS        : $(sw_vers -productVersion 2>/dev/null || echo 'not macOS')"
require_dotnet; echo ".NET SDK     : $(dotnet --version)"
require_godot;  echo "Godot        : $GODOT_BIN ($("$GODOT_BIN" --version 2>/dev/null))"
if ls "$HOME/Library/Application Support/Godot/export_templates/"*mono* >/dev/null 2>&1 || ls "$HOME/Library/Application Support/Godot/export_templates/"*/macos.zip >/dev/null 2>&1; then
  echo "Export tmpl. : found"
else
  echo "Export tmpl. : NOT found. Only needed for exporting a .app: open the editor, Editor > Manage Export Templates > Download."
fi
echo "OK: you can run ./scripts/test.sh and ./scripts/run.sh"
