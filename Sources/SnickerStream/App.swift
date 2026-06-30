import SwiftUI
import AppKit

@main
struct SnickerStreamApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @StateObject private var model = StreamViewModel()

    init() {
        if CommandLine.arguments.contains("--selftest") {
            exit(SelfTest.run())
        }
    }

    var body: some Scene {
        WindowGroup("SnickerStream") {
            ContentView()
                .environmentObject(model)
                .frame(minWidth: 720, minHeight: 560)
        }
        .windowStyle(.titleBar)
        .windowResizability(.contentMinSize)
    }
}

/// Ensures the SwiftPM executable launches as a normal, focusable GUI app.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        if let url = Bundle.main.url(forResource: "AppIcon", withExtension: "icns"),
           let icon = NSImage(contentsOf: url) {
            NSApp.applicationIconImage = icon
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
}
