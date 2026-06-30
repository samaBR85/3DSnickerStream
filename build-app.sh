#!/bin/bash
# Builds SnickerStream.app — a native Apple Silicon application bundle.
set -euo pipefail
cd "$(dirname "$0")"

CONFIG="${1:-release}"
APP="SnickerStream.app"
BIN_NAME="SnickerStream"

echo "▶ Building ($CONFIG)…"
swift build -c "$CONFIG" --arch arm64

BIN_PATH="$(swift build -c "$CONFIG" --arch arm64 --show-bin-path)/$BIN_NAME"

echo "▶ Assembling $APP…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_PATH" "$APP/Contents/MacOS/$BIN_NAME"
cp Info.plist "$APP/Contents/Info.plist"

# App icon — regenerate if missing, then bundle it.
if [ ! -f AppIcon.icns ]; then
    echo "▶ Generating app icon…"
    swift make_icon.swift && iconutil -c icns AppIcon.iconset -o AppIcon.icns
fi
[ -f AppIcon.icns ] && cp AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc codesign so Gatekeeper/local-network permission works on the user's machine.
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true

echo "✓ Built $APP"
echo "  Run:  open $APP    (or ./$APP/Contents/MacOS/$BIN_NAME)"
