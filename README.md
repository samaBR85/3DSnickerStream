<div align="center">

<img src="AppIcon.iconset/icon_256x256.png" width="120" alt="SnickerStream icon">

# Snickerstream4Mac

**A native Nintendo 3DS streaming client for Apple Silicon.**

Stream your 3DS screens to your Mac over Wi-Fi using NTR CFW remoteplay.

![Platform](https://img.shields.io/badge/platform-macOS%2013%2B-black?logo=apple)
![Arch](https://img.shields.io/badge/arch-Apple%20Silicon-blue)
![Swift](https://img.shields.io/badge/Swift-SwiftUI-orange?logo=swift)
![License](https://img.shields.io/badge/license-GPLv3-green)

</div>

![Connect screen](screenshots/hero.png)

---

## About

[Snickerstream](https://github.com/RattletraPM/Snickerstream) is a beloved 3DS streaming
client — but it only runs on Windows (it's written in AutoIt + Direct2D).

**Snickerstream4Mac** is a from-scratch reimplementation in **Swift / SwiftUI** that runs
natively on Apple Silicon. It speaks the same NTR remoteplay protocol — the TCP init
handshake and the UDP JPEG stream — and wraps it in a modern macOS interface with
hardware-accelerated JPEG decoding via ImageIO/Core Graphics.

> This is the `master` branch's upstream AutoIt code reimagined for the Mac. The original
> Windows sources are preserved on [`master`](../../tree/master); the port lives here on
> `macos-apple-silicon`.

## Features

- 🎮 **NTR remoteplay** — sends the init handshake, receives and reassembles the live stream
- 🖥️ **Both screens** with **Stacked / Side-by-side / Top-only / Bottom-only** layouts
- ✨ **Ambient backdrop** — a blurred glow of the game behind the screens
- 🎚️ Familiar controls — priority screen/factor, image quality, QoS
- 🔖 **Saved IPs** — bookmark consoles and reconnect with one click
- ⌨️ **Configurable keyboard shortcuts** (screenshot, layout, rotate, quality, fullscreen…)
- 📸 **Screenshots** straight to `~/Pictures/SnickerStream`
- 🔁 **Smart connect** — retries the init up to 3× and returns to the menu if the 3DS doesn't respond
- 🔍 Scaling filters (Sharp / Linear / Smooth) and 0/90/180/270° rotation
- ⚡ Layer-backed rendering for a smooth 30+ fps stream

### Keyboard shortcuts

<img src="screenshots/shortcuts.png" width="460" alt="Keyboard shortcuts">

| Action | Default | Action | Default |
|--------|:-------:|--------|:-------:|
| Screenshot | `S` | Toggle fullscreen | `⌘F` |
| Disconnect | `Esc` | Increase quality | `↑` |
| Cycle layout | `L` | Decrease quality | `↓` |
| Cycle filter | `F` | Swap priority screen | `P` |
| Rotate screen | `R` | | |

All bindings are remappable from the **⌨️ Keyboard Shortcuts** menu on the connect screen.

## Requirements

- Apple Silicon Mac, **macOS 13+**
- A Nintendo 3DS on the **same network** running **NTR CFW** with remoteplay

## Install

Grab the latest `SnickerStream-mac.zip` from the
[**Releases**](../../releases) page, unzip, and drag **SnickerStream.app** to Applications.

Because the app is ad-hoc signed, the first launch needs **right-click → Open** (or
*System Settings → Privacy & Security → Open Anyway*). On first connect, allow **Local
Network** access so it can reach the 3DS.

## Build from source

```bash
git clone -b macos-apple-silicon https://github.com/samaBR85/Snickerstream4Mac.git
cd Snickerstream4Mac
./build-app.sh           # produces SnickerStream.app
open SnickerStream.app

# run directly during development:
swift run SnickerStream

# verify the protocol logic (no 3DS needed):
swift run SnickerStream --selftest
```

## Usage

1. On the 3DS, launch NTR CFW and enable remoteplay.
2. Find the 3DS IP (Settings → Internet Settings → Connection).
3. Enter it in SnickerStream, tweak quality/priority if you like, and click **Connect**.
4. Frames appear once the stream starts. Use the control bar (or shortcuts) to change
   layout, filter, rotation, take screenshots, or disconnect.

## Protocol notes

Reverse-engineered from the original's `include/ntr.au3`:

**TCP init** — connect to `<3DS>:8000`, send an 84-byte NTR debugger command, disconnect,
wait ~3s, then connect/disconnect once more to kick streaming.

| Offset | Field | Value |
|-------:|-------|-------|
| `0x00` | magic | `0x12345678` |
| `0x04` | seq | `3000` |
| `0x0C` | cmd | `901` (remoteplay) |
| `0x10` | priority factor | `0–10` |
| `0x11` | priority screen | `1`=top, `0`=bottom |
| `0x14` | JPEG quality | `10–100` |
| `0x1A` | QoS | value × 2 |

**UDP frames** — the 3DS pushes datagrams to `<PC>:8001`, each a 4-byte header + JPEG slice
(frame id / screen+last-packet nibble / format / packet number). Payloads are concatenated
in order until the last-packet flag; the result is a JPEG (validated by its `FF D9` marker).

## Not yet implemented

- **HzMod** protocol (the original also supports it). NTR is the focus; the `NTRClient` is
  structured so a second backend can sit alongside it.

## Credits & license

Protocol and UX modeled on the original **[Snickerstream](https://github.com/RattletraPM/Snickerstream)**
by RattletraPM and contributors. Licensed under **GPLv3** (see [LICENSE](LICENSE)), same as
upstream.
