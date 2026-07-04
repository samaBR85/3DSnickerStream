# Building 3DSnickerStream on macOS

The macOS builds on the [Releases](https://github.com/samaBR85/3DSnickerStream/releases) page are
**unsigned**, so when you download one through a browser, Gatekeeper may block it — on Apple Silicon
it can even say *"3DSnickerStream is damaged and can't be opened"*. That's the quarantine flag, not a
real problem with the file.

**The most reliable fix is to build it yourself.** A locally built app is never quarantined and the
.NET SDK signs it for you, so it just opens. It takes about five minutes. Every command below is
copy-paste — run them in **Terminal** (or ask Claude Desktop on your Mac to run them for you).

---

## 1. Install the prerequisites

```bash
# Xcode Command Line Tools (git, compilers, codesign) — skip if already installed
xcode-select --install

# Homebrew — skip if you already have `brew` (https://brew.sh)
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"

# .NET SDK (builds the app) and Tesseract (enables the OCR "copy text" feature)
brew install dotnet tesseract
```

> The Homebrew `dotnet` formula installs the current .NET SDK, which builds this `net8.0` app fine.
> If `dotnet` isn't found after install, open a new Terminal window (so your PATH refreshes).

## 2. Get the source

```bash
git clone https://github.com/samaBR85/3DSnickerStream.git
cd 3DSnickerStream
```

(Already cloned before? Just update it: `cd 3DSnickerStream && git pull`.)

## 3. Run it

**Quickest — run directly (no packaging):**

```bash
dotnet run --project Snickerstream.Avalonia
```

**Or build a proper `3DSnickerStream.app` you can keep in /Applications:**

```bash
# Apple Silicon (M1–M4). For an Intel Mac, use osx-x64 in both places below.
dotnet publish Snickerstream.Avalonia/Snickerstream.Avalonia.csproj \
  -c Release -r osx-arm64 --self-contained true -o publish/osx-arm64

bash packaging/macos/make-app.sh publish/osx-arm64 dist/3DSnickerStream.app 2.0.0

open dist/3DSnickerStream.app          # launch it
# optional: keep it permanently
# mv dist/3DSnickerStream.app /Applications/
```

`make-app.sh` builds the icon, writes `Info.plist`, and ad-hoc-signs the bundle — so it opens without
the "damaged" error.

---

## Notes

- **OCR:** works once `tesseract` is installed (step 1). Without it, everything else still works and the
  app just tells you OCR needs Tesseract.
- **First launch after moving/downloading:** if macOS ever still blocks a bundle, clear the quarantine
  flag once and open it:
  ```bash
  xattr -cr /path/to/3DSnickerStream.app
  open /path/to/3DSnickerStream.app
  ```
- **Requirements:** a Nintendo 3DS running NTR (via BootNTR) or HzMod, on the same Wi-Fi as your Mac.
