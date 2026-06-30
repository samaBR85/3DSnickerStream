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

    @State private var showAbout = false
    @State private var showShortcuts = false

    var body: some View {
        ZStack {
            LinearGradient(colors: [Color(nsColor: .windowBackgroundColor),
                                    Color(nsColor: .underPageBackgroundColor)],
                           startPoint: .top, endPoint: .bottom)
                .ignoresSafeArea()

            VStack(alignment: .leading, spacing: 18) {
                header

                HStack(alignment: .top, spacing: 16) {
                    remoteplayCard
                    displayCard
                }

                footer
            }
            .padding(24)
        }
        .frame(minWidth: 720, minHeight: 560)
        .popover(isPresented: $showAbout, arrowEdge: .bottom) { aboutPopover }
        .sheet(isPresented: $showShortcuts) { ShortcutsView() }
    }

    // MARK: - Header

    private var header: some View {
        HStack(spacing: 16) {
            appIcon.frame(width: 60, height: 60)
            VStack(alignment: .leading, spacing: 2) {
                Text("SnickerStream")
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
                .help("About SnickerStream")
            }
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
            ipSection
            Divider().opacity(0.4)
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
        }
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
                Text("0°").tag(CGFloat(0))
                Text("90°").tag(CGFloat(90))
                Text("180°").tag(CGFloat(180))
                Text("270°").tag(CGFloat(270))
            }
            HStack {
                Label("Ambient glow", systemImage: "sparkles").foregroundStyle(.secondary)
                Spacer()
                Toggle("", isOn: $ambilight).labelsHidden().toggleStyle(.switch)
            }
            Spacer(minLength: 0)
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
            Text("SnickerStream for Apple Silicon")
                .font(.headline)
            Text("A native macOS reimplementation of the NTR remoteplay client by RattletraPM.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Divider()
            Text("Receives the 3DS JPEG stream over UDP and drives the NTR init over TCP. Built in Swift + SwiftUI.")
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
            if !savedIPs.isEmpty {
                FlowLayout(spacing: 8) {
                    ForEach(savedIPs, id: \.self) { ipChip($0) }
                }
            }
        }
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

        var config = NTRConfig(ip: ip.trimmingCharacters(in: .whitespaces))
        config.listenPort = UInt16(listenPort) ?? 8001
        config.priorityScreen = priorityTop ? .top : .bottom
        config.priorityFactor = UInt8(priorityFactor)
        config.quality = UInt8(quality)
        config.qos = UInt8(qos)
        model.connect(config: config)
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
