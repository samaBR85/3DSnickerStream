#!/bin/bash
# Create-or-upload a platform build to a shared GitHub release (see RELEASING.md).
# Usage: ./scripts/release.sh <tag> <asset> [target-branch] [notes]
#   ./scripts/release.sh v1.4.0 3DSnickerStream-mac.zip macos-apple-silicon "Notes here"
set -euo pipefail

TAG="${1:?usage: release.sh <tag> <asset> [target-branch] [notes]}"
ASSET="${2:?missing asset path}"
TARGET="${3:-macos-apple-silicon}"
NOTES="${4:-Release $TAG}"

if [ ! -f "$ASSET" ]; then
    echo "✗ asset not found: $ASSET" >&2
    exit 1
fi

if gh release view "$TAG" >/dev/null 2>&1; then
    echo "▶ Release $TAG exists — uploading $ASSET"
    gh release upload "$TAG" "$ASSET" --clobber
else
    echo "▶ Creating release $TAG (target $TARGET) with $ASSET"
    gh release create "$TAG" "$ASSET" \
        --target "$TARGET" \
        --title "3DSnickerStream $TAG" \
        --notes "$NOTES"
fi

echo "✓ Done. Assets on $TAG:"
gh release view "$TAG" --json assets --jq '.assets[].name' | sed 's/^/    /'
