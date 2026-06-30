import SwiftUI
import QuartzCore

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

/// Brand gradient shared by the icon and the primary action button.
extension LinearGradient {
    static let brand = LinearGradient(
        colors: [Color(red: 0.40, green: 0.22, blue: 0.85), Color(red: 0.92, green: 0.28, blue: 0.52)],
        startPoint: .topLeading, endPoint: .bottomTrailing
    )
}
