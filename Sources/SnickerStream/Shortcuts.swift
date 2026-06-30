import SwiftUI
import AppKit

/// A recorded key combination (a key plus optional ⌘⌥⌃⇧ modifiers).
struct KeyChord: Codable, Equatable {
    var keyCode: UInt16
    var modifiers: UInt          // NSEvent.ModifierFlags raw value, limited to the relevant set
    var display: String

    static let relevant: NSEvent.ModifierFlags = [.command, .option, .control, .shift]

    func matches(_ event: NSEvent) -> Bool {
        let mods = event.modifierFlags.intersection(Self.relevant).rawValue
        return event.keyCode == keyCode && mods == modifiers
    }

    static func from(event: NSEvent) -> KeyChord {
        let mods = event.modifierFlags.intersection(relevant)
        return KeyChord(keyCode: event.keyCode,
                        modifiers: mods.rawValue,
                        display: describe(keyCode: event.keyCode, mods: mods, event: event))
    }

    /// Convenience for defaults defined by key code.
    static func key(_ keyCode: UInt16, _ display: String, command: Bool = false) -> KeyChord {
        KeyChord(keyCode: keyCode,
                 modifiers: command ? NSEvent.ModifierFlags.command.rawValue : 0,
                 display: display)
    }

    static func describe(keyCode: UInt16, mods: NSEvent.ModifierFlags, event: NSEvent?) -> String {
        var s = ""
        if mods.contains(.control) { s += "⌃" }
        if mods.contains(.option)  { s += "⌥" }
        if mods.contains(.shift)   { s += "⇧" }
        if mods.contains(.command) { s += "⌘" }
        return s + keyName(keyCode: keyCode, event: event)
    }

    static func keyName(keyCode: UInt16, event: NSEvent?) -> String {
        if let name = specialKeys[keyCode] { return name }
        if let chars = event?.charactersIgnoringModifiers?.uppercased(),
           let c = chars.first, c != " " {
            return String(c)
        }
        return "key \(keyCode)"
    }

    static let specialKeys: [UInt16: String] = [
        53: "Esc", 49: "Space", 36: "Return", 48: "Tab", 51: "Delete",
        123: "←", 124: "→", 125: "↓", 126: "↑",
        33: "[", 30: "]", 27: "−", 24: "=",
        115: "Home", 119: "End", 116: "Pg Up", 121: "Pg Dn"
    ]
}

/// Every action the user can bind a key to. Default codes are US-layout.
enum ShortcutAction: String, CaseIterable, Identifiable, Codable {
    case screenshot, disconnect, cycleLayout, cycleFilter, rotate
    case toggleFullscreen, increaseQuality, decreaseQuality, swapPriority

    var id: String { rawValue }

    var title: String {
        switch self {
        case .screenshot:       return "Screenshot"
        case .disconnect:       return "Disconnect"
        case .cycleLayout:      return "Cycle layout"
        case .cycleFilter:      return "Cycle filter"
        case .rotate:           return "Rotate screen"
        case .toggleFullscreen: return "Toggle fullscreen"
        case .increaseQuality:  return "Increase quality"
        case .decreaseQuality:  return "Decrease quality"
        case .swapPriority:     return "Swap priority screen"
        }
    }

    var symbol: String {
        switch self {
        case .screenshot:       return "camera"
        case .disconnect:       return "stop.circle"
        case .cycleLayout:      return "rectangle.split.2x2"
        case .cycleFilter:      return "wand.and.rays"
        case .rotate:           return "rotate.right"
        case .toggleFullscreen: return "arrow.up.left.and.arrow.down.right"
        case .increaseQuality:  return "arrow.up.circle"
        case .decreaseQuality:  return "arrow.down.circle"
        case .swapPriority:     return "arrow.left.arrow.right"
        }
    }

    var defaultChord: KeyChord {
        switch self {
        case .screenshot:       return .key(1,  "S")      // S
        case .disconnect:       return .key(53, "Esc")    // Esc
        case .cycleLayout:      return .key(37, "L")      // L
        case .cycleFilter:      return .key(3,  "F")      // F
        case .rotate:           return .key(15, "R")      // R
        case .toggleFullscreen: return .key(3,  "⌘F", command: true)
        case .increaseQuality:  return .key(126, "↑")
        case .decreaseQuality:  return .key(125, "↓")
        case .swapPriority:     return .key(35, "P")      // P
        }
    }
}

/// Holds the user's key bindings, persists them, and matches incoming key events.
@MainActor
final class ShortcutStore: ObservableObject {
    @Published private var chords: [String: KeyChord]

    private let defaultsKey = "shortcutChords"

    init() {
        if let data = UserDefaults.standard.data(forKey: defaultsKey),
           let decoded = try? JSONDecoder().decode([String: KeyChord].self, from: data) {
            chords = decoded
        } else {
            chords = [:]
        }
    }

    func chord(for action: ShortcutAction) -> KeyChord {
        chords[action.rawValue] ?? action.defaultChord
    }

    func set(_ chord: KeyChord, for action: ShortcutAction) {
        chords[action.rawValue] = chord
        persist()
    }

    func resetToDefaults() {
        chords = [:]
        persist()
    }

    /// Returns the action bound to this key event, if any.
    func action(for event: NSEvent) -> ShortcutAction? {
        ShortcutAction.allCases.first { chord(for: $0).matches(event) }
    }

    private func persist() {
        if let data = try? JSONEncoder().encode(chords) {
            UserDefaults.standard.set(data, forKey: defaultsKey)
        }
    }
}
