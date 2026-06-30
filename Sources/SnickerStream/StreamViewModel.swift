import Foundation
import SwiftUI
import AppKit
import ImageIO
import CoreGraphics
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
    @Published var fps: Int = 0

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

    func connect(config: StreamConfig) {
        disconnect()
        self.config = config
        let client: any StreamClient = config.proto == .hzmod
            ? HzModClient(config: config)
            : NTRClient(config: config)
        client.onStatus = { [weak self] msg in
            // Only surface low-level status while still connecting.
            Task { @MainActor in
                guard let self = self, self.phase == .connecting else { return }
                self.status = msg
            }
        }
        let rotationBox = self.rotationBox
        client.onFrame = { [weak self] screen, jpeg in
            guard let decoded = Self.decodeJPEG(jpeg) else { return }
            let image = Self.rotate(decoded, clockwiseDegrees: rotationBox.value) ?? decoded
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
        self.status = "Connecting to \(config.ip)… (1/\(maxAttempts))"
        client.start()
        startFPSTimer()
        startWatchdog()
    }

    func disconnect() {
        watchdog?.invalidate()
        watchdog = nil
        client?.stop()
        client = nil
        config = nil
        gotFrame = false
        attempt = 0
        phase = .idle
        fps = 0
        topImage = nil
        bottomImage = nil
        backdropImage = nil
        fpsTimer?.invalidate()
        fpsTimer = nil
        status = "Idle"
    }

    /// First frame of the session: promote to streaming and stop the watchdog.
    private func noteFrameArrived() {
        guard !gotFrame else { return }
        gotFrame = true
        watchdog?.invalidate()
        watchdog = nil
        phase = .streaming
        status = "Streaming"
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
        } else {
            // Give up and return to the connect screen.
            let ip = config?.ip ?? ""
            client?.stop()
            client = nil
            watchdog?.invalidate()
            watchdog = nil
            fpsTimer?.invalidate()
            fpsTimer = nil
            fps = 0
            let proto = config?.proto.rawValue ?? "remoteplay"
            phase = .failed
            status = "No response from \(ip). Check that \(proto) remoteplay is running and the IP is correct."
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
        let opts: [CGFloat] = [0, 90, 180, 270]
        let i = opts.firstIndex(of: rotationDegrees) ?? opts.count - 1
        rotationDegrees = opts[(i + 1) % opts.count]
        flash("Rotation: \(Int(rotationDegrees))°")
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

    /// Saves the current screen(s) as a PNG in ~/Pictures/SnickerStream.
    func takeScreenshot() {
        guard let image = composeScreenshot() else { flash("Nothing to capture"); return }
        let rep = NSBitmapImageRep(cgImage: image)
        guard let data = rep.representation(using: .png, properties: [:]) else { return }
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Pictures/SnickerStream")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let name = "SnickerStream-\(Self.timestamp()).png"
        let url = dir.appendingPathComponent(name)
        do {
            try data.write(to: url)
            flash("Saved \(name)")
        } catch {
            flash("Screenshot failed: \(error.localizedDescription)")
        }
    }

    /// Composites the current frames according to the active layout.
    private func composeScreenshot() -> CGImage? {
        switch layout {
        case .topOnly:    return topImage
        case .bottomOnly: return bottomImage
        case .stacked:    return Self.combine(topImage, bottomImage, vertical: true)
        case .sideBySide: return Self.combine(topImage, bottomImage, vertical: false)
        }
    }

    private static func combine(_ a: CGImage?, _ b: CGImage?, vertical: Bool) -> CGImage? {
        let imgs = [a, b].compactMap { $0 }
        guard !imgs.isEmpty else { return nil }
        if imgs.count == 1 { return imgs[0] }
        let gap = 6
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
                self.fps = self.frameCount
                self.frameCount = 0
                // Refresh the ambient backdrop at 1 Hz (cheap glow, no per-frame blur).
                self.backdropImage = self.topImage ?? self.bottomImage
            }
        }
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
}
