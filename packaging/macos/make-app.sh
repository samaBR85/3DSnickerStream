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

echo "built $APP (version $VERSION)"
