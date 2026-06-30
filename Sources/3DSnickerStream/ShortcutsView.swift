import SwiftUI
import AppKit

/// The keyboard-shortcuts configurator — a modern take on Snickerstream's "Controls" window.
struct ShortcutsView: View {
    @EnvironmentObject var store: ShortcutStore
    @Environment(\.dismiss) private var dismiss

    @State private var recording: ShortcutAction?
    @State private var monitor: Any?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Label("Keyboard Shortcuts", systemImage: "keyboard")
                    .font(.system(.title2, design: .rounded).bold())
                Spacer()
            }
            .padding(20)

            Divider()

            ScrollView {
                VStack(spacing: 6) {
                    ForEach(ShortcutAction.allCases) { action in
                        row(action)
                    }
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 14)
            }

            Divider()

            HStack {
                Button("Reset to Defaults") {
                    store.resetToDefaults()
                }
                Spacer()
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(20)
        }
        .frame(width: 460, height: 520)
        .onChange(of: recording) { newValue in
            updateRecorder(for: newValue)
        }
        .onDisappear { removeMonitor() }
    }

    private func row(_ action: ShortcutAction) -> some View {
        let isRecording = recording == action
        return HStack {
            Label(action.title, systemImage: action.symbol)
                .labelStyle(.titleAndIcon)
            Spacer()
            Button {
                recording = isRecording ? nil : action
            } label: {
                Text(isRecording ? "Press a key…" : store.chord(for: action).display)
                    .font(.system(.body, design: .rounded).weight(.medium))
                    .monospacedDigit()
                    .frame(minWidth: 120)
                    .padding(.vertical, 7)
                    .background(
                        RoundedRectangle(cornerRadius: 8)
                            .fill(isRecording ? AnyShapeStyle(LinearGradient.brand) : AnyShapeStyle(.quaternary.opacity(0.5)))
                    )
                    .foregroundStyle(isRecording ? Color.white : .primary)
                    .overlay(
                        RoundedRectangle(cornerRadius: 8)
                            .strokeBorder(isRecording ? Color.white.opacity(0.4) : .clear, lineWidth: 1)
                    )
            }
            .buttonStyle(.plain)
        }
        .padding(.vertical, 6)
        .padding(.horizontal, 12)
        .background(RoundedRectangle(cornerRadius: 10).fill(.quaternary.opacity(0.18)))
    }

    private func updateRecorder(for action: ShortcutAction?) {
        removeMonitor()
        guard let action = action else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            let chord = KeyChord.from(event: event)
            store.set(chord, for: action)
            recording = nil
            return nil  // consume the key so it isn't typed elsewhere
        }
    }

    private func removeMonitor() {
        if let monitor = monitor {
            NSEvent.removeMonitor(monitor)
            self.monitor = nil
        }
    }
}
