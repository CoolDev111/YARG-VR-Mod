#!/usr/bin/env bash
# Downloads every reference library needed to build YARG-VR into ./libs/
#
#   MelonLoader.dll, 0Harmony.dll     <- MelonLoader.x64.zip (net472 variant)
#   UnityEngine*.dll, Unity.InputSystem.dll <- YARG v0.15.0 Windows release (YARG_Data/Managed)
#
# Usage:  ./setup-libs.sh [YARG_VERSION]     (default: 0.15.0)
set -euo pipefail

YARG_VERSION="${1:-0.15.0}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
LIBS="$ROOT/libs"
mkdir -p "$LIBS"

echo "==> MelonLoader (latest x64)"
curl -L --fail -o /tmp/melonloader.zip \
  "https://github.com/LavaGang/MelonLoader/releases/latest/download/MelonLoader.x64.zip"
rm -rf /tmp/melonloader && mkdir -p /tmp/melonloader
unzip -q /tmp/melonloader.zip -d /tmp/melonloader
cp /tmp/melonloader/MelonLoader/net472/MelonLoader.dll "$LIBS/"
cp /tmp/melonloader/MelonLoader/net472/0Harmony.dll "$LIBS/" 2>/dev/null || true

echo "==> YARG v$YARG_VERSION (Windows, only YARG_Data/Managed is extracted)"
curl -L --fail -o /tmp/yarg-win.zip \
  "https://github.com/YARC-Official/YARG/releases/download/v$YARG_VERSION/YARG_v$YARG_VERSION-Windows-x64.zip"
mkdir -p "$LIBS/yarg-managed"
unzip -q -j -o /tmp/yarg-win.zip 'YARG_Data/Managed/*' -d "$LIBS/yarg-managed"

for f in UnityEngine.dll UnityEngine.CoreModule.dll UnityEngine.UIModule.dll \
         UnityEngine.UI.dll Unity.InputSystem.dll UnityEngine.VideoModule.dll UnityEngine.AudioModule.dll UnityEngine.PhysicsModule.dll; do
  test -f "$LIBS/yarg-managed/$f" || { echo "MISSING: $f"; exit 1; }
done

echo "==> openvr_api.dll (ValveSoftware/openvr)"
curl -L --fail -o "$LIBS/openvr_api.dll" \
  "https://github.com/ValveSoftware/openvr/raw/master/bin/win64/openvr_api.dll"

echo "==> Done. Build with:"
echo "    dotnet build YARG.VR.csproj -c Release -p:GameLibs=$LIBS"
