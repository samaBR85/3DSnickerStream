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
