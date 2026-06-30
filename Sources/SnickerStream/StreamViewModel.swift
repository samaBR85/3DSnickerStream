import Foundation
import SwiftUI
import ImageIO
import CoreGraphics

/// Drives a live NTR session and publishes decoded frames to the UI.
@MainActor
final class StreamViewModel: ObservableObject {
    @Published var topImage: CGImage?
    @Published var bottomImage: CGImage?
    @Published var status: String = "Idle"
    @Published var isStreaming = false
    @Published var fps: Int = 0

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

    private var client: NTRClient?

    // FPS accounting (counts top+bottom frames received in the last second).
    private var frameCount = 0
    private var fpsTimer: Timer?

    func connect(config: NTRConfig) {
        disconnect()
        let client = NTRClient(config: config)
        client.onStatus = { [weak self] msg in
            Task { @MainActor in self?.status = msg }
        }
        let rotationBox = self.rotationBox
        client.onFrame = { [weak self] screen, jpeg in
            guard let decoded = Self.decodeJPEG(jpeg) else { return }
            let image = Self.rotate(decoded, clockwiseDegrees: rotationBox.value) ?? decoded
            Task { @MainActor in
                self?.frameCount += 1
                switch screen {
                case .top: self?.topImage = image
                case .bottom: self?.bottomImage = image
                }
            }
        }
        self.client = client
        isStreaming = true
        status = "Connecting to \(config.ip)…"
        client.start()
        startFPSTimer()
    }

    func disconnect() {
        client?.stop()
        client = nil
        isStreaming = false
        fps = 0
        fpsTimer?.invalidate()
        fpsTimer = nil
        status = "Idle"
    }

    private func startFPSTimer() {
        fpsTimer?.invalidate()
        fpsTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self = self else { return }
                self.fps = self.frameCount
                self.frameCount = 0
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
