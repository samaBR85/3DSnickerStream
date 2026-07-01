import SwiftUI
import AppKit

/// Live view shown while streaming: the two 3DS screens over an ambient backdrop, plus a control bar.
struct StreamView: View {
    @EnvironmentObject var model: StreamViewModel
    @EnvironmentObject var shortcuts: ShortcutStore

    @State private var showShortcuts = false
    @State private var showAdjust = false
    @State private var showLayoutPopover = false
    @State private var savedWindowFrame: NSRect?
    @AppStorage("ambilight") private var ambilight = true

    private let topAspect: CGFloat = 400.0 / 240.0
    private let bottomAspect: CGFloat = 320.0 / 240.0
    private let nativeTop = CGSize(width: 400, height: 240)
    private let nativeBottom = CGSize(width: 320, height: 240)

    var body: some View {
        Group {
            if model.cleanMode {
                screensContainer
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color.black)
            } else {
                VStack(spacing: 0) {
                    HStack(spacing: 0) {
                        screensContainer
                            .padding(24)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .background(AmbientBackdrop(image: ambilight ? model.backdropImage : nil).equatable())
                            .clipped()
                        if showAdjust {
                            adjustPanel
                                .frame(width: 236)
                                .background(.ultraThinMaterial)
                        }
                    }
                    controlBar
                }
            }
        }
        .background(Color.black)
        .background(KeyCatcher(handler: handleKey))
        .sheet(isPresented: $showShortcuts) { ShortcutsView() }
        .onChange(of: model.cleanMode) { clean in applyCleanMode(clean) }
        .onChange(of: model.zoom) { _ in fitWindowToZoom() }
        .onChange(of: showAdjust) { _ in fitWindowToZoom() }
    }

    /// Maps a key event to a bound action; returns nil to consume it.
    private func handleKey(_ event: NSEvent) -> NSEvent? {
        guard !showShortcuts else { return event }
        // In clean mode, Esc restores the UI instead of disconnecting.
        if model.cleanMode, event.keyCode == 53 { model.cleanMode = false; return nil }
        guard let action = shortcuts.action(for: event) else { return event }
        switch action {
        case .screenshot:            model.takeScreenshot()
        case .screenshotToClipboard: model.copyScreenshotToClipboard()
        case .disconnect:            model.disconnect()
        case .cycleLayout:           model.cycleLayout()
        case .cycleFilter:           model.cycleFilter()
        case .rotate:                model.cycleRotation()
        case .toggleFullscreen:      (event.window ?? NSApp.keyWindow)?.toggleFullScreen(nil)
        case .increaseQuality:       model.adjustQuality(by: 5)
        case .decreaseQuality:       model.adjustQuality(by: -5)
        case .swapPriority:          model.swapPriority()
        case .cleanMode:             model.toggleCleanMode()
        }
        return nil
    }

    // MARK: - Screens

    @ViewBuilder
    private var screensContainer: some View {
        if let factor = model.zoom.factor {
            zoomedScreens(factor)
        } else {
            fitScreens
        }
    }

    /// Fit mode: the screens share the available space by their scale weights.
    @ViewBuilder
    private var fitScreens: some View {
        let gap = model.screenGap
        let tW = max(0.1, model.topScale)
        let bW = max(0.1, model.bottomScale)
        switch model.layout {
        case .stacked:
            GeometryReader { geo in
                let avail = geo.size.height - gap
                VStack(spacing: gap) {
                    screen(model.topImage, aspect: topAspect).frame(height: avail * tW / (tW + bW))
                    screen(model.bottomImage, aspect: bottomAspect).frame(height: avail * bW / (tW + bW))
                }
                .frame(width: geo.size.width, height: geo.size.height)
            }
        case .sideBySide:
            GeometryReader { geo in
                let avail = geo.size.width - gap
                HStack(spacing: gap) {
                    screen(model.topImage, aspect: topAspect).frame(width: avail * tW / (tW + bW))
                    screen(model.bottomImage, aspect: bottomAspect).frame(width: avail * bW / (tW + bW))
                }
                .frame(width: geo.size.width, height: geo.size.height)
            }
        case .topOnly:    screen(model.topImage, aspect: topAspect)
        case .bottomOnly: screen(model.bottomImage, aspect: bottomAspect)
        }
    }

    /// Fixed zoom: each screen at native × zoom × per-screen scale, crisp (nearest).
    @ViewBuilder
    private func zoomedScreens(_ factor: CGFloat) -> some View {
        let gap = model.screenGap
        let top = fixedScreen(model.topImage, native: nativeTop, factor: factor, scale: model.topScale)
        let bottom = fixedScreen(model.bottomImage, native: nativeBottom, factor: factor, scale: model.bottomScale)
        switch model.layout {
        case .stacked:    VStack(spacing: gap) { top; bottom }
        case .sideBySide: HStack(spacing: gap) { top; bottom }
        case .topOnly:    top
        case .bottomOnly: bottom
        }
    }

    private func fixedScreen(_ image: CGImage?, native: CGSize, factor: CGFloat, scale: CGFloat) -> some View {
        ScreenView(image: image, aspect: native.width / native.height, filter: .nearest, cornerRadius: 12)
            .frame(width: native.width * factor * scale, height: native.height * factor * scale)
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.white.opacity(0.10), lineWidth: 1))
            .shadow(color: .black.opacity(0.6), radius: 22, y: 10)
    }

    private func screen(_ image: CGImage?, aspect: CGFloat) -> some View {
        ScreenView(image: image, aspect: aspect, filter: model.interpolation.filter, cornerRadius: 12)
            .aspectRatio(aspect, contentMode: .fit)
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.white.opacity(0.10), lineWidth: 1))
            .shadow(color: .black.opacity(0.6), radius: 22, y: 10)
    }

    // MARK: - Adjust panel

    private var adjustPanel: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                adjustSection("Top screen", binding: $model.topAdjust) { model.resetAdjust(top: true) }
                Divider()
                adjustSection("Bottom screen", binding: $model.bottomAdjust) { model.resetAdjust(top: false) }
            }
            .padding(16)
        }
    }

    private func adjustSection(_ title: String, binding: Binding<ColorAdjust>, reset: @escaping () -> Void) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(title).font(.headline)
                Spacer()
                Button("Reset", action: reset).controlSize(.small)
            }
            adjSlider("Brightness", binding.brightness, -1...1)
            adjSlider("Contrast", binding.contrast, 0...2)
            adjSlider("Saturation", binding.saturation, 0...2)
            adjSlider("Highlights", binding.highlights, 0...1)
            adjSlider("Shadows", binding.shadows, 0...1)
        }
    }

    private func adjSlider(_ label: String, _ value: Binding<Double>, _ range: ClosedRange<Double>) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text(label).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Text(String(format: "%.2f", value.wrappedValue)).font(.caption2).monospacedDigit().foregroundStyle(.tertiary)
            }
            Slider(value: value, in: range).tint(Color(red: 0.66, green: 0.25, blue: 0.7))
        }
    }

    // MARK: - Control bar

    private var controlBar: some View {
        HStack(spacing: 14) {
            Button(role: .destructive, action: model.disconnect) {
                Label("Disconnect", systemImage: "stop.fill").font(.callout.weight(.medium))
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
                Text("0°").tag(CGFloat(270))
                Text("90°").tag(CGFloat(0))
                Text("180°").tag(CGFloat(90))
                Text("270°").tag(CGFloat(180))
            }
            compactPicker("plus.magnifyingglass", selection: $model.zoom) {
                ForEach(ZoomMode.allCases) { Text($0.rawValue).tag($0) }
            }
            compactPicker("gauge.with.needle", selection: $model.maxFPS) {
                Text("∞").tag(0)
                ForEach([60, 30, 24, 20, 15, 10], id: \.self) { Text("\($0)").tag($0) }
                if ![0, 60, 30, 24, 20, 15, 10].contains(model.maxFPS) {
                    Text("\(model.maxFPS)").tag(model.maxFPS)
                }
            }

            Divider().frame(height: 20)

            // Gap + per-screen scale live sliders (popover to save bar space).
            Button { showLayoutPopover.toggle() } label: {
                Image(systemName: "arrow.up.and.down.and.arrow.left.and.right").font(.body).foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("Gap & scale")
            .popover(isPresented: $showLayoutPopover, arrowEdge: .top) { layoutPopover }

            Button { showAdjust.toggle() } label: {
                Image(systemName: "slider.horizontal.3")
                    .font(.body)
                    .foregroundStyle(showAdjust ? AnyShapeStyle(LinearGradient.brand) : AnyShapeStyle(.secondary))
            }
            .buttonStyle(.plain)
            .help("Adjust colors")

            Button { ambilight.toggle() } label: {
                Image(systemName: "sparkles")
                    .font(.body)
                    .foregroundStyle(ambilight ? AnyShapeStyle(LinearGradient.brand) : AnyShapeStyle(.secondary))
            }
            .buttonStyle(.plain)
            .help(ambilight ? "Ambient glow: on" : "Ambient glow: off")

            Button { showShortcuts = true } label: {
                Image(systemName: "keyboard").font(.body).foregroundStyle(.secondary)
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
                .frame(maxWidth: 200, alignment: .trailing)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .frame(minHeight: 56)
        .background(.ultraThinMaterial)
        .overlay(Rectangle().frame(height: 1).foregroundStyle(.white.opacity(0.08)), alignment: .top)
    }

    private var layoutPopover: some View {
        VStack(alignment: .leading, spacing: 14) {
            popSlider("Gap", $model.screenGap, 0...64, suffix: "pt")
            popSlider("Top scale", $model.topScale, 0.5...2, suffix: "×", decimals: 1)
            popSlider("Bottom scale", $model.bottomScale, 0.5...2, suffix: "×", decimals: 1)
        }
        .padding(16)
        .frame(width: 240)
    }

    private func popSlider(_ label: String, _ value: Binding<CGFloat>, _ range: ClosedRange<CGFloat>,
                           suffix: String, decimals: Int = 0) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(label).foregroundStyle(.secondary)
                Spacer()
                Text(String(format: "%.\(decimals)f\(suffix)", value.wrappedValue)).monospacedDigit()
            }
            Slider(value: value, in: range).tint(Color(red: 0.66, green: 0.25, blue: 0.7))
        }
    }

    // MARK: - Window sizing (zoom / clean mode)

    private func currentWindow() -> NSWindow? { NSApp.keyWindow ?? NSApp.mainWindow }

    /// Aspect ratio (w/h) of the current screen group, used for clean-mode sizing.
    private var groupAspect: CGFloat {
        let g = model.screenGap, tS = model.topScale, bS = model.bottomScale
        let tW = nativeTop.width * tS, tH = nativeTop.height * tS
        let bW = nativeBottom.width * bS, bH = nativeBottom.height * bS
        switch model.layout {
        case .topOnly:    return topAspect
        case .bottomOnly: return bottomAspect
        case .stacked:    return max(tW, bW) / (tH + g + bH)
        case .sideBySide: return (tW + g + bW) / max(tH, bH)
        }
    }

    /// Point size of the screen group at the given zoom factor.
    private func groupSize(factor: CGFloat) -> CGSize {
        let g = model.screenGap, tS = model.topScale, bS = model.bottomScale
        let tW = nativeTop.width * tS * factor, tH = nativeTop.height * tS * factor
        let bW = nativeBottom.width * bS * factor, bH = nativeBottom.height * bS * factor
        switch model.layout {
        case .topOnly:    return CGSize(width: tW, height: tH)
        case .bottomOnly: return CGSize(width: bW, height: bH)
        case .stacked:    return CGSize(width: max(tW, bW), height: tH + g + bH)
        case .sideBySide: return CGSize(width: tW + g + bW, height: max(tH, bH))
        }
    }

    private func fitWindowToZoom() {
        guard let factor = model.zoom.factor, let win = currentWindow() else { return }
        let content = groupSize(factor: factor)
        var target = NSSize(width: content.width + (showAdjust ? 236 : 0) + 48,
                            height: content.height + 56 + 48)
        target.width = max(target.width, 720)
        target.height = max(target.height, 560)
        if let vis = win.screen?.visibleFrame.size {
            target.width = min(target.width, vis.width)
            target.height = min(target.height, vis.height)
        }
        // Grow only (never shrink below the current size).
        let cur = win.contentRect(forFrameRect: win.frame).size
        target.width = max(target.width, cur.width)
        target.height = max(target.height, cur.height)
        win.setContentSize(target)
    }

    private func applyCleanMode(_ clean: Bool) {
        guard let win = currentWindow() else { return }
        if clean {
            savedWindowFrame = win.frame
            let contentW = win.contentRect(forFrameRect: win.frame).width
            let size = NSSize(width: contentW, height: contentW / max(0.1, groupAspect))
            win.setContentSize(size)
        } else if let frame = savedWindowFrame {
            win.setFrame(frame, display: true, animate: true)
            savedWindowFrame = nil
        }
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
