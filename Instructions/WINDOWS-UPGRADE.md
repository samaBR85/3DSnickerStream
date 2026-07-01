# 3DSnickerStream — Windows upgrade guide (to match macOS v1.3.1)

You already built the Windows app from **`BUILD-ON-WINDOWS.md`**. Since then the macOS app
gained five things. This file is the **delta only** — apply these on top of your existing build.
Everything else in `BUILD-ON-WINDOWS.md` is unchanged.

> How to use with Claude Code on Windows: open your project, paste **both** `BUILD-ON-WINDOWS.md`
> (the base spec) and this file, and say *"apply these upgrades to the existing app."*

---

## 1. Quality ↔ framerate presets (NEW)

Add a **"Preset"** row at the top of the **Remoteplay** card (above Priority screen / Priority
factor / Image quality / QoS). It's a dropdown that sets **priority factor, image quality, and
QoS** in one click (priority screen = Top for all built-ins).

**The 7 built-in presets** (priority factor, quality, QoS):

| Preset | Factor | Quality | QoS |
|---|:---:|:---:|:---:|
| Best quality | 2 | 90 | 10 |
| Great quality | 5 | 80 | 18 |
| Good quality | 5 | 75 | 18 |
| **Balanced** *(default)* | 5 | 70 | 20 |
| Good framerate | 8 | 60 | 26 |
| Great framerate | 8 | 50 | 26 |
| Best framerate | 10 | 40 | 34 |

Behavior:
- Selecting a preset writes those three values into the sliders (they update live).
- The dropdown's label shows the **matching preset name**, or **"Custom"** if the current
  factor/quality/QoS don't match any preset.
- **Add custom preset…** opens a name dialog and saves the current factor/quality/QoS under that
  name. **Delete custom preset** lists the custom ones to remove.
- Persist custom presets in the settings JSON (`%APPDATA%\3DSnickerStream\settings.json`).
- For **HzMod** the preset effectively just changes quality (factor/QoS don't apply) — that's fine.

---

## 2. Per-screen scale (NEW)

Add two sliders to the **Display** card: **Top scale** and **Bottom scale**, range **0.5×–2.0×**,
step 0.1, default **1.0×**. Persisted.

In the **streaming view**, the two screens split the available space **proportionally to their
scale weights** (instead of an even split):
- **Stacked:** top gets `topScale / (topScale + bottomScale)` of the height; bottom gets the rest.
- **Side by side:** same split but on width.
- **Top only / Bottom only:** scale is ignored (single screen fills).

Each screen still keeps its aspect ratio (top 400:240, bottom 320:240) and stays centered inside
its slot. (In WPF: put the two screens in a `Grid` and set the row/column `Height`/`Width` to
`new GridLength(topScale, GridUnitType.Star)` and `new GridLength(bottomScale, GridUnitType.Star)`.)

---

## 3. Rotation relabel (CHANGED)

The upright image is now labeled **0°** (it used to be labeled 270°). More intuitive: the menu
opens on **0° = correct orientation** and the default is 0°.

Implementation: the 270° correction that makes the raw 3DS framebuffer upright is now **baked in**,
and the user-facing rotation is an **offset added on top**:

```
displayedRotation ∈ {0, 90, 180, 270}, default 0
renderedAngleClockwise = (270 + displayedRotation) mod 360
```

So the Rotation menu shows `0° / 90° / 180° / 270°` (default **0°**), and you rotate each frame by
`(270 + displayedRotation)` degrees clockwise. `0°` → 270° (upright); `90°` → 0° (raw); etc.
Update both the Display-card menu and the stream-bar rotation control to default to 0°.

---

## 4. "Screenshot to clipboard" shortcut (NEW)

Add one more keyboard shortcut:

| Action | Default |
|---|---|
| **Screenshot to clipboard** | **`Shift+S`** |

It composes the current screen(s) exactly like a normal screenshot, but **copies the image to the
clipboard** instead of saving a file (the plain `S` still saves a PNG). Show a brief status like
"Screenshot copied to clipboard". Add it to the remappable shortcuts list.

(In WPF: `System.Windows.Clipboard.SetImage(bitmapSource)`. Make sure `Shift+S` and `S` are
distinct in your key matcher — match on key **and** modifiers.)

---

## 5. Check for updates (NEW)

On startup (if enabled), check GitHub for a newer release and show a banner.

- **Endpoint:** `GET https://api.github.com/repos/samaBR85/3DSnickerStream/releases/latest`
  with header `Accept: application/vnd.github+json`.
  ⚠️ **The GitHub API requires a `User-Agent` header** — with .NET `HttpClient` you must set one
  (e.g. `client.DefaultRequestHeaders.UserAgent.ParseAdd("3DSnickerStream")`) or you'll get HTTP 403.
- Parse `tag_name` (e.g. `v1.3.1`), strip a leading `v`, and **compare semantically** to the app's
  own version. If the release is newer, show a **dismissible banner** above the cards:
  *"Update available — v1.x.y"* with a **Download** button that opens the release's `html_url`
  (or `https://github.com/samaBR85/3DSnickerStream/releases`).
- Add to **About**: the **current version**, a **"Check for updates on startup"** toggle
  (persisted, default on), and a **"Check now"** button.
- Keep the app's version constant in sync with the release tag (currently **1.3.1**).

---

## Quick checklist

- [ ] Preset dropdown in Remoteplay card (7 built-ins + add/delete custom), default "Balanced"
- [ ] Top/Bottom scale sliders (0.5×–2.0×) + proportional split in the stream view
- [ ] Rotation relabeled so 0° = upright (render `270 + displayedRotation`)
- [ ] `Shift+S` = screenshot to clipboard
- [ ] Startup update check (GitHub releases) with banner + About toggle/version
- [ ] App/version strings already say **3DSnickerStream** / **1.3.1**

*Test each against the real 3DS. These are additive — they don't change the NTR/HzMod protocol or
the rest of the UI.*
