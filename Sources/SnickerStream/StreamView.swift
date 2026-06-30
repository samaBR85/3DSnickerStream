import SwiftUI
import AppKit

/// Live view shown while streaming: the two 3DS screens over an ambient backdrop, plus a control bar.
struct StreamView: View {
    @EnvironmentObject var model: StreamViewModel
    @EnvironmentObject var shortcuts: ShortcutStore

    @State private var showShortcuts = false
    @AppStorage("ambilight") private var ambilight = true

    private let topAspect: CGFloat = 400.0 / 240.0
    private let bottomAspect: CGFloat = 320.0 / 240.0

    var body: some View {
        VStack(spacing: 0) {
            screens
                .padding(24)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(AmbientBackdrop(image: ambilight ? model.backdropImage : nil).equatable())
                .clipped()
            controlBar
        }
        .background(Color.black)
        .background(KeyCatcher(handler: handleKey))
        .sheet(isPresented: $showShortcuts) { ShortcutsView() }
    }

    /// Maps a key event to a bound action; returns nil to consume it.
    private func handleKey(_ event: NSEvent) -> NSEvent? {
        // Don't act on keys while the shortcuts sheet is open (it captures keys itself).
        guard !showShortcuts else { return event }
        guard let action = shortcuts.action(for: event) else { return event }
        switch action {
        case .screenshot:       model.takeScreenshot()
        case .disconnect:       model.disconnect()
        case .cycleLayout:      model.cycleLayout()
        case .cycleFilter:      model.cycleFilter()
        case .rotate:           model.cycleRotation()
        case .toggleFullscreen: (event.window ?? NSApp.keyWindow)?.toggleFullScreen(nil)
        case .increaseQuality:  model.adjustQuality(by: 5)
        case .decreaseQuality:  model.adjustQuality(by: -5)
        case .swapPriority:     model.swapPriority()
        }
        return nil
    }

    // MARK: - Screens

    @ViewBuilder
    private var screens: some View {
        switch model.layout {
        case .stacked:
            VStack(spacing: 14) {
                screen(model.topImage, aspect: topAspect)
                screen(model.bottomImage, aspect: bottomAspect)
            }
        case .sideBySide:
            HStack(spacing: 14) {
                screen(model.topImage, aspect: topAspect)
                screen(model.bottomImage, aspect: bottomAspect)
            }
        case .topOnly:
            screen(model.topImage, aspect: topAspect)
        case .bottomOnly:
            screen(model.bottomImage, aspect: bottomAspect)
        }
    }

    private func screen(_ image: CGImage?, aspect: CGFloat) -> some View {
        ScreenView(image: image, aspect: aspect, filter: model.interpolation.filter, cornerRadius: 12)
            .aspectRatio(aspect, contentMode: .fit)
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(.white.opacity(0.10), lineWidth: 1)
            )
            .shadow(color: .black.opacity(0.6), radius: 22, y: 10)
    }

    // MARK: - Control bar

    private var controlBar: some View {
        HStack(spacing: 16) {
            Button(role: .destructive, action: model.disconnect) {
                Label("Disconnect", systemImage: "stop.fill")
                    .font(.callout.weight(.medium))
            }
            .buttonStyle(.borderedProminent)
            .tint(.red)

            Divider().frame(height: 20)

            compactPicker("rectangle.split.2x2", selection: $model.layout) {
                ForEach(StreamLayout.allCases) { Label($0.rawValue, systemImage: $0.symbol).tag($0) }
            }
            compactPicker("wand.and.rays", selection: $model.interpolation) {
                ForEach(Interpolation.allCases) { Text($0.rawValue).tag($0) }
            }
            compactPicker("rotate.right", selection: $model.rotationDegrees) {
                Text("0°").tag(CGFloat(0))
                Text("90°").tag(CGFloat(90))
                Text("180°").tag(CGFloat(180))
                Text("270°").tag(CGFloat(270))
            }
            compactPicker("gauge.with.needle", selection: $model.maxFPS) {
                Text("∞").tag(0)
                ForEach([60, 30, 24, 20, 15, 10], id: \.self) { Text("\($0)").tag($0) }
                // Show a custom value (set via the connect screen) so it stays selected.
                if ![0, 60, 30, 24, 20, 15, 10].contains(model.maxFPS) {
                    Text("\(model.maxFPS)").tag(model.maxFPS)
                }
            }

            Divider().frame(height: 20)

            Button { ambilight.toggle() } label: {
                Image(systemName: "sparkles")
                    .font(.body)
                    .foregroundStyle(ambilight ? AnyShapeStyle(LinearGradient.brand) : AnyShapeStyle(.secondary))
            }
            .buttonStyle(.plain)
            .help(ambilight ? "Ambient glow: on" : "Ambient glow: off")

            Button { showShortcuts = true } label: {
                Image(systemName: "keyboard")
                    .font(.body)
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("Keyboard shortcuts")

            Spacer()

            liveBadge

            Text(model.status)
                .font(.callout)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.tail)
                .frame(maxWidth: 240, alignment: .trailing)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 14)
        .frame(minHeight: 56)
        .background(.ultraThinMaterial)
        .overlay(Rectangle().frame(height: 1).foregroundStyle(.white.opacity(0.08)), alignment: .top)
    }

    private var liveBadge: some View {
        // Rendered fps in front, received (real console rate) muted behind a slash.
        HStack(spacing: 7) {
            Circle()
                .fill(model.fps > 0 ? Color.green : Color.gray)
                .frame(width: 8, height: 8)
                .shadow(color: model.fps > 0 ? .green.opacity(0.8) : .clear, radius: 4)
            HStack(spacing: 2) {
                Text("\(model.fps)")
                    .font(.callout.weight(.semibold))
                    .monospacedDigit()
                Text("/ \(model.receivedFPS)")
                    .font(.caption)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
            Text("fps")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(.quaternary.opacity(0.6), in: Capsule())
        .help("Rendered / received frames per second")
    }

    private func compactPicker<S: Hashable, C: View>(_ icon: String, selection: Binding<S>,
                                                     @ViewBuilder _ content: () -> C) -> some View {
        HStack(spacing: 5) {
            Image(systemName: icon).font(.caption).foregroundStyle(.secondary)
            Picker("", selection: selection) { content() }
                .labelsHidden()
                .fixedSize()
        }
    }
}

/// Renders a `CGImage` directly into a layer-backed view — avoids per-frame SwiftUI image
/// diffing so the stream stays smooth at 30+ fps.
struct ScreenView: NSViewRepresentable {
    let image: CGImage?
    let aspect: CGFloat
    let filter: CALayerContentsFilter
    var cornerRadius: CGFloat = 0

    func makeNSView(context: Context) -> AspectImageView {
        let view = AspectImageView()
        view.aspect = aspect
        return view
    }

    func updateNSView(_ view: AspectImageView, context: Context) {
        view.aspect = aspect
        view.setCornerRadius(cornerRadius)
        view.setFilter(filter)
        view.cgImage = image
    }
}

/// A layer-backed view that displays a CGImage scaled to fill its (aspect-correct) frame.
final class AspectImageView: NSView {
    var aspect: CGFloat = 400.0 / 240.0
    var cgImage: CGImage? {
        didSet { updateContents() }
    }

    override var wantsUpdateLayer: Bool { true }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.contentsGravity = .resizeAspect
        layer?.backgroundColor = NSColor.black.cgColor
        layer?.magnificationFilter = .linear
        layer?.minificationFilter = .linear
        layer?.masksToBounds = true
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        wantsLayer = true
        layer?.contentsGravity = .resizeAspect
        layer?.masksToBounds = true
    }

    func setFilter(_ filter: CALayerContentsFilter) {
        layer?.magnificationFilter = filter
        layer?.minificationFilter = filter
    }

    func setCornerRadius(_ radius: CGFloat) {
        layer?.cornerRadius = radius
    }

    private func updateContents() {
        layer?.contents = cgImage
    }

    override func updateLayer() {
        layer?.contents = cgImage
    }
}

/// Blurred ambient glow behind the screens. Equatable so SwiftUI only re-renders the
/// expensive blur when the (1 Hz) backdrop image actually changes — not on every frame.
struct AmbientBackdrop: View, Equatable {
    let image: CGImage?

    static func == (lhs: AmbientBackdrop, rhs: AmbientBackdrop) -> Bool {
        lhs.image === rhs.image
    }

    var body: some View {
        ZStack {
            Color.black
            if let image {
                Image(decorative: image, scale: 1)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .blur(radius: 90)
                    .saturation(1.6)
                    .opacity(0.40)
                    .drawingGroup()   // rasterize the blur once on the GPU
            }
            // Vignette so the screens stay the focal point.
            RadialGradient(colors: [.clear, .black.opacity(0.55)],
                           center: .center, startRadius: 100, endRadius: 700)
        }
        .animation(.easeOut(duration: 0.4), value: image == nil)
    }
}

/// Invisible view that installs a local key-down monitor for the lifetime of the stream view.
struct KeyCatcher: NSViewRepresentable {
    let handler: (NSEvent) -> NSEvent?

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        context.coordinator.monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: handler)
        return NSView()
    }

    func updateNSView(_ nsView: NSView, context: Context) {}

    static func dismantleNSView(_ nsView: NSView, coordinator: Coordinator) {
        if let monitor = coordinator.monitor {
            NSEvent.removeMonitor(monitor)
        }
    }

    final class Coordinator {
        var monitor: Any?
    }
}
