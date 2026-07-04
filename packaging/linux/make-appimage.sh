#!/usr/bin/env bash
# Build a Linux AppImage around a self-contained publish of 3DSnickerStream.
#
# Usage: make-appimage.sh <publish-dir> <output-appimage> <version>
#   publish-dir      folder from `dotnet publish -r linux-x64 --self-contained true`
#   output-appimage  e.g. dist/3DSnickerStream-x86_64.AppImage
#   version          e.g. 2.0.0
#
# Requires appimagetool on PATH (or set APPIMAGETOOL to its path). Downloads it if absent.
set -euo pipefail

PUBLISH_DIR="${1:?publish dir required}"
OUT="${2:?output AppImage path required}"
VERSION="${3:?version required}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
EXE_NAME="3DSnickerStream"

APPDIR="$(mktemp -d)/3DSnickerStream.AppDir"
mkdir -p "$APPDIR/usr/bin"

# Payload
cp -R "$PUBLISH_DIR"/. "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/$EXE_NAME"

# Desktop entry (AppImage wants it at the AppDir root too)
cp "$HERE/3dsnickerstream.desktop" "$APPDIR/3dsnickerstream.desktop"

# Icon: 256px PNG named to match Icon= in the .desktop
if command -v convert >/dev/null 2>&1 && [[ -f "$REPO_ROOT/AppIcon-1024.png" ]]; then
    convert "$REPO_ROOT/AppIcon-1024.png" -resize 256x256 "$APPDIR/3dsnickerstream.png"
elif [[ -f "$REPO_ROOT/AppIcon-1024.png" ]]; then
    cp "$REPO_ROOT/AppIcon-1024.png" "$APPDIR/3dsnickerstream.png"
fi

# AppRun launcher
cat > "$APPDIR/AppRun" <<'RUN'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/3DSnickerStream" "$@"
RUN
chmod +x "$APPDIR/AppRun"

# appimagetool
TOOL="${APPIMAGETOOL:-appimagetool}"
if ! command -v "$TOOL" >/dev/null 2>&1; then
    TOOL="$(mktemp -d)/appimagetool"
    curl -fsSL -o "$TOOL" \
        "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "$TOOL"
fi

mkdir -p "$(dirname "$OUT")"
ARCH=x86_64 VERSION="$VERSION" "$TOOL" --no-appstream "$APPDIR" "$OUT"
echo "built $OUT (version $VERSION)"
