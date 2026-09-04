#!/usr/bin/env bash
# Builds YARG-VR.dll. Assumes ./setup-libs.sh was run at least once
# (or point GameLibs at any folder with the required DLLs).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
LIBS="${GameLibs:-$ROOT/libs}"

command -v dotnet >/dev/null 2>&1 || {
  echo "dotnet SDK not found. Install .NET SDK 8: https://dot.net/v1/dotnet-install.sh"; exit 1;
}

dotnet build "$ROOT/YARG.VR.csproj" -c Release -p:GameLibs="$LIBS"

OUT="$ROOT/bin/Release/YARG-VR.dll"
test -f "$OUT" || { echo "Build did not produce $OUT"; exit 1; }

echo
echo "==> $OUT"
echo "    Install: copy YARG-VR.dll AND openvr_api.dll into  <YARG>/Mods/"
