import Foundation
import SwiftUI
import AppKit
import ImageIO
import CoreGraphics
import CoreImage
import UniformTypeIdentifiers

/// Where the session is in its lifecycle.
enum ConnectionPhase: Equatable {
    case idle          // showing the connect screen
    case connecting    // init sent, waiting for the first frame
    case streaming     // frames are arriving
    case failed        // gave up after retries; back on the connect screen
}

/// Drives a live NTR session and publishes decoded frames to the UI.
@MainActor
final class StreamViewModel: ObservableObject {
    @Published var topImage: CGImage?
    @Published var bottomImage: CGImage?
    /// Low-frequency copy of a frame used only for the blurred ambient backdrop,
    /// so the expensive full-window blur isn't recomputed every frame.
    @Published var backdropImage: CGImage?
    @Published var status: String = "Idle"
    @Published var phase: ConnectionPhase = .idle
    @Published var attempt: Int = 0
    /// Frames actually rendered per second (after the playback-FPS cap).
    @Published var fps: Int = 0
    /// Frames received from the console per second (before the cap) — keeps showing the real rate.
    @Published var receivedFPS: Int = 0

    /// Max playback FPS per screen (0 = unlimited). Caps how many frames are rendered,
    /// independent of how many the console sends. Applied live (no reconnect needed).
    @Published var maxFPS: Int = 0 {
        didSet { maxFPSBox.value = maxFPS }
    }
    private let maxFPSBox = LockedBox<Int>(0)
    /// Per-screen timestamp (uptime ns) of the last rendered frame, read on the frame queue.
    private let lastRenderBox = LockedBox<[Int: UInt64]>([:])
    /// Count of frames received from the console (incremented on the frame queue).
    private let arrivedBox = LockedBox<Int>(0)

    /// How many remoteplay-init attempts before giving up and returning to the menu.
    let maxAttempts = 3
    /// Seconds to wait for a frame before retrying (NTR's own init takes ~3s).
    private let attemptTimeout: TimeInterval = 5

    private var config: StreamConfig?
    private var gotFrame = false
    private var watchdog: Timer?

    /// Display preferences, shared between the connect screen and the live view.
    @Published var layout: StreamLayout = .stacked
    @Published var interpolation: Interpolation = .linear
    /// Relative size of each screen in the stream view (1.0 = equal split).
    @Published var topScale: CGFloat = 1
    @Published var bottomScale: CGFloat = 1

    /// Per-screen color adjustment (applied via Core Image before display).
    @Published var topAdjust = ColorAdjust() { didSet { topAdjustBox.value = topAdjust } }
    @Published var bottomAdjust = ColorAdjust() { didSet { bottomAdjustBox.value = bottomAdjust } }
    private let topAdjustBox = LockedBox<ColorAdjust>(.neutral)
    private let bottomAdjustBox = LockedBox<ColorAdjust>(.neutral)

    /// Gap between the screens in points (0 = touching). Stacked = vertical, side-by-side = horizontal.
    @Published var screenGap: CGFloat = 16
    /// Zoom mode (Fit, or a native-resolution multiple).
    @Published var zoom: ZoomMode = .fit
    /// Clean mode: hide all chrome, show only the screens.
    @Published var cleanMode: Bool = false

    // Reconnect / stale-frame watchdog.
    private var reconnectEnabled = false
    private var staleTimer: Timer?
    private let lastFrameBox = LockedBox<UInt64>(0)
    /// True once the launch-time network scan has run (so it doesn't re-run on every menu return).
    var didStartupScan = false

    /// Clockwise rotation (in degrees, screen sense) applied to every frame.
    /// 3DS framebuffers are stored rotated 90° CCW, so 270° CW restores the upright landscape image.
    /// Changing it takes effect on the next frame — no need to reconnect.
    @Published var rotationDegrees: CGFloat = 270 {
        didSet { rotationBox.value = rotationDegrees }
    }

    /// Thread-safe mirror of `rotationDegrees`, read from the UDP queue during decoding.
    private let rotationBox = LockedBox<CGFloat>(270)

    private var client: (any StreamClient)?

    // FPS accounting (counts top+bottom frames received in the last second).
    private var frameCount = 0
    private var fpsTimer: Timer?

    func connect(config: StreamConfig, reconnect: Bool = false) {
        disconnect()
        reconnectEnabled = reconnect
        self.config = config
        startSession(config, verb: "Connecting")
    }

    /// Builds and starts a client for `config`. Used by both connect() and reconnect().
    private func startSession(_ config: StreamConfig, verb: String) {
        let client: any StreamClient = config.proto == .hzmod
            ? HzModClient(config: config)
            : NTRClient(config: config)
        client.onStatus = { [weak self] msg in
            Task { @MainActor in
                guard let self = self, self.phase == .connecting else { return }
                self.status = msg
            }
        }
        let rotationBox = self.rotationBox
        let maxFPSBox = self.maxFPSBox
        let lastRenderBox = self.lastRenderBox
        let arrivedBox = self.arrivedBox
        let lastFrameBox = self.lastFrameBox
        let topAdjustBox = self.topAdjustBox
        let bottomAdjustBox = self.bottomAdjustBox
        client.onFrame = { [weak self] screen, jpeg in
            arrivedBox.mutate { $0 += 1 }                                  // console frame rate
            lastFrameBox.value = DispatchTime.now().uptimeNanoseconds      // for the stale watchdog

            // Playback-FPS cap: drop frames that arrive faster than the cap, per screen.
            let cap = maxFPSBox.value
            if cap > 0 {
                let now = DispatchTime.now().uptimeNanoseconds
                let minInterval = 1_000_000_000 / UInt64(cap)
                var shouldRender = false
                lastRenderBox.mutate { dict in
                    let last = dict[screen.rawValue] ?? 0
                    if now &- last >= minInterval { dict[screen.rawValue] = now; shouldRender = true }
                }
                if !shouldRender { return }
            }

            guard let decoded = Self.decodeJPEG(jpeg) else { return }
            let rotated = Self.rotate(decoded, clockwiseDegrees: rotationBox.value) ?? decoded
            let adj = (screen == .top) ? topAdjustBox.value : bottomAdjustBox.value
            let image = Self.applyAdjust(rotated, adj)
            Task { @MainActor in
                guard let self = self else { return }
                self.noteFrameArrived()
                self.frameCount += 1
                switch screen {
                case .top: self.topImage = image
                case .bottom: self.bottomImage = image
                }
            }
        }
        self.client = client
        self.gotFrame = false
        self.attempt = 1
        self.phase = .connecting
        self.status = "\(verb) to \(config.ip)… (1/\(maxAttempts))"
        client.start()
        startFPSTimer()
        startWatchdog()
    }

    /// Re-establishes the session with the same config (drops/failed connects with Try-reconnect on).
    private func reconnect() {
        guard let cfg = config else { return }
        stopClientAndTimers()
        startSession(cfg, verb: "Reconnecting")
    }

    /// User-initiated teardown: stops everything and returns to the menu (no auto-reconnect).
    func disconnect() {
        stopClientAndTimers()
        reconnectEnabled = false
        config = nil
        attempt = 0
        phase = .idle
        fps = 0
        receivedFPS = 0
        topImage = nil
        bottomImage = nil
        backdropImage = nil
        status = "Idle"
    }

    /// Tears down the client and all timers without clearing config/reconnect intent.
    private func stopClientAndTimers() {
        watchdog?.invalidate(); watchdog = nil
        staleTimer?.invalidate(); staleTimer = nil
        fpsTimer?.invalidate(); fpsTimer = nil
        client?.stop()
        client = nil
        gotFrame = false
        frameCount = 0
        arrivedBox.value = 0
        lastRenderBox.value = [:]
        lastFrameBox.value = 0
    }

    /// First frame of the session: promote to streaming, stop the connect watchdog, start the stale one.
    private func noteFrameArrived() {
        guard !gotFrame else { return }
        gotFrame = true
        watchdog?.invalidate(); watchdog = nil
        phase = .streaming
        status = "Streaming"
        startStaleWatchdog()
    }

    private func startWatchdog() {
        watchdog?.invalidate()
        watchdog = Timer.scheduledTimer(withTimeInterval: attemptTimeout, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.watchdogFired() }
        }
    }

    private func watchdogFired() {
        guard phase == .connecting, !gotFrame else { return }
        if attempt < maxAttempts {
            attempt += 1
            status = "No response — retrying… (\(attempt)/\(maxAttempts))"
            client?.retry()
            startWatchdog()
        } else if reconnectEnabled {
            // Try-reconnect: keep retrying until it connects or the user cancels.
            attempt = 1
            status = "No response — reconnecting…"
            client?.retry()
            startWatchdog()
        } else {
            let ip = config?.ip ?? ""
            let proto = config?.proto.rawValue ?? "remoteplay"
            stopClientAndTimers()
            fps = 0
            phase = .failed
            status = "No response from \(ip). Check that \(proto) remoteplay is running and the IP is correct."
        }
    }

    /// While streaming with Try-reconnect on, a ~5s gap with no frames (NTR is UDP, no drop signal)
    /// is treated as a dropped stream → reconnect.
    private func startStaleWatchdog() {
        guard reconnectEnabled else { return }
        staleTimer?.invalidate()
        staleTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkStale() }
        }
    }

    private func checkStale() {
        guard phase == .streaming, reconnectEnabled else { return }
        let last = lastFrameBox.value
        let now = DispatchTime.now().uptimeNanoseconds
        if last != 0, now &- last > 5_000_000_000 {
            status = "Stream lost — reconnecting…"
            reconnect()
        }
    }

    // MARK: - Shortcut actions

    func cycleLayout() {
        let all = StreamLayout.allCases
        if let i = all.firstIndex(of: layout) { layout = all[(i + 1) % all.count] }
        flash("Layout: \(layout.rawValue)")
    }

    func cycleFilter() {
        let all = Interpolation.allCases
        if let i = all.firstIndex(of: interpolation) { interpolation = all[(i + 1) % all.count] }
        flash("Filter: \(interpolation.rawValue)")
    }

    func cycleRotation() {
        // Internal values in displayed order (0°, 90°, 180°, 270°), where 0° = upright.
        let opts: [CGFloat] = [270, 0, 90, 180]
        let i = opts.firstIndex(of: rotationDegrees) ?? 0
        rotationDegrees = opts[(i + 1) % opts.count]
        let shown = (Int(rotationDegrees) + 90) % 360   // internal → displayed label
        flash("Rotation: \(shown)°")
    }

    func adjustQuality(by delta: Int) {
        guard var cfg = config else { return }
        let q = min(100, max(10, Int(cfg.quality) + delta))
        cfg.quality = UInt8(q)
        config = cfg
        client?.setQuality(UInt8(q))
        flash("Quality: \(q)")
    }

    func swapPriority() {
        guard var cfg = config else { return }
        cfg.priorityScreen = cfg.priorityScreen == .top ? .bottom : .top
        config = cfg
        client?.swapPriority()
        flash("Priority: \(cfg.priorityScreen == .top ? "Top" : "Bottom")")
    }

    /// Saves the current screen(s) as a PNG in the user-chosen folder (default ~/Pictures/3DSnickerStream).
    func takeScreenshot() {
        guard let image = composeScreenshot() else { flash("Nothing to capture"); return }
        let rep = NSBitmapImageRep(cgImage: image)
        guard let data = rep.representation(using: .png, properties: [:]) else { return }
        let dir = Self.screenshotDirectory()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let name = "3DSnickerStream-\(Self.timestamp()).png"
        let url = dir.appendingPathComponent(name)
        do {
            try data.write(to: url)
            flash("Saved \(name)")
        } catch {
            flash("Screenshot failed: \(error.localizedDescription)")
        }
    }

    /// Copies the current screen(s) to the clipboard (no file written).
    func copyScreenshotToClipboard() {
        guard let image = composeScreenshot() else { flash("Nothing to capture"); return }
        let nsImage = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.writeObjects([nsImage])
        if let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) {
            pb.setData(data, forType: .png)
        }
        flash("Screenshot copied to clipboard")
    }

    /// The configured screenshot folder, falling back to ~/Pictures/3DSnickerStream.
    static func screenshotDirectory() -> URL {
        if let path = UserDefaults.standard.string(forKey: "screenshotFolder"), !path.isEmpty {
            return URL(fileURLWithPath: path, isDirectory: true)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Pictures/3DSnickerStream")
    }

    /// Composites the current frames according to the active layout, using the configured gap.
    private func composeScreenshot() -> CGImage? {
        let gap = max(0, Int(screenGap.rounded()))
        switch layout {
        case .topOnly:    return topImage
        case .bottomOnly: return bottomImage
        case .stacked:    return Self.combine(topImage, bottomImage, vertical: true, gap: gap)
        case .sideBySide: return Self.combine(topImage, bottomImage, vertical: false, gap: gap)
        }
    }

    private static func combine(_ a: CGImage?, _ b: CGImage?, vertical: Bool, gap: Int) -> CGImage? {
        let imgs = [a, b].compactMap { $0 }
        guard !imgs.isEmpty else { return nil }
        if imgs.count == 1 { return imgs[0] }
        let w: Int, h: Int
        if vertical {
            w = max(imgs[0].width, imgs[1].width)
            h = imgs[0].height + imgs[1].height + gap
        } else {
            w = imgs[0].width + imgs[1].width + gap
            h = max(imgs[0].height, imgs[1].height)
        }
        let cs = CGColorSpaceCreateDeviceRGB()
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8,
                                  bytesPerRow: 0, space: cs,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.setFillColor(CGColor(gray: 0, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        if vertical {
            // CG origin is bottom-left: top screen goes on top.
            let topY = imgs[1].height + gap
            ctx.draw(imgs[0], in: CGRect(x: (w - imgs[0].width) / 2, y: topY, width: imgs[0].width, height: imgs[0].height))
            ctx.draw(imgs[1], in: CGRect(x: (w - imgs[1].width) / 2, y: 0, width: imgs[1].width, height: imgs[1].height))
        } else {
            ctx.draw(imgs[0], in: CGRect(x: 0, y: (h - imgs[0].height) / 2, width: imgs[0].width, height: imgs[0].height))
            ctx.draw(imgs[1], in: CGRect(x: imgs[0].width + gap, y: (h - imgs[1].height) / 2, width: imgs[1].width, height: imgs[1].height))
        }
        return ctx.makeImage()
    }

    private static func timestamp() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        return f.string(from: Date())
    }

    /// Shows a transient status message (the control bar reflects `status`).
    private func flash(_ message: String) {
        status = message
    }

    private func startFPSTimer() {
        fpsTimer?.invalidate()
        fpsTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self = self else { return }
                self.fps = self.frameCount            // rendered (after the cap)
                self.frameCount = 0
                self.arrivedBox.mutate { count in     // received (before the cap)
                    self.receivedFPS = count
                    count = 0
                }
                // Refresh the ambient backdrop at 1 Hz (cheap glow, no per-frame blur).
                self.backdropImage = self.topImage ?? self.bottomImage
            }
        }
    }

    // MARK: - Color adjust (Core Image)

    nonisolated static let ciContext = CIContext(options: [.cacheIntermediates: false])

    /// Applies per-screen color adjustment. Returns the original image when neutral (no cost).
    nonisolated static func applyAdjust(_ image: CGImage, _ adj: ColorAdjust) -> CGImage {
        if adj.isNeutral { return image }
        let source = CIImage(cgImage: image)
        var out = source
        if adj.brightness != 0 || adj.contrast != 1 || adj.saturation != 1 {
            let f = CIFilter(name: "CIColorControls")!
            f.setValue(out, forKey: kCIInputImageKey)
            f.setValue(adj.brightness, forKey: kCIInputBrightnessKey)
            f.setValue(adj.contrast, forKey: kCIInputContrastKey)
            f.setValue(adj.saturation, forKey: kCIInputSaturationKey)
            out = f.outputImage ?? out
        }
        if adj.highlights != 0 || adj.shadows != 0 {
            let f = CIFilter(name: "CIHighlightShadowAdjust")!
            f.setValue(out, forKey: kCIInputImageKey)
            f.setValue(1 - adj.highlights, forKey: "inputHighlightAmount")  // 1 = off, lower tames highlights
            f.setValue(adj.shadows, forKey: "inputShadowAmount")           // 0 = off, up to 1 lifts shadows
            out = f.outputImage ?? out
        }
        return ciContext.createCGImage(out, from: source.extent) ?? image
    }

    // MARK: - Clean mode / adjust actions

    func toggleCleanMode() {
        cleanMode.toggle()
    }

    func resetAdjust(top: Bool) {
        if top { topAdjust = .neutral } else { bottomAdjust = .neutral }
    }

    /// Decodes a JPEG `Data` into a `CGImage` using ImageIO (hardware-accelerated on Apple Silicon).
    nonisolated static func decodeJPEG(_ data: Data) -> CGImage? {
        guard let src = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        return CGImageSourceCreateImageAtIndex(src, 0, nil)
    }

    /// Rotates a `CGImage` by a clockwise angle (screen sense), expanding the canvas to fit.
    nonisolated static func rotate(_ image: CGImage, clockwiseDegrees degrees: CGFloat) -> CGImage? {
        if degrees.truncatingRemainder(dividingBy: 360) == 0 { return image }
        // CG rotates counter-clockwise for positive angles, so negate for screen-clockwise.
        let radians = -degrees * .pi / 180
        let w = CGFloat(image.width)
        let h = CGFloat(image.height)
        var box = CGRect(x: 0, y: 0, width: w, height: h)
            .applying(CGAffineTransform(rotationAngle: radians)).size
        box.width = abs(box.width.rounded())
        box.height = abs(box.height.rounded())
        let cs = image.colorSpace ?? CGColorSpaceCreateDeviceRGB()
        guard let ctx = CGContext(
            data: nil,
            width: Int(box.width),
            height: Int(box.height),
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: cs,
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue
        ) else { return nil }
        ctx.interpolationQuality = .high
        ctx.translateBy(x: box.width / 2, y: box.height / 2)
        ctx.rotate(by: radians)
        ctx.draw(image, in: CGRect(x: -w / 2, y: -h / 2, width: w, height: h))
        return ctx.makeImage()
    }
}

/// A minimal lock-guarded value, safe to read/write across threads.
final class LockedBox<T>: @unchecked Sendable {
    private var _value: T
    private let lock = NSLock()
    init(_ value: T) { _value = value }
    var value: T {
        get { lock.lock(); defer { lock.unlock() }; return _value }
        set { lock.lock(); _value = newValue; lock.unlock() }
    }
    /// Atomically read-modify-write (e.g. increment a counter).
    func mutate(_ body: (inout T) -> Void) {
        lock.lock(); body(&_value); lock.unlock()
    }
}
