# Releasing 3DSnickerStream (macOS + Windows, one repo)

This app ships from a single repo with two platform builds. Two Claude Code instances (one on a
Mac, one on Windows) publish to the **same GitHub releases**, one build each.

## Branch layout
| Branch | Content | Who touches it |
|---|---|---|
| `master` | Original AutoIt (upstream) | nobody |
| `macos-apple-silicon` *(default)* | Swift / SwiftUI app | **only the Mac agent** |
| `windows` | WPF / .NET app | **only the Windows agent** |

**Golden rule:** each agent only pushes to its own branch. The only shared surface is the
releases/tags list.

## Release model — one tag per version, two assets
For a version `vX.Y.Z` there is **one** GitHub release holding both platform artifacts:

- `3DSnickerStream-mac.zip` — from the Mac agent
- `3DSnickerStream-win.zip` — from the Windows agent

Both platforms share the **same version line** (`vX.Y.Z`). A release may temporarily carry only
one asset until the other platform catches up.

## The release procedure (create-or-upload)
Whichever platform releases a version first **creates** the release; the other **uploads** its
asset to the existing one. `--clobber` makes re-runs safe. `--target` is your own branch.

**macOS (bash):**
```bash
TAG=v1.4.0
gh release view "$TAG" >/dev/null 2>&1 \
  && gh release upload "$TAG" 3DSnickerStream-mac.zip --clobber \
  || gh release create "$TAG" 3DSnickerStream-mac.zip \
       --target macos-apple-silicon --title "3DSnickerStream $TAG" --notes "…"
```
(or just run `./scripts/release.sh v1.4.0 3DSnickerStream-mac.zip macos-apple-silicon`)

**Windows (PowerShell):**
```powershell
$TAG = "v1.4.0"
gh release view $TAG *> $null
if ($LASTEXITCODE -eq 0) {
    gh release upload $TAG 3DSnickerStream-win.zip --clobber
} else {
    gh release create $TAG 3DSnickerStream-win.zip `
        --target windows --title "3DSnickerStream $TAG" --notes "…"
}
```

Notes are version-level (feature-focused), written by whoever creates the release; the asset
names identify each platform.

## One-time Windows setup
On the Windows PC, in your WPF project folder (a git repo with your commits):
```powershell
# 1. Authenticate the GitHub CLI as samaBR85
winget install --id GitHub.cli    # if gh isn't installed
gh auth login                     # GitHub.com · HTTPS · browser

# 2. Publish your local project as the 'windows' branch
git remote add origin https://github.com/samaBR85/3DSnickerStream.git
git fetch origin
git push -u origin HEAD:windows

# 3. Add a GPLv3 LICENSE file to the project (copy the repo's), then commit/push.

# 4. First Windows release = attach your build to the current tag (makes it multi-platform)
gh release upload v1.3.1 3DSnickerStream-win.zip --clobber
```

## Sanity checks
- `git branch -r` shows `origin/windows` alongside `master` and `macos-apple-silicon`.
- `gh release view v1.3.1 --json assets --jq '.assets[].name'` lists both zips once Windows uploads.
