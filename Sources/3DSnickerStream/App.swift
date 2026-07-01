import SwiftUI
import AppKit

@main
struct ThreeDSnickerStreamApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @StateObject private var model = StreamViewModel()
    @StateObject private var shortcuts = ShortcutStore()

    init() {
        if CommandLine.arguments.contains("--selftest") {
            exit(SelfTest.run())
        }
        if CommandLine.arguments.contains("--scan") {
            setbuf(stdout, nil)
            print("Local IP:", NetworkScanner.localIPv4Address() ?? "unknown")
            print("Scanning subnet for a 3DS (ports 8000/6464)…")
            Task {
                let found = await NetworkScanner.scan()
                print(found.isEmpty ? "No devices found." : "Found: \(found.joined(separator: ", "))")
                exit(0)
            }
            RunLoop.main.run()   // pump the runloop so the async Task can execute
        }
    }

    var body: some Scene {
        WindowGroup("3DSnickerStream") {
            ContentView()
                .environmentObject(model)
                .environmentObject(shortcuts)
                // Window min size is set per scene (large for connect, small for streaming) in each
                // view's onAppear, so streaming can shrink to ~400 pt while the home stays full.
        }
        .windowStyle(.titleBar)
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
