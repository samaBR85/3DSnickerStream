import SwiftUI
import AppKit

/// The connection screen — a modern reimagining of Snickerstream's two-column connect dialog.
struct ConnectView: View {
    @EnvironmentObject var model: StreamViewModel

    @AppStorage("ip") private var ip: String = "192.168.1.10"
    @AppStorage("priorityTop") private var priorityTop: Bool = true
    @AppStorage("priorityFactor") private var priorityFactor: Double = 5
    @AppStorage("quality") private var quality: Double = 70
    @AppStorage("qos") private var qos: Double = 20
    @AppStorage("listenPort") private var listenPort: String = "8001"
    @AppStorage("layoutRaw") private var layoutRaw: String = StreamLayout.stacked.rawValue
    @AppStorage("interpRaw") private var interpRaw: String = Interpolation.linear.rawValue
    @AppStorage("rotation") private var rotation: Double = 270
    @AppStorage("savedIPs") private var savedIPsRaw: String = ""
    @AppStorage("ambilight") private var ambilight = true
    @AppStorage("protocol") private var protoRaw: String = StreamProtocol.ntr.rawValue
    @AppStorage("cpuLimit") private var cpuLimit: Double = 128
    @AppStorage("maxFPS") private var maxFPS: Int = 0
    @AppStorage("screenshotFolder") private var screenshotFolder: String = ""
    @AppStorage("topScale") private var topScale: Double = 1
    @AppStorage("bottomScale") private var bottomScale: Double = 1
    @AppStorage("checkUpdates") private var checkUpdates: Bool = true
    @AppStorage("customPresets") private var customPresetsRaw: String = "[]"
    @AppStorage("scanOnStartup") private var scanOnStartup: Bool = false
    @AppStorage("autoConnect") private var autoConnect: Bool = false
    @AppStorage("tryReconnect") private var tryReconnect: Bool = false

    @State private var showAbout = false
    @State private var showShortcuts = false

    // Presets.
    @State private var showSavePreset = false
    @State private var newPresetName = ""

    // Update check.
    @State private var update: (tag: String, url: String)?

    // Network auto-discovery state.
    @State private var scanning = false
    @State private var discovered: [String] = []
    @State private var hasScanned = false

    private var proto: StreamProtocol { StreamProtocol(rawValue: protoRaw) ?? .ntr }

    var body: some View {
        ZStack {
            LinearGradient(colors: [Color(nsColor: .windowBackgroundColor),
                                    Color(nsColor: .underPageBackgroundColor)],
                           startPoint: .top, endPoint: .bottom)
                .ignoresSafeArea()

            VStack(alignment: .leading, spacing: 16) {
                header

                if let update = update { updateBanner(update) }

                // Scrollable safety net so no control is ever unreachable on a short window.
                ScrollView(.vertical, showsIndicators: false) {
                    HStack(alignment: .top, spacing: 16) {
                        remoteplayCard
                        displayCard
                    }
                }

                footer
            }
            .padding(24)
        }
        .onAppear {
            WindowUtil.configure(minWidth: 700, minHeight: 560, preferredWidth: 760, preferredHeight: 840)
        }
        .popover(isPresented: $showAbout, arrowEdge: .bottom) { aboutPopover }
        .sheet(isPresented: $showShortcuts) { ShortcutsView() }
        .alert("Add custom preset", isPresented: $showSavePreset) {
            TextField("Preset name", text: $newPresetName)
            Button("Save") { savePreset(named: newPresetName); newPresetName = "" }
            Button("Cancel", role: .cancel) { newPresetName = "" }
        } message: {
            Text("Save the current priority factor, image quality, and QoS as a named preset.")
        }
        .task {
            startupScanIfNeeded()
            guard checkUpdates else { return }
            update = await UpdateChecker.newerRelease()
        }
    }

    private func updateBanner(_ u: (tag: String, url: String)) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "arrow.down.circle.fill").foregroundStyle(LinearGradient.brand)
            Text("Update available — **\(u.tag)**").font(.callout)
            Spacer()
            Button("Download") { NSWorkspace.shared.open(URL(string: u.url) ?? AppInfo.releasesURL) }
                .controlSize(.small)
            Button { update = nil } label: { Image(systemName: "xmark") }
                .buttonStyle(.plain).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.white.opacity(0.10)))
    }

    // MARK: - Header

    private var header: some View {
        HStack(spacing: 16) {
            appIcon.frame(width: 60, height: 60)
            VStack(alignment: .leading, spacing: 2) {
                Text("3DSnickerStream")
                    .font(.system(size: 30, weight: .bold, design: .rounded))
                Text("Nintendo 3DS NTR remoteplay · Apple Silicon")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            HStack(spacing: 14) {
                Button { showShortcuts = true } label: {
                    Image(systemName: "keyboard")
                        .font(.title2)
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Keyboard shortcuts")

                Button { showAbout.toggle() } label: {
                    Image(systemName: "info.circle")
                        .font(.title2)
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("About 3DSnickerStream")
            }
        }
    }

    // MARK: - Quality / framerate presets

    private var presetRow: some View {
        HStack {
            Label("Preset", systemImage: "dial.medium").foregroundStyle(.secondary)
            Spacer()
            Menu {
                Section { ForEach(StreamPreset.builtIn) { p in Button(p.name) { applyPreset(p) } } }
                if !customPresets.isEmpty {
                    Section("Custom") {
                        ForEach(customPresets) { p in Button(p.name) { applyPreset(p) } }
                    }
                }
                Divider()
                Button("Add custom preset…") { showSavePreset = true }
                if !customPresets.isEmpty {
                    Menu("Delete custom preset") {
                        ForEach(customPresets) { p in
                            Button(p.name, role: .destructive) { deletePreset(p) }
                        }
                    }
                }
            } label: {
                Text(currentPresetName)
            }
            .frame(maxWidth: 160)
        }
    }

    private var customPresets: [StreamPreset] {
        (try? JSONDecoder().decode([StreamPreset].self, from: Data(customPresetsRaw.utf8))) ?? []
    }

    /// Name of the preset matching the current factor/quality/QoS, or "Custom".
    private var currentPresetName: String {
        let all = StreamPreset.builtIn + customPresets
        let match = all.first {
            Int($0.priorityFactor) == Int(priorityFactor)
                && Int($0.quality) == Int(quality)
                && Int($0.qos) == Int(qos)
        }
        return match?.name ?? "Custom"
    }

    private func applyPreset(_ p: StreamPreset) {
        priorityTop = p.priorityTop
        priorityFactor = p.priorityFactor
        quality = p.quality
        qos = p.qos
    }

    private func savePreset(named rawName: String) {
        let name = rawName.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        let preset = StreamPreset(name: name, priorityTop: priorityTop,
                                  priorityFactor: priorityFactor, quality: quality, qos: qos)
        var list = customPresets.filter { $0.name != name }   // replace same-named
        list.append(preset)
        persistCustomPresets(list)
    }

    private func deletePreset(_ p: StreamPreset) {
        persistCustomPresets(customPresets.filter { $0.id != p.id })
    }

    private func persistCustomPresets(_ list: [StreamPreset]) {
        if let data = try? JSONEncoder().encode(list) {
            customPresetsRaw = String(decoding: data, as: UTF8.self)
        }
    }

    private var appIcon: some View {
        Group {
            if let url = Bundle.main.url(forResource: "AppIcon", withExtension: "icns"),
               let img = NSImage(contentsOf: url) {
                Image(nsImage: img).resizable()
            } else {
                ZStack {
                    RoundedRectangle(cornerRadius: 14).fill(LinearGradient.brand)
                    Image(systemName: "play.tv.fill")
                        .font(.system(size: 26, weight: .semibold))
                        .foregroundStyle(.white)
                }
            }
        }
    }

    // MARK: - Cards

    private var remoteplayCard: some View {
        card("Remoteplay", systemImage: "antenna.radiowaves.left.and.right") {
            Picker("", selection: protoBinding) {
                ForEach(StreamProtocol.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            ipSection
            Divider().opacity(0.4)
            presetRow

            if proto == .ntr {
                field("Priority screen", icon: "rectangle.on.rectangle") {
                    Picker("", selection: $priorityTop) {
                        Text("Top").tag(true)
                        Text("Bottom").tag(false)
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .frame(width: 180)
                }
                slider("Priority factor", icon: "slider.horizontal.3", value: $priorityFactor, range: 0...10)
                slider("Image quality", icon: "photo", value: $quality, range: 10...100)
                slider("QoS", icon: "gauge.with.dots.needle.67percent", value: $qos, range: 2...100)
            } else {
                slider("Image quality", icon: "photo", value: $quality, range: 1...100)
                slider("CPU limit", icon: "cpu", value: $cpuLimit, range: 0...255)
                Label("HzMod streams the top screen only. Beta — please report issues.",
                      systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var protoBinding: Binding<StreamProtocol> {
        Binding(get: { proto }, set: { protoRaw = $0.rawValue })
    }

    private var displayCard: some View {
        card("Display", systemImage: "macwindow") {
            field("Listen port", icon: "point.3.connected.trianglepath.dotted") {
                TextField("8001", text: $listenPort)
                    .textFieldStyle(.plain)
                    .multilineTextAlignment(.center)
                    .frame(width: 80)
                    .padding(.vertical, 6)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
            }
            Divider().opacity(0.4)
            menu("Screen layout", icon: "rectangle.split.2x2", selection: layoutBinding) {
                ForEach(StreamLayout.allCases) { Label($0.rawValue, systemImage: $0.symbol).tag($0) }
            }
            menu("Interpolation", icon: "wand.and.rays", selection: interpBinding) {
                ForEach(Interpolation.allCases) { Text($0.rawValue).tag($0) }
            }
            menu("Rotation", icon: "rotate.right", selection: rotationBinding) {
                // Labels are relative to the correct orientation: "0°" is the upright image
                // (internally a 270° rotation of the raw 3DS framebuffer).
                Text("0°").tag(CGFloat(270))
                Text("90°").tag(CGFloat(0))
                Text("180°").tag(CGFloat(90))
                Text("270°").tag(CGFloat(180))
            }
            scaleRow("Top scale", icon: "rectangle.tophalf.inset.filled", value: $topScale)
            scaleRow("Bottom scale", icon: "rectangle.bottomhalf.inset.filled", value: $bottomScale)
            field("Max FPS", icon: "gauge.with.needle") {
                HStack(spacing: 6) {
                    TextField("0", value: $maxFPS, format: .number)
                        .textFieldStyle(.plain)
                        .multilineTextAlignment(.center)
                        .frame(width: 56)
                        .padding(.vertical, 6)
                        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
                    Text("0 = ∞").font(.caption).foregroundStyle(.tertiary)
                }
            }
            HStack {
                Label("Ambient glow", systemImage: "sparkles").foregroundStyle(.secondary)
                Spacer()
                Toggle("", isOn: $ambilight).labelsHidden().toggleStyle(.switch)
            }
            Divider().opacity(0.4)
            screenshotFolderRow
            Spacer(minLength: 0)
        }
    }

    private var screenshotFolderRow: some View {
        HStack {
            Label("Screenshots", systemImage: "folder").foregroundStyle(.secondary)
            Spacer()
            Text(screenshotFolderName)
                .font(.callout)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: 110, alignment: .trailing)
            Button("Choose…", action: chooseScreenshotFolder)
                .controlSize(.small)
        }
    }

    private var screenshotFolderName: String {
        screenshotFolder.isEmpty ? "Pictures/3DSnickerStream"
                                 : URL(fileURLWithPath: screenshotFolder).lastPathComponent
    }

    private func chooseScreenshotFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        panel.message = "Choose a folder to save screenshots"
        if !screenshotFolder.isEmpty {
            panel.directoryURL = URL(fileURLWithPath: screenshotFolder, isDirectory: true)
        }
        if panel.runModal() == .OK, let url = panel.url {
            screenshotFolder = url.path
        }
    }

    // MARK: - Footer

    private var footer: some View {
        HStack(spacing: 12) {
            Label(model.status, systemImage: "circle.fill")
                .font(.callout)
                .labelStyle(StatusLabelStyle(dotColor: statusDotColor,
                                             textColor: model.phase == .failed ? .red : .secondary))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 12)
            if model.phase == .connecting {
                ProgressView().controlSize(.small)
                Button("Cancel", role: .cancel, action: model.disconnect)
                    .controlSize(.large)
            } else {
                Button(action: connect) {
                    Label("Connect", systemImage: "play.fill")
                        .font(.headline)
                        .padding(.horizontal, 22)
                        .padding(.vertical, 11)
                        .background(LinearGradient.brand, in: Capsule())
                        .foregroundStyle(.white)
                        .shadow(color: Color(red: 0.6, green: 0.2, blue: 0.6).opacity(0.4), radius: 10, y: 4)
                }
                .buttonStyle(.plain)
                .keyboardShortcut(.defaultAction)
            }
        }
    }

    private var statusDotColor: Color {
        switch model.phase {
        case .failed: return .red
        case .connecting: return .orange
        default: return .gray
        }
    }

    private var aboutPopover: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text("3DSnickerStream for Apple Silicon").font(.headline)
                Spacer()
                Text("v\(AppInfo.version)").font(.caption).foregroundStyle(.secondary)
            }
            Text("A native macOS reimplementation of the NTR remoteplay client.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            VStack(alignment: .leading, spacing: 2) {
                Text("Original by [RattletraPM](https://github.com/RattletraPM)")
                Text("Revision by [samaBR](https://github.com/samaBR85)")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .tint(Color(red: 0.66, green: 0.25, blue: 0.7))
            Divider()
            Toggle("Check for updates on startup", isOn: $checkUpdates)
                .toggleStyle(.switch)
                .font(.callout)
            Button("Check now") {
                Task { update = await UpdateChecker.newerRelease(); showAbout = false }
            }
            .controlSize(.small)
            Text("Built in Swift + SwiftUI, with the help of Claude.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(18)
        .frame(width: 300)
    }

    // MARK: - IP address + saved list

    private var ipSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Label("3DS IP address", systemImage: "network")
                    .foregroundStyle(.secondary)
                Spacer()
                OctetIPField(ip: $ip)
                Button {
                    if isCurrentSaved { removeIP(currentIP) } else { saveCurrentIP() }
                } label: {
                    Image(systemName: isCurrentSaved ? "bookmark.fill" : "bookmark")
                        .foregroundStyle(isCurrentSaved ? AnyShapeStyle(LinearGradient.brand) : AnyShapeStyle(.secondary))
                }
                .buttonStyle(.plain)
                .disabled(!isValidIP(currentIP))
                .help(isCurrentSaved ? "Remove from saved" : "Save this IP")
            }
            findOnNetworkBox
            if !savedIPs.isEmpty {
                FlowLayout(spacing: 8) {
                    ForEach(savedIPs, id: \.self) { ipChip($0) }
                }
            }
        }
    }

    /// Grouped "Find on network" box: scan trigger + status, then the three network toggles.
    private var findOnNetworkBox: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Button { startScan() } label: {
                    if scanning {
                        ProgressView().controlSize(.small).frame(width: 16)
                    } else {
                        Image(systemName: "dot.radiowaves.left.and.right")
                            .foregroundStyle(radarIsConnected ? AnyShapeStyle(Color.green) : AnyShapeStyle(LinearGradient.brand))
                    }
                }
                .buttonStyle(.plain)
                .disabled(scanning)
                .help("Scan the network for a 3DS")
                Text(scanStatusText)
                    .font(.callout).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
                Spacer(minLength: 0)
            }
            toggleRow("Scan on startup", "power", $scanOnStartup)
            toggleRow("Auto-connect (found device)", "bolt.horizontal", $autoConnect)
            toggleRow("Try reconnect", "arrow.clockwise", $tryReconnect)
        }
        .padding(12)
        .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.white.opacity(0.06)))
    }

    /// Green radar: the entered IP was found on the network by the last scan.
    private var radarIsConnected: Bool {
        discovered.contains(currentIP)
    }

    private var scanStatusText: String {
        if scanning { return "Scanning…" }
        if !hasScanned { return "Find on network" }
        guard let first = discovered.first else { return "No device found" }
        return discovered.count > 1 ? "Found \(first) (+\(discovered.count - 1))" : "Found \(first)"
    }


    // MARK: - Auto-discovery

    private func startScan() {
        guard !scanning else { return }
        scanning = true
        discovered = []
        Task {
            let results = await NetworkScanner.scan()
            await MainActor.run {
                discovered = results
                scanning = false
                hasScanned = true
                if let first = results.first {
                    ip = first                                   // fill the IP field on a hit
                    if autoConnect, model.phase == .idle { connect() }
                }
            }
        }
    }

    /// Runs the launch-time scan once (guarded so it doesn't loop on every menu return).
    private func startupScanIfNeeded() {
        guard scanOnStartup, !model.didStartupScan else { return }
        model.didStartupScan = true
        startScan()
    }

    private func ipChip(_ entry: String) -> some View {
        let selected = entry == currentIP
        return HStack(spacing: 7) {
            Button { ip = entry } label: {
                Text(entry)
                    .font(.callout)
                    .monospacedDigit()
                    .foregroundStyle(selected ? Color.white : .primary)
            }
            .buttonStyle(.plain)
            Button { removeIP(entry) } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(selected ? Color.white.opacity(0.8) : .secondary)
            }
            .buttonStyle(.plain)
        }
        .padding(.leading, 12)
        .padding(.trailing, 9)
        .padding(.vertical, 6)
        .background {
            if selected {
                Capsule().fill(LinearGradient.brand)
            } else {
                Capsule().fill(.quaternary.opacity(0.5))
            }
        }
    }

    private var currentIP: String { ip.trimmingCharacters(in: .whitespaces) }
    private var isCurrentSaved: Bool { savedIPs.contains(currentIP) }
    private var savedIPs: [String] {
        savedIPsRaw.split(separator: "\n").map(String.init)
    }

    private func saveCurrentIP() {
        guard isValidIP(currentIP) else { return }
        var list = savedIPs.filter { $0 != currentIP }
        list.insert(currentIP, at: 0)
        savedIPsRaw = Array(list.prefix(8)).joined(separator: "\n")
    }

    private func removeIP(_ value: String) {
        savedIPsRaw = savedIPs.filter { $0 != value }.joined(separator: "\n")
    }

    private func isValidIP(_ s: String) -> Bool {
        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        return parts.allSatisfy { Int($0).map { $0 >= 0 && $0 <= 255 } ?? false }
    }

    // MARK: - Reusable building blocks

    private func card<Content: View>(_ title: String, systemImage: String,
                                     @ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Label(title, systemImage: systemImage)
                .font(.system(.headline, design: .rounded))
                .foregroundStyle(.primary)
            content()
        }
        .padding(20)
        .frame(maxWidth: .infinity, minHeight: 300, alignment: .topLeading)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 18))
        .overlay(RoundedRectangle(cornerRadius: 18).strokeBorder(.white.opacity(0.08)))
    }

    private func field<Content: View>(_ title: String, icon: String,
                                      @ViewBuilder _ content: () -> Content) -> some View {
        HStack {
            Label(title, systemImage: icon)
                .foregroundStyle(.secondary)
                .labelStyle(.titleAndIcon)
            Spacer()
            content()
        }
    }

    private func slider(_ title: String, icon: String, value: Binding<Double>,
                        range: ClosedRange<Double>) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Label(title, systemImage: icon).foregroundStyle(.secondary)
                Spacer()
                Text("\(Int(value.wrappedValue))")
                    .monospacedDigit()
                    .font(.callout.weight(.medium))
            }
            Slider(value: value, in: range, step: 1)
                .tint(Color(red: 0.66, green: 0.25, blue: 0.7))
        }
    }

    private func menu<S: Hashable, Content: View>(_ title: String, icon: String,
                                                  selection: Binding<S>,
                                                  @ViewBuilder _ content: () -> Content) -> some View {
        HStack {
            Label(title, systemImage: icon).foregroundStyle(.secondary)
            Spacer()
            Picker("", selection: selection) { content() }
                .labelsHidden()
                .frame(maxWidth: 150)
        }
    }

    private func scaleRow(_ title: String, icon: String, value: Binding<Double>) -> some View {
        HStack {
            Label(title, systemImage: icon).foregroundStyle(.secondary)
            Spacer()
            Slider(value: value, in: 0.5...2, step: 0.1)
                .tint(Color(red: 0.66, green: 0.25, blue: 0.7))
                .frame(width: 130)
            Text(String(format: "%.1f×", value.wrappedValue))
                .monospacedDigit()
                .font(.callout)
                .frame(width: 40, alignment: .trailing)
        }
    }

    // MARK: - Bindings bridging @AppStorage strings to typed pickers

    private var layoutBinding: Binding<StreamLayout> {
        Binding(get: { StreamLayout(rawValue: layoutRaw) ?? .stacked },
                set: { layoutRaw = $0.rawValue })
    }
    private var interpBinding: Binding<Interpolation> {
        Binding(get: { Interpolation(rawValue: interpRaw) ?? .linear },
                set: { interpRaw = $0.rawValue })
    }
    private var rotationBinding: Binding<CGFloat> {
        Binding(get: { CGFloat(rotation) }, set: { rotation = Double($0) })
    }

    // MARK: - Connect

    private func connect() {
        saveCurrentIP()   // remember the IP we're connecting to
        model.layout = layoutBinding.wrappedValue
        model.interpolation = interpBinding.wrappedValue
        model.rotationDegrees = CGFloat(rotation)
        model.maxFPS = max(0, maxFPS)
        model.topScale = CGFloat(topScale)
        model.bottomScale = CGFloat(bottomScale)

        var config = StreamConfig(ip: ip.trimmingCharacters(in: .whitespaces))
        config.proto = proto
        config.listenPort = UInt16(listenPort) ?? 8001
        config.priorityScreen = priorityTop ? .top : .bottom
        config.priorityFactor = UInt8(priorityFactor)
        config.quality = UInt8(quality)
        config.qos = UInt8(qos)
        config.cpuLimit = UInt8(cpuLimit)
        model.connect(config: config, reconnect: tryReconnect)
    }

    private func toggleRow(_ title: String, _ icon: String, _ isOn: Binding<Bool>) -> some View {
        HStack {
            Label(title, systemImage: icon).foregroundStyle(.secondary)
            Spacer()
            Toggle("", isOn: isOn).labelsHidden().toggleStyle(.switch)
        }
    }
}

/// Renders the status line with a small colored dot reflecting the connection phase.
private struct StatusLabelStyle: LabelStyle {
    var dotColor: Color = .gray
    var textColor: Color = .secondary
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 6) {
            configuration.icon.font(.system(size: 7)).foregroundStyle(dotColor)
            configuration.title.foregroundStyle(textColor)
        }
    }
}

/// Segmented IPv4 entry (`0 . 0 . 0 . 0`) with auto-advance — the signature element of the original.
struct OctetIPField: View {
    @Binding var ip: String
    @FocusState private var focused: Int?
    @State private var octets: [String] = ["", "", "", ""]

    var body: some View {
        HStack(spacing: 6) {
            ForEach(0..<4, id: \.self) { i in
                TextField("0", text: binding(for: i))
                    .textFieldStyle(.plain)
                    .multilineTextAlignment(.center)
                    .monospacedDigit()
                    .frame(width: 44)
                    .padding(.vertical, 6)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
                    .focused($focused, equals: i)
                if i < 3 {
                    Text(".").font(.headline).foregroundStyle(.secondary)
                }
            }
        }
        .onAppear(perform: split)
        .onChange(of: ip) { newValue in
            // Re-sync the boxes when the IP is set externally (e.g. a saved-IP chip).
            if octets.joined(separator: ".") != newValue { split() }
        }
    }

    private func binding(for i: Int) -> Binding<String> {
        Binding(
            get: { octets[i] },
            set: { newValue in
                var digits = String(newValue.filter(\.isNumber).prefix(3))
                if let n = Int(digits), n > 255 { digits = "255" }
                octets[i] = digits
                ip = octets.joined(separator: ".")
                if digits.count == 3 && i < 3 { focused = i + 1 }
            }
        )
    }

    private func split() {
        let parts = ip.split(separator: ".", omittingEmptySubsequences: false).map(String.init)
        for i in 0..<4 { octets[i] = i < parts.count ? parts[i] : "" }
    }
}
