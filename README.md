# SnickerStream for Apple Silicon

A native macOS (Apple Silicon) reimplementation of
[RattletraPM/Snickerstream](https://github.com/RattletraPM/Snickerstream) — a streaming
client for the **Nintendo 3DS** running NTR CFW remoteplay.

The original is Windows-only (written in AutoIt + Direct2D). This is a from-scratch
rewrite in **Swift / SwiftUI** using Apple's `Network` framework for sockets and
ImageIO/Core Graphics for hardware-accelerated JPEG decoding — it compiles to a real
`arm64` `.app`.

## Features

- **NTR remoteplay**: sends the init handshake and receives the live stream.
- Connect dialog with IP, listen port, **priority screen** (top/bottom), **priority
  factor**, **image quality**, and **QoS** — the same knobs as the original.
- Top + bottom screen display with **Stacked / Side-by-side / Top-only / Bottom-only**
  layouts.
- Automatic 90° rotation (3DS framebuffers are stored rotated) and live **FPS** counter.
- Layer-backed rendering so the stream stays smooth at 30+ fps.
- Settings persist between launches.

## Requirements

- Apple Silicon Mac, macOS 13+
- Xcode / Swift toolchain (`swift --version` should report Swift 5.9+)
- A 3DS on the **same network** running NTR CFW with remoteplay support

## Build & run

```bash
# Build a double-clickable app bundle:
./build-app.sh
open SnickerStream.app

# …or run directly during development:
swift run SnickerStream

# Verify the protocol logic (no 3DS needed):
swift run SnickerStream --selftest
```

On first connect macOS may ask for **Local Network** permission — allow it so the app can
reach the 3DS.

## How to use

1. On the 3DS, launch NTR CFW and enable remoteplay (or use a remoteplay-enabling app).
2. Find the 3DS IP (Settings → Internet Settings → Connection).
3. Enter that IP in SnickerStream, adjust quality/priority if desired, and click **Connect**.
4. The app sends the NTR init, binds UDP `8001`, and starts displaying frames.

## Protocol notes (reverse-engineered from the original `include/ntr.au3`)

**TCP init** — connect to `<3DS>:8000`, send an 84-byte NTR debugger command, disconnect,
wait ~3s, then connect/disconnect once more to kick streaming. Packet layout:

| Offset | Field | Value |
|-------:|-------|-------|
| `0x00` | magic | `0x12345678` (LE) |
| `0x04` | seq | `3000` |
| `0x0C` | cmd | `901` (remoteplay) |
| `0x10` | priority factor | `0–10` |
| `0x11` | priority screen | `1`=top, `0`=bottom |
| `0x14` | JPEG quality | `10–100` |
| `0x1A` | QoS | value × 2 |

**UDP frames** — the 3DS pushes datagrams to `<PC>:8001`, each a 4-byte header + JPEG slice:

| Byte | Meaning |
|-----:|---------|
| `0` | frame id |
| `1` | high nibble = last-packet flag; low nibble = screen (`1`=top, `0`=bottom) |
| `2` | image format (usually `2`) |
| `3` | packet number within the frame |

Payloads are concatenated in packet-number order until the last-packet flag; the result is
a complete JPEG (validated by its `FF D9` end marker).

## Project layout

```
Sources/SnickerStream/
  App.swift            – @main app + AppDelegate (activation policy, --selftest)
  ContentView.swift    – routes between connect / stream views
  ConnectView.swift    – connection dialog
  StreamView.swift     – live screens, layout picker, FPS bar, layer-backed renderer
  NTRClient.swift      – TCP init + UDP receive + frame reassembly (the protocol core)
  StreamViewModel.swift – JPEG decode, rotation, FPS, published frames
  SelfTest.swift       – headless protocol validation (--selftest)
```

## Not yet implemented

- **HzMod** protocol (the original also supports it). NTR is the focus here; the client is
  structured so a second protocol backend can be added alongside `NTRClient`.

## Credits

Protocol and UX modeled on the original **Snickerstream** by RattletraPM and contributors.
