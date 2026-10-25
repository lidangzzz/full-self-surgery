#!/usr/bin/env bash
# Clones SofaUnity (https://github.com/InfinyTech3D/SofaUnity) into Assets/,
# applies the Unity 6 compatibility patches, and (on macOS) installs the
# native SOFA libraries so the SofaUnity physics backend actually runs.
#
# System prerequisites on macOS (Apple Silicon):
#   brew install glfw glew tinyxml2
#   (the official dylibs link against homebrew paths)
#
# The upstream "Modules" submodule points to a repo that no longer exists
# publicly; this script tolerates that failure.
set -euo pipefail
cd "$(dirname "$0")"

DEST="Assets/SofaUnity"
NATIVE_DIR="$DEST/Core/Plugins/Native/macOS"
SOFA_MAC_ZIP="https://github.com/InfinyTech3D/SofaUnity/releases/download/SofaUnity_v26.06.00/SOFA_MacOS_v26.06.zip"

echo "[setup] == 1/3 SofaUnity source =="
if [ -d "$DEST/.git" ]; then
    echo "[setup] $DEST already exists - skipping clone (delete it to re-clone)"
else
    echo "[setup] cloning SofaUnity into $DEST ..."
    git clone --recurse-submodules -j4 https://github.com/InfinyTech3D/SofaUnity.git "$DEST" || true
    [ -d "$DEST" ] || git clone https://github.com/InfinyTech3D/SofaUnity.git "$DEST"
fi

cd "$DEST"
git config submodule.Modules.url https://github.com/InfinyTech3D/SofaUnity_modules.git
git submodule update --init --recursive || \
    echo "[setup] WARNING: Modules submodule unavailable upstream (expected) - continuing without it"

echo "[setup] == 2/3 compatibility patch =="
git apply --check ../../sofaunity-unity6-compat.patch 2>/dev/null \
    && git apply ../../sofaunity-unity6-compat.patch \
    && echo "[setup] patch applied" \
    || echo "[setup] patch not applied (already applied or upstream fixed)"

echo "[setup] == 3/3 native SOFA libraries (macOS) =="
cd - >/dev/null
if [ "$(uname)" != "Darwin" ]; then
    echo "[setup] not macOS - get the Windows DLLs from the SofaUnity releases instead (see README)"
    exit 0
fi
if [ -d "$NATIVE_DIR" ] && ls "$NATIVE_DIR"/libSofaVerseAPI.dylib >/dev/null 2>&1; then
    echo "[setup] native libs already installed at $NATIVE_DIR"
else
    echo "[setup] downloading SOFA_MacOS_v26.06.zip ..."
    curl -sL -o /tmp/SOFA_MacOS_v26.06.zip "$SOFA_MAC_ZIP"
    rm -rf /tmp/sofa_mac_extract
    mkdir -p /tmp/sofa_mac_extract
    unzip -qo /tmp/SOFA_MacOS_v26.06.zip -x "*__MACOSX*" "*/.DS_Store" -d /tmp/sofa_mac_extract
    mkdir -p "$(dirname "$NATIVE_DIR")"
    rm -rf "$NATIVE_DIR"
    mv /tmp/sofa_mac_extract/Native/macos "$NATIVE_DIR"
    xattr -dr com.apple.quarantine "$NATIVE_DIR" 2>/dev/null || true
    echo "[setup] native libs installed: $(ls "$NATIVE_DIR" | grep -c 'dylib$') dylibs"
fi

echo "[setup] checking homebrew dependencies of the dylibs ..."
MISSING=0
for dep in /opt/homebrew/opt/glfw/lib/libglfw.3.dylib \
           /opt/homebrew/opt/glew/lib/libGLEW.2.3.dylib \
           /opt/homebrew/opt/tinyxml2/lib/libtinyxml2.11.dylib; do
    [ -e "$dep" ] || { echo "[setup] MISSING: $dep  ->  brew install $(basename "$(dirname "$(dirname "$(dirname "$dep")")")")"; MISSING=1; }
done
[ "$MISSING" = "0" ] && echo "[setup] all system dependencies present" || \
    echo "[setup] run the brew installs above, then open the project in Unity."

echo "[setup] done. Open the project in Unity 6000.6+; set MultiArmAgentManager.backend = SofaUnity to enable SOFA."
