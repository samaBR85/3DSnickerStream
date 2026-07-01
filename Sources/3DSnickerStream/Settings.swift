import SwiftUI
import QuartzCore
import AppKit

/// Per-scene window sizing helpers (connect wants a large min, streaming a small one).
enum WindowUtil {
    static func current() -> NSWindow? {
        NSApp.keyWindow ?? NSApp.mainWindow ?? NSApp.windows.first { $0.isVisible }
    }

    /// Sets the window's minimum content size, growing it toward a preferred size if it's smaller.
    static func configure(minWidth: CGFloat, minHeight: CGFloat,
                          preferredWidth: CGFloat? = nil, preferredHeight: CGFloat? = nil) {
        guard let win = current() else { return }
        win.contentMinSize = NSSize(width: minWidth, height: minHeight)
        var size = win.contentRect(forFrameRect: win.frame).size
        if let pw = preferredWidth { size.width = max(size.width, pw) }
        if let ph = preferredHeight { size.height = max(size.height, ph) }
        if let vis = win.screen?.visibleFrame.size {
            size.width = min(size.width, vis.width)
            size.height = min(size.height, vis.height)
        }
        let current = win.contentRect(forFrameRect: win.frame).size
        if abs(size.width - current.width) > 1 || abs(size.height - current.height) > 1 {
            win.setContentSize(size)
        }
    }
}

/// How the two 3DS screens are arranged in the live view.
enum StreamLayout: String, CaseIterable, Identifiable {
    case stacked = "Stacked"
    case sideBySide = "Side by side"
    case topOnly = "Top only"
    case bottomOnly = "Bottom only"
    var id: String { rawValue }
    var symbol: String {
        switch self {
        case .stacked: return "rectangle.grid.1x2"
        case .sideBySide: return "rectangle.split.2x1"
        case .topOnly: return "rectangle.tophalf.inset.filled"
        case .bottomOnly: return "rectangle.bottomhalf.inset.filled"
        }
    }
}

/// Scaling filter applied when the stream is drawn larger than its native resolution —
/// the modern counterpart of the original's "Interpolation" dropdown.
enum Interpolation: String, CaseIterable, Identifiable {
    case nearest = "Sharp"
    case linear = "Linear"
    case smooth = "Smooth"
    var id: String { rawValue }
    var filter: CALayerContentsFilter {
        switch self {
        case .nearest: return .nearest
        case .linear: return .linear
        case .smooth: return .trilinear
        }
    }
}

/// Zoom mode for the streaming view: Fit (scale-to-fill) or a fixed native-resolution multiple.
enum ZoomMode: String, CaseIterable, Identifiable {
    case fit = "Fit"
    case x1 = "100%"
    case x1_5 = "150%"
    case x2 = "200%"
    case x3 = "300%"
    var id: String { rawValue }
    /// Native-resolution multiplier, or nil for Fit.
    var factor: CGFloat? {
        switch self {
        case .fit:  return nil
        case .x1:   return 1
        case .x1_5: return 1.5
        case .x2:   return 2
        case .x3:   return 3
        }
    }
}

/// Per-screen color adjustment (neutral = identity). Applied via Core Image before display.
struct ColorAdjust: Equatable {
    var brightness: Double = 0    // -1…1  (0 neutral)
    var contrast: Double = 1      //  0…2  (1 neutral)
    var saturation: Double = 1    //  0…2  (1 neutral)
    var highlights: Double = 0    //  0…1  (0 = off, tames blown whites)
    var shadows: Double = 0       //  0…1  (0 = off, lifts dark areas)

    var isNeutral: Bool {
        brightness == 0 && contrast == 1 && saturation == 1 && highlights == 0 && shadows == 0
    }
    static let neutral = ColorAdjust()
}

/// Brand gradient shared by the icon and the primary action button.
extension LinearGradient {
    static let brand = LinearGradient(
        colors: [Color(red: 0.40, green: 0.22, blue: 0.85), Color(red: 0.92, green: 0.28, blue: 0.52)],
        startPoint: .topLeading, endPoint: .bottomTrailing
    )
}

/// App version, kept in sync with the published release tags.
enum AppInfo {
    static let version = "1.3"
    static let repo = "samaBR85/3DSnickerStream"
    static var releasesURL: URL { URL(string: "https://github.com/\(repo)/releases")! }
}

/// A quality↔framerate preset: a tuned bundle of priority factor, JPEG quality, and QoS.
/// Built-in values mirror the original Snickerstream presets. Custom ones are user-saved.
struct StreamPreset: Codable, Identifiable, Equatable {
    var name: String
    var priorityTop: Bool
    var priorityFactor: Double
    var quality: Double
    var qos: Double
    var id: String { name }

    /// The seven built-in presets (priorityFactor, quality, QoS), top-screen priority.
    static let builtIn: [StreamPreset] = [
        .init(name: "Best quality",    priorityTop: true, priorityFactor: 2,  quality: 90, qos: 10),
        .init(name: "Great quality",   priorityTop: true, priorityFactor: 5,  quality: 80, qos: 18),
        .init(name: "Good quality",    priorityTop: true, priorityFactor: 5,  quality: 75, qos: 18),
        .init(name: "Balanced",        priorityTop: true, priorityFactor: 5,  quality: 70, qos: 20),
        .init(name: "Good framerate",  priorityTop: true, priorityFactor: 8,  quality: 60, qos: 26),
        .init(name: "Great framerate", priorityTop: true, priorityFactor: 8,  quality: 50, qos: 26),
        .init(name: "Best framerate",  priorityTop: true, priorityFactor: 10, quality: 40, qos: 34)
    ]
}

/// Checks GitHub for a newer release than the running build.
enum UpdateChecker {
    /// Returns the latest release `(tag, htmlURL)` if it's newer than `AppInfo.version`.
    static func newerRelease() async -> (tag: String, url: String)? {
        guard let url = URL(string: "https://api.github.com/repos/\(AppInfo.repo)/releases/latest") else { return nil }
        var req = URLRequest(url: url)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.timeoutInterval = 8
        guard let (data, _) = try? await URLSession.shared.data(for: req),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = json["tag_name"] as? String else { return nil }
        let html = (json["html_url"] as? String) ?? AppInfo.releasesURL.absoluteString
        return isNewer(tag, than: AppInfo.version) ? (tag, html) : nil
    }

    static func isNewer(_ a: String, than b: String) -> Bool {
        func parts(_ s: String) -> [Int] {
            s.lowercased().replacingOccurrences(of: "v", with: "")
                .split(separator: ".").map { Int($0) ?? 0 }
        }
        let x = parts(a), y = parts(b)
        for i in 0..<max(x.count, y.count) {
            let xv = i < x.count ? x[i] : 0, yv = i < y.count ? y[i] : 0
            if xv != yv { return xv > yv }
        }
        return false
    }
}

/// A simple left-to-right flow layout that wraps to new rows — used for the saved-IP chips.
struct FlowLayout: Layout {
    var spacing: CGFloat = 8

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) -> CGSize {
        let maxWidth = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0, widest: CGFloat = 0
        for sub in subviews {
            let size = sub.sizeThatFits(.unspecified)
            if x + size.width > maxWidth, x > 0 {
                x = 0
                y += rowHeight + spacing
                rowHeight = 0
            }
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
            widest = max(widest, x - spacing)
        }
        return CGSize(width: min(widest, maxWidth), height: y + rowHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) {
        var x = bounds.minX, y = bounds.minY, rowHeight: CGFloat = 0
        for sub in subviews {
            let size = sub.sizeThatFits(.unspecified)
            if x + size.width > bounds.maxX, x > bounds.minX {
                x = bounds.minX
                y += rowHeight + spacing
                rowHeight = 0
            }
            sub.place(at: CGPoint(x: x, y: y), anchor: .topLeading, proposal: ProposedViewSize(size))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
    }
}
