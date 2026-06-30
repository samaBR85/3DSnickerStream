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

    @State private var showAbout = false

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
            Button { showAbout.toggle() } label: {
                Image(systemName: "info.circle")
                    .font(.title2)
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("About SnickerStream")
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
            field("3DS IP address", icon: "network") {
                OctetIPField(ip: $ip)
            }
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
            Spacer(minLength: 0)
        }
    }

    // MARK: - Footer

    private var footer: some View {
        HStack {
            Label(model.status, systemImage: "circle.fill")
                .font(.callout)
                .foregroundStyle(.secondary)
                .labelStyle(StatusLabelStyle())
            Spacer()
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

/// Tints the status dot green while streaming, gray otherwise.
private struct StatusLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 6) {
            configuration.icon.font(.system(size: 7)).foregroundStyle(.tertiary)
            configuration.title
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
