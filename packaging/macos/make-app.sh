#!/usr/bin/env bash
# Assemble a macOS .app bundle around a self-contained publish of 3DSnickerStream.
#
# Usage: make-app.sh <publish-dir> <output-app-path> <version>
#   publish-dir      folder produced by `dotnet publish -r osx-* --self-contained true`
#   output-app-path  e.g. dist/3DSnickerStream.app
#   version          e.g. 2.0.0
#
# Produces an UNSIGNED bundle. On first launch users must right-click > Open (Gatekeeper),
# or run: xattr -dr com.apple.quarantine 3DSnickerStream.app
set -euo pipefail

PUBLISH_DIR="${1:?publish dir required}"
APP="${2:?output .app path required}"
VERSION="${3:?version required}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"

EXE_NAME="3DSnickerStream"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# Payload -> Contents/MacOS
cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXE_NAME"

# Info.plist (substitute version)
sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"

# Icon: build AppIcon.icns from the 1024px source next to the repo root.
ICON_SRC="$REPO_ROOT/AppIcon-1024.png"
if [[ -f "$ICON_SRC" ]] && command -v iconutil >/dev/null 2>&1; then
    ICONSET="$(mktemp -d)/AppIcon.iconset"
    mkdir -p "$ICONSET"
    for sz in 16 32 64 128 256 512; do
        sips -z $sz $sz "$ICON_SRC" --out "$ICONSET/icon_${sz}x${sz}.png" >/dev/null
        sips -z $((sz*2)) $((sz*2)) "$ICON_SRC" --out "$ICONSET/icon_${sz}x${sz}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
else
    echo "warn: iconutil/AppIcon-1024.png missing — bundle will use a generic icon" >&2
fi

# MetalFX native helper (Apple Silicon/Intel exclusive upscaler). Compiled here — not by dotnet — as a
# universal (arm64 + x86_64) dylib so a single build covers both the osx-arm64 and osx-x64 packages.
# On Macs whose GPU lacks MetalFX support, mfx_available() returns 0 and the app disables the option.
NATIVE_SRC="$REPO_ROOT/native/metalfx_helper.m"
if [[ -f "$NATIVE_SRC" ]] && command -v clang >/dev/null 2>&1; then
    clang -x objective-c -fobjc-arc -mmacosx-version-min=13.0 -arch arm64 -arch x86_64 -dynamiclib \
        -framework Foundation -framework Metal -framework MetalFX \
        -o "$APP/Contents/MacOS/libmetalfx_helper.dylib" "$NATIVE_SRC" \
        && echo "compiled MetalFX helper (universal arm64+x86_64)"
else
    echo "warn: native/metalfx_helper.m or clang missing — MetalFX upscaler will be unavailable" >&2
fi

# Ad-hoc sign the whole bundle. Apple Silicon requires signed code to launch at all, and a signed
# (if un-notarized) download shows the bypassable "unidentified developer" prompt instead of the
# dead-end "damaged and can't be opened" error. Must run last — signing after any bundle change.
if command -v codesign >/dev/null 2>&1; then
    codesign --force --deep --sign - "$APP" && echo "ad-hoc signed $APP"
else
    echo "warn: codesign not found — bundle left unsigned (will be blocked on Apple Silicon)" >&2
fi

echo "built $APP (version $VERSION)"
