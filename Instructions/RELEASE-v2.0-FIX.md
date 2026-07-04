# 3DSnickerStream v2.0 — release naming incident & what's left for Windows

> How to use with Claude Code on Windows: open this project (pull `main` first so this file and
> the current `Snickerstream4Win`/`Snickerstream.Avalonia` code are in sync), paste this file, and
> say *"fix the Windows/Linux release assets per this doc."*

## What happened

While fixing macOS-only OCR/UI bugs in the Avalonia app (v2.0), the macOS Claude Code session
mistakenly ran the full `build-and-package` CI workflow via `workflow_dispatch` and uploaded
**all 5** resulting platform assets to the `v2.0` GitHub release with `--clobber` — including
Windows and Linux, which had nothing to do with the macOS fixes and should not have been touched.

This also broke the **release asset naming convention that Auto-Update depends on**: assets must
have **no version number in the filename** (e.g. `3DSnickerStream-win.zip`,
`3DSnickerStream-mac.zip`), not the `3DSnickerStream-v2.0-<rid>.<ext>` pattern that the current
`.github/workflows/release.yml` (`build-and-package`) produces when run standalone.

## What was already fixed (macOS side, done)

- The `macos-apple-silicon` branch (previously the old native Swift/SwiftUI v1.4 app) was force-
  replaced with the current Avalonia `main` code — it's now the source for macOS builds.
- On the `v2.0` release:
  - Deleted the wrongly-named `3DSnickerStream-v2.0-osx-arm64.zip` and
    `3DSnickerStream-v2.0-osx-x64.zip`.
  - Uploaded `3DSnickerStream-mac.zip` (correct naming, no version) built from the up-to-date
    macOS Avalonia app (includes fixes: OCR via system `tesseract` CLI, in-window OCR result
    popup working in fullscreen, correct popup position on HiDPI, drag-anywhere-on-the-panel,
    correct app name in the macOS menu bar instead of "Avalonia Application").

## What's left — Windows (and Linux) release assets

The following two assets on the `v2.0` release are **still using the wrong (versioned) naming**
and were left untouched on purpose, waiting for you:

- `3DSnickerStream-v2.0-win-x64.zip` → should become **`3DSnickerStream-win.zip`**
- `3DSnickerStream-v2.0-linux-x64.tar.gz` / `3DSnickerStream-v2.0-linux-x86_64.AppImage` → should
  probably become **`3DSnickerStream-linux.tar.gz`** / **`3DSnickerStream-linux.AppImage`** (adjust
  to whatever your actual established convention is — mac side used no-version, no-RID names)

### Two ways to do this

1. **Just rename** the current assets on the release (fastest, no rebuild):
   ```
   gh release download v2.0 -p "3DSnickerStream-v2.0-win-x64.zip" -O 3DSnickerStream-win.zip
   gh release delete-asset v2.0 3DSnickerStream-v2.0-win-x64.zip -y
   gh release upload v2.0 3DSnickerStream-win.zip
   ```
   (repeat similarly for the Linux assets if you also manage those)

2. **Rebuild from the original, untouched CI run** if you'd rather not reuse the binary the
   macOS-side `workflow_dispatch` produced (even though it was built from the same commit, in
   case you want a clean provenance trail):
   - The **original** CI run that actually built and published `v2.0` (triggered by the `v2.0`
     tag push, not a later `workflow_dispatch`) is run id **`28694589622`**
     (`.github/workflows/release.yml`, commit `bcbce7f`).
   - Its artifacts (`3DSnickerStream-win-x64`, `3DSnickerStream-linux-x64`) were still available
     (not expired) as of this writing — download via:
     ```
     gh run download 28694589622 -n 3DSnickerStream-win-x64 -D ./restore-win
     ```
     then rename/upload the zip inside to `3DSnickerStream-win.zip` as above.

### Please also double-check

- Whatever mechanism reads the release assets by name for Windows Auto-Update — confirm
  `3DSnickerStream-win.zip` (no version) is what it actually expects, and update
  `Snickerstream.Avalonia/Net/UpdateChecker.cs` or any Windows-specific updater logic if the
  convention is something slightly different from what's assumed here.
- The `v2.0` release should currently look like this once you're done (macOS side is already
  correct):
  ```
  3DSnickerStream-mac.zip
  3DSnickerStream-win.zip
  3DSnickerStream-linux.tar.gz  (or your Linux convention)
  ```

## Guardrails that were violated (please keep these in mind)

- **Don't run the full 4-platform CI workflow and `--clobber` every asset** just because one
  platform changed. Only rebuild/replace the asset(s) for the platform actually touched.
- **Release asset filenames must never include a version number** — Auto-Update depends on the
  bare `3DSnickerStream-<platform>.zip` naming.
- The release (`v2.0`) is still `published` (not draft) and the tag hasn't moved — no need to
  worry about that part.
