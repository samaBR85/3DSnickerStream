# Building Snickerstream4Mac on Windows 11 (with Claude Code)

This document is a **complete brief** for recreating this app on **Windows 11** using
**Claude Code**, keeping the same **features, colors, and menus**. You don't need to port the
Swift code line by line — Claude Code should **reimplement** it in a native Windows stack,
following the spec below.

> Tip: clone the macOS repo as a protocol/UX reference:
> `git clone -b macos-apple-silicon https://github.com/samaBR85/Snickerstream4Mac.git`
> The key reference files are `Sources/SnickerStream/NTRClient.swift`,
> `HzModClient.swift`, `NetworkScanner.swift`, `ConnectView.swift`, `StreamView.swift`.

---

## 1. What the app does

A native client to **stream the Nintendo 3DS screen** to the PC over Wi-Fi, using the **NTR**
(primary) and **HzMod** (beta) remoteplay homebrews. It receives JPEG frames, decodes, rotates,
and shows both screens in real time, with a modern dark UI.

---

## 2. Recommended Windows stack

**Recommended: C# + .NET 8 + WPF** (native, mature, easy to build, and Claude Code handles it well).

| Need | On Windows (.NET) |
|---|---|
| UDP (receive NTR on 8001) | `System.Net.Sockets.UdpClient` |
| TCP (NTR init 8000 / HzMod 6464) | `System.Net.Sockets.TcpClient` |
| Decode JPEG | `JpegBitmapDecoder` / `BitmapImage` (WPF) |
| Rotate a frame | `TransformedBitmap` with `RotateTransform` |
| Fast rendering | `Image` + `WriteableBitmap` or swapping `Source` |
| Folder picker | `Ookii.Dialogs.Wpf` or `FolderBrowserDialog` |
| Global keys (while streaming) | `Window.PreviewKeyDown` |
| Persistence | JSON in `%APPDATA%\Snickerstream4Win\settings.json` |
| Icon | `.ico` (see section 6) |

Alternatives: **Avalonia** (cross-platform XAML, modern look) or **WinUI 3** (official Win11
Fluent, but fiddlier setup). For the most "Windows 11" look, use Avalonia.

---

## 3. NTR protocol (the most important part — replicate exactly)

### 3.1 Init over TCP (port 8000)
Connect to `<3DS_IP>:8000` and send **an 84-byte packet**. Then disconnect, wait ~3 s, connect
again and disconnect (sending nothing) — this "kicks" streaming into starting.

Packet layout (byte offsets; little-endian on the wire):

| Offset | Bytes | Meaning |
|---|---|---|
| `0x00` | `78 56 34 12` | magic `0x12345678` |
| `0x04` | `B8 0B 00 00` | seq = `3000` |
| `0x08` | `00 00 00 00` | type = 0 |
| `0x0C` | `85 03 00 00` | cmd = `901` (remoteplay) |
| `0x10` | 1 byte | priority factor (0–10) |
| `0x11` | 1 byte | priority screen (1=top, 0=bottom) |
| `0x14` | 1 byte | JPEG quality (10–100) |
| `0x1A` | 1 byte | QoS **× 2** |
| rest | zeros | total = 84 bytes |

### 3.2 Frames over UDP (PC listens on port 8001)
The 3DS sends datagrams to `<PC_IP>:8001`. Each datagram = **4-byte header + a slice of a JPEG**:

| Byte | Meaning |
|---|---|
| `0` | frame id (increments per frame) |
| `1` | high nibble = `1` on the last packet of a frame; low nibble = screen (`1`=top, `0`=bottom) |
| `2` | format (usually `2`) |
| `3` | packet number within the frame (starts at 0) |

Reassembly **per screen**: start a buffer when `packet_no == 0`; append the payload (from byte 4)
in `packet_no` order; when the high nibble of byte 1 is `1`, the frame is complete. Validate that
it ends with `FF D9` (JPEG EOI) before displaying. If a packet arrives out of order, drop the
frame and wait for the next `packet_no == 0`.

### 3.3 Orientation
The 3DS framebuffers are stored rotated. **Rotate each frame 270° clockwise** to display it
correctly (240×400 → 400×240). Keep rotation configurable (0/90/180/270), default **270**.

---

## 4. HzMod protocol (beta — TCP port 6464)

Connect to `<IP>:6464` (TCP) and send, in this order:

| Packet | Bytes |
|---|---|
| CPU limit | `7E 05 00 00 FF 00 00 00` + 1 byte (cpuLimit 0–255) |
| Quality | `7E 05 00 00 03 00 00 00` + 1 byte (quality 1–100) |
| Start | `7E 05 00 00 00 00 00 00 01` |

The 3DS then streams **JPEG frames over the same TCP connection**. The per-frame header in the
original source is ambiguous, so use a **robust** approach: accumulate the received bytes and
**scan the stream for complete JPEGs** — start `FF D8` to end `FF D9` — emitting each one.
HzMod only streams the **top screen**. Live quality change = re-send the quality packet.

---

## 5. UI — colors, menus, and behavior

**Dark** theme. Window background: vertical gradient from `#1C1C24` → `#131318`.

### 5.1 Palette
| Use | Value |
|---|---|
| **Brand gradient** (Connect button, icon, chips, accents) | `#6638D9` → `#EB4785` (diagonal ↘) |
| Slider tint | `#A840B3` |
| Connect button shadow | `#993399` ~40% |
| Cards | translucent dark panel, **18px** corners, ~8% white border |
| Stream screens | **12px** corners, drop shadow, ~10% white border |
| Selected segment (Top / NTR) | system accent blue (`#0A84FF`) |
| Status dot | green=streaming, orange=connecting, red=failed, gray=idle |
| Radar (find) icon | **purple (brand gradient)**; **green** when the IP was found by the scan |
| FPS badge | green dot when >0; text "rendered / received fps" |

### 5.2 Connect screen (two columns)
- **Header:** app icon + "SnickerStream" (rounded, bold font) + subtitle
  "Nintendo 3DS NTR remoteplay · Windows". Right side: **keyboard** button (shortcuts) and **ⓘ** (about).
- **"Remoteplay" card** (antenna icon):
  - **Segmented protocol selector: NTR / HzMod**.
  - **3DS IP address:** 4 octet boxes (auto-advance after 3 digits, clamp to 255) +
    **bookmark** button (save/remove IP) + **radar** button (scan network).
    Below: **chips** of saved IPs (purple when it's the current one; `×` removes). The radar opens
    a **"Found on network"** popover listing discovered IPs (click fills the field).
  - **NTR:** Priority screen (Top/Bottom), sliders Priority factor (0–10), Image quality (10–100),
    QoS (2–100).
  - **HzMod:** sliders Image quality (1–100), CPU limit (0–255) + beta note (top-screen only).
- **"Display" card** (window icon):
  - **Listen port** (field, default `8001`).
  - **Screen layout** (menu): Stacked / Side by side / Top only / Bottom only.
  - **Interpolation** (menu): Sharp / Linear / Smooth (image scaling filter).
  - **Rotation** (menu): 0° / 90° / 180° / 270° (default **270**).
  - **Max FPS** (numeric field; `0 = ∞`).
  - **Ambient glow** (toggle).
  - **Screenshots:** current folder name + **Choose…** button (folder picker).
- **Footer:** dot + status text (Idle / "Connecting… (n/3)" / error in red).
  **Connect** button (capsule with the brand gradient). While connecting: spinner + **Cancel**.

### 5.3 Streaming screen
- Screens **centered**, rounded corners + shadow. Aspect: top **400:240**, bottom **320:240**.
- **"Ambilight" backdrop:** a **blurred** copy of the frame (blur ~90, saturation ~1.6, opacity
  ~0.40) behind the screens, updated ~**once per second** (not every frame!), with a radial
  vignette on top. Toggleable.
- **Control bar** (translucent material, top border, min height ~56px):
  - **Disconnect** (red) · divider
  - compact menus: **Layout · Filter · Rotation · Max FPS** (`∞/60/30/24/20/15/10` + custom value)
  - divider · **ambient glow** button (sparkles, purple when on) · **keyboard** button (shortcuts)
  - spacer · **FPS badge** ("rendered / received") · status text

### 5.4 Important behaviors
- **Connection watchdog:** states `idle → connecting → streaming / failed`. On connect, show
  "Connecting… (1/3)" and **only switch to the streaming screen when the first frame arrives**.
  If nothing arrives within ~5 s, re-send the init (NTR) / reconnect (HzMod) and retry, up to
  **3×**; then return to the menu with an error. **Never** show a black "connected" screen with
  no frame.
- **Playback FPS cap (per screen):** drop frames **before decoding** if they arrive faster than
  the cap. Count **received** frames (every arrival) and **rendered** frames (those that pass)
  separately — the badge shows both. `0 = unlimited`.
- **Screenshot:** compose according to the layout (Stacked = vertical, Side by side = horizontal,
  Top/Bottom = single screen), save a PNG to the chosen folder named
  `SnickerStream-yyyy-MM-dd_HH-mm-ss.png` (default: `%USERPROFILE%\Pictures\SnickerStream`).
- **Saved IPs:** persist up to **8** recent (dedup, most recent first); auto-save on connect.
- **Auto-discovery:** scan the **/24** subnet for TCP **8000** (NTR) or **6464** (HzMod).
  Details that avoid hangs / scanning the wrong subnet:
  - use **IP-literal connections** (no DNS resolution);
  - **short per-host timeout** (~0.6 s), decoupled from the network callback queue;
  - **skip loopback and link-local** (`169.254.x`); prefer private ranges (10/172.16–31/192.168);
  - bounded concurrency (~48 at a time). A full scan takes ~4 s.

### 5.5 Keyboard shortcuts (remappable, persisted)
| Action | Default (Win) |
|---|---|
| Screenshot | `S` |
| Disconnect | `Esc` |
| Cycle layout | `L` |
| Cycle filter | `F` |
| Rotate screen | `R` |
| Toggle fullscreen | `F11` (was `⌘F` on Mac) |
| Increase quality | `↑` |
| Decrease quality | `↓` |
| Swap priority screen | `P` |

A "Keyboard Shortcuts" menu with a key recorder per action (click → press the key) and a
**Reset to Defaults** button.

---

## 6. Icon
A rounded square (squircle) with the **brand gradient** (`#6638D9`→`#EB4785`); in the center, a
stylized dual-screen "3DS": a light top screen with a **play triangle** (`#F24D73`), and a bottom
screen with **Wi-Fi arcs** (`#4D66F2`). Generate a multi-resolution `.ico` (16–256px).

---

## 7. Windows-specific differences
- **Firewall:** the first time it receives UDP on 8001, Windows may ask to **allow the app on the
  network** — allow it (private networks).
- **Fullscreen:** `F11` (instead of `⌘F`).
- **Default screenshot folder:** `%USERPROFILE%\Pictures\SnickerStream`.
- **SmartScreen:** an unsigned `.exe` may show a warning ("More info → Run anyway"). Signing is
  optional.
- **Persistence:** use JSON in `%APPDATA%` (was `UserDefaults` on Mac).

---

## 8. Build steps (WPF / .NET 8)
```powershell
# 1. Install the .NET 8 SDK: https://dotnet.microsoft.com/download
dotnet --version

# 2. Create the project
dotnet new wpf -n Snickerstream4Win
cd Snickerstream4Win

# 3. (Claude Code implements the code here)

# 4. Run in development
dotnet run

# 5. Produce a distributable .exe
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

---

## 9. How to drive Claude Code on Windows
1. Create a folder, open **Claude Code** in it, and **paste this file** (or ask it to read it).
2. Ask, in this order:
   - "First implement the **NTR protocol layer** (TCP init + UDP receive + reassembly) with a
     headless **self-test** that validates the 84-byte packet and the reassembly of a synthetic JPEG."
   - "Now the **connect screen UI** (two columns, with the colors and menus from the spec)."
   - "Now the **streaming screen** (centered screens, ambilight, control bar)."
   - "Add **auto-discovery**, **HzMod (beta)**, **shortcuts**, **playback FPS cap**, **screenshot
     folder**, and the **connection watchdog**."
3. **Test against the real 3DS** at each step (enable NTR on the console, same network).
4. If the image appears upside down, adjust the rotation (270° is the correct value on Mac).
5. For HzMod, if there are artifacts, the thing to tune is the frame parser (header offsets).

---

*Generated from Snickerstream4Mac (made with Claude). The original AutoIt app is by RattletraPM;
protocol and UX are based on it, under GPLv3.*
