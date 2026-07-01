# 3DSnickerStream — macOS parity brief (v1.0 → v1.2)

Instructions for **Claude Code on macOS** to bring the **macOS app** (branch `macos-apple-silicon`,
SwiftUI, `Sources/3DSnickerStream/…`) up to feature parity with the **Windows v1.2** release. The
Windows app (WPF) is the source of truth for behavior; on macOS, implement the *same behavior* the
native SwiftUI/AppKit/Core Image way — do **not** copy WPF specifics.

Work only on the `macos-apple-silicon` branch. These are additive UI/UX features on the streaming and
connect screens; the NTR/HzMod protocol is unchanged. Verified beforehand: the macOS app currently has
**none** of these (no per-screen color, zoom, clean mode, per-screen scale, gap slider, or network
toggles). Likely files: `StreamView.swift`, `StreamViewModel.swift`, `ConnectView.swift`,
`Settings.swift`, `NTRClient.swift`, `Info.plist`.

Persist all new settings the same way existing ones are (`@AppStorage` / the settings store). Match
defaults so upgrading users see no change until they touch a control.

---

## 1. Per-screen color adjust (Brightness / Contrast / Saturation / Highlights / Shadows)
Independent color controls **per screen** (top and bottom separately), revealed by a toggle button
(label **"Adjustes"**/"Adjust") in the streaming controls; panels appear beside each screen.

- Controls per screen: **Brightness**, **Contrast**, **Saturation**, **Highlights**, **Shadows**, plus a
  **Reset**.
- **Native approach (recommended):** apply a Core Image chain to each decoded frame/image before
  display — `CIColorControls` (brightness/contrast/saturation) + `CIHighlightShadowAdjust`
  (`highlightAmount` to pull down blown highlights, `shadowAmount` to lift the blacks). This is the
  clean macOS equivalent of the Windows CPU pixel loop; only run the filter when a value is non-neutral.
- Behavior to match:
  - **Brightness** −1..1 (0 neutral), **Contrast** 0..2 (1), **Saturation** 0..2 (1).
  - **Highlights** 0..1 (0 = off) — reduces only the bright/blown areas (tames blown whites). In the
    Windows UI its slider is **reversed** (rest at the right, drag right→left to increase); mirror that
    on macOS if easy, otherwise a normal 0..1 slider is fine.
  - **Shadows** 0..1 (0 = off) — lifts only the dark areas (reveals shadow detail); normal slider
    (drag left→right).
  - **Reset** returns Brightness 0 / Contrast 1 / Saturation 1 / Highlights 0 / Shadows 0.
- Changing a slider updates that screen live.

## 2. Adjustable vertical gap between the stacked screens
A **Gap** slider (in the streaming controls) that sets the vertical spacing between the two stacked
screens: **0 = the screens touch**, larger = more separation (also apply to side-by-side as horizontal
spacing). Default a small value (~16 pt). The **screenshot** composition must use this same gap (0 =
glued), not a fixed gap.

## 3. Per-screen scale in the streaming controls
**Top scale** and **Bottom scale** sliders (≈0.5×–2.0×, default 1.0×) that change the relative size of
each screen **live in the streaming view** (not only on the connect screen). If the macOS connect
screen doesn't have them either, add them there too; the point is live preview while streaming.

## 4. Zoom (Fit / native %)
A **Zoom** menu in the streaming controls: **Fit** (scale-to-fill, the current behavior) plus fixed
percentages **100% / 150% / 200% / 300%**, where **100% = native 3DS resolution** (top **400×240**,
bottom **320×240**, 1:1 pixels — pair with a nearest-neighbor/"Sharp" interpolation for crispness).
On a fixed %, **grow the `NSWindow` to fit** the zoomed screens (adjust to the larger screen), clamped
to the visible screen frame; never shrink below what the controls need. Per-screen scale (feature 3)
still multiplies on top of the zoom.

## 5. Clean mode (hide UI) — keyboard shortcut
A remappable shortcut (**default `H`**) that **hides all chrome** (controls/toolbar/adjust panels) and
shows only the two screens, resizing the window so the **top screen is flush to the sides** (no black
bars). **Esc or the same key restores** the UI. Add it to the shortcuts list. On macOS this is an
`NSWindow` style/size change (borderless-ish content) computed from the screen group's aspect; remember
and restore the previous window frame/state on exit.

## 6. "Find on network" — status field + toggles (connect screen)
Rework the network-scan UI:
- Put the **radar/scan icon next to an inline status field** that shows **"Find on network" → "Scanning…"
  → "Found 192.168.x.x" (or "Found <ip> (+N)") → "No device found"**. On a hit, also **fill the IP
  address field** automatically so Connect is ready. (No separate popover.)
- Three persisted **toggles**:
  - **Scan on startup** — scan the network automatically, but **only at real app launch** (not every
    time the user returns to the connect screen — otherwise it loops with Auto-connect).
  - **Auto-connect (found device)** — when the scan finds a 3DS, connect to it automatically (once).
  - **Try reconnect** — if the initial connect fails **or** the stream drops, automatically retry in a
    loop until it connects or the user cancels. Because NTR is UDP with no drop signal, add a
    **stale-frame watchdog** (≈5 s with zero received frames while streaming ⇒ treat as dropped and
    reconnect). An explicit user **Disconnect** must NOT trigger auto-scan/auto-connect/reconnect.

## 7. Version → 1.2
- `Sources/3DSnickerStream/Settings.swift` — `AppInfo.version` `"1.0"` → `"1.2"` (shown in About).
- `Info.plist` — set both `CFBundleShortVersionString` and `CFBundleVersion` to `1.2`.

---

## Verification (macOS)
- `swift build` (or Xcode) succeeds; About shows **v1.2**.
- Streaming: Gap slider joins/separates the screens (touching at 0); Top/Bottom scale resize live;
  the **Adjust** toggle reveals per-screen Brightness/Contrast/Saturation/Highlights/Shadows that
  affect only their screen; Reset clears them; **Zoom** 100% shows pixel-native screens and the window
  grows to fit; **`H`** hides the UI flush and **Esc/H** restores; screenshot respects the gap.
- Connect: radar icon + inline status; Scan-on-startup runs once at launch; Auto-connect connects to
  the found device; Try-reconnect retries after a failed connect and after a stream drop until it
  connects or is cancelled; a manual Disconnect stays on the menu (no loop).

*Source of truth: 3DSnickerStream Windows v1.2. Implement the same behavior natively on macOS.*
