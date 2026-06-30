import Foundation
import Network

/// Which 3DS screen a decoded frame belongs to.
enum Screen: Int {
    case bottom = 0
    case top = 1
}

/// Configuration for an NTR remoteplay session, mirroring Snickerstream's connect dialog.
struct NTRConfig {
    var ip: String
    var listenPort: UInt16 = 8001
    /// 1 = give the Top screen priority, 0 = Bottom screen priority.
    var priorityScreen: Screen = .top
    /// 0–10. How much more of the priority screen is sent vs. the other one.
    var priorityFactor: UInt8 = 5
    /// 10–100 JPEG quality.
    var quality: UInt8 = 70
    /// Quality-of-Service value (sent doubled, like the original).
    var qos: UInt8 = 20
}

/// Reassembles NTR's UDP JPEG stream and drives a TCP remoteplay handshake.
///
/// Protocol (reverse-engineered from RattletraPM/Snickerstream `include/ntr.au3`):
///
/// **TCP init** — connect to `<3DS>:8000`, send an 84-byte NTR debugger command
/// (magic `0x12345678`, seq `3000`, cmd `901 / 0x385`) carrying the priority/quality/QoS
/// args, disconnect, wait ~3s, then connect/disconnect once more to kick streaming.
///
/// **UDP frames** — the 3DS pushes datagrams to `<PC>:<listenPort>` (default 8001).
/// Each datagram is a 4-byte header + a slice of a JPEG:
///   - byte 0: frame id (increments per frame)
///   - byte 1: high nibble = 1 on the last packet of a frame; low nibble = screen (1=top, 0=bottom)
///   - byte 2: image format (usually 2)
///   - byte 3: packet number within the frame (starts at 0)
/// Concatenate payloads in packet-number order until the last-packet flag; the result is a JPEG.
final class NTRClient {
    private var config: NTRConfig
    private var listener: NWConnectionGroup?
    private var udp: NWConnection?
    private let queue = DispatchQueue(label: "snickerstream.ntr.udp")

    /// Per-screen reassembly state.
    private struct FrameBuffer {
        var frameID: UInt8 = 0
        var nextPacket: UInt8 = 0
        var data = Data()
        var active = false
    }
    private var buffers: [Int: FrameBuffer] = [
        Screen.top.rawValue: FrameBuffer(),
        Screen.bottom.rawValue: FrameBuffer()
    ]

    /// Called on the UDP queue with a complete JPEG for a screen.
    var onFrame: ((Screen, Data) -> Void)?
    /// Status / log line for the UI.
    var onStatus: ((String) -> Void)?

    init(config: NTRConfig) {
        self.config = config
    }

    // MARK: - Start / stop

    func start() {
        startListening()
        sendRemoteplayInit()
    }

    /// Re-sends the remoteplay init handshake (UDP listener stays bound). Used for retries.
    func resendInit() {
        sendRemoteplayInit()
    }

    /// Applies a new config (e.g. changed quality/priority) and re-sends the init.
    func reinit(config: NTRConfig) {
        self.config = config
        sendRemoteplayInit()
    }

    func stop() {
        udp?.cancel()
        udp = nil
        listener?.cancel()
        listener = nil
    }

    // MARK: - UDP receive

    private func startListening() {
        // Bind a UDP listener on the local port; the 3DS sends datagrams to it.
        let params = NWParameters.udp
        params.allowLocalEndpointReuse = true
        if let inet = params.defaultProtocolStack.internetProtocol as? NWProtocolIP.Options {
            inet.version = .v4
        }
        let localPort = NWEndpoint.Port(rawValue: config.listenPort)!
        let listener: NWListener
        do {
            listener = try NWListener(using: params, on: localPort)
        } catch {
            onStatus?("Failed to bind UDP \(config.listenPort): \(error.localizedDescription)")
            return
        }
        listener.newConnectionHandler = { [weak self] conn in
            guard let self = self else { return }
            conn.start(queue: self.queue)
            self.receive(on: conn)
        }
        listener.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                self?.onStatus?("Listening on UDP \(self?.config.listenPort ?? 0)")
            case .failed(let err):
                self?.onStatus?("UDP listener failed: \(err.localizedDescription)")
            default:
                break
            }
        }
        listener.start(queue: queue)
        self.nwListener = listener
    }

    private var nwListener: NWListener?

    private func receive(on conn: NWConnection) {
        conn.receiveMessage { [weak self] data, _, _, error in
            guard let self = self else { return }
            if let data = data, !data.isEmpty {
                self.handlePacket(data)
            }
            if error == nil {
                self.receive(on: conn)
            }
        }
    }

    // MARK: - Frame reassembly

    private func handlePacket(_ packet: Data) {
        guard packet.count > 4 else { return }
        let bytes = [UInt8](packet)
        let frameID = bytes[0]
        let flags = bytes[1]
        let isLast = (flags >> 4) & 0x0F == 1
        let screenRaw = Int(flags & 0x0F)
        let packetNo = bytes[3]
        guard let screen = Screen(rawValue: screenRaw) else { return }

        let payload = packet.subdata(in: 4..<packet.count)
        var buf = buffers[screenRaw] ?? FrameBuffer()

        if packetNo == 0 {
            // Start of a new frame for this screen.
            buf = FrameBuffer()
            buf.frameID = frameID
            buf.active = true
            buf.nextPacket = 0
        }

        // Drop the frame if packets arrived out of order or from a different frame.
        guard buf.active, buf.frameID == frameID, buf.nextPacket == packetNo else {
            buf.active = false
            buffers[screenRaw] = buf
            return
        }

        buf.data.append(payload)
        buf.nextPacket = packetNo &+ 1

        if isLast {
            let jpeg = buf.data
            buf = FrameBuffer()
            // Validate the JPEG end-of-image marker (FF D9) before handing it off.
            if jpeg.count > 4, jpeg[jpeg.count - 2] == 0xFF, jpeg[jpeg.count - 1] == 0xD9 {
                onFrame?(screen, jpeg)
            }
        }
        buffers[screenRaw] = buf
    }

    // MARK: - TCP remoteplay init

    private func sendRemoteplayInit() {
        let packet = Self.buildInitPacket(config: config)
        // First handshake.
        sendOnce(packet: packet) { [weak self] in
            self?.onStatus?("Sent remoteplay init, waiting 3s…")
            self?.queue.asyncAfter(deadline: .now() + 3) {
                // Second connect/disconnect kicks NTR into streaming.
                self?.sendOnce(packet: nil) {
                    self?.onStatus?("Remoteplay init complete — awaiting frames")
                }
            }
        }
    }

    private func sendOnce(packet: Data?, completion: @escaping () -> Void) {
        guard let port = NWEndpoint.Port(rawValue: 8000) else { completion(); return }
        let host = NWEndpoint.Host(config.ip)
        let conn = NWConnection(host: host, port: port, using: .tcp)
        conn.stateUpdateHandler = { state in
            switch state {
            case .ready:
                if let packet = packet {
                    conn.send(content: packet, completion: .contentProcessed { _ in
                        // Give NTR a moment to read, then close.
                        DispatchQueue.global().asyncAfter(deadline: .now() + 0.3) {
                            conn.cancel()
                        }
                    })
                } else {
                    conn.cancel()
                }
            case .failed, .cancelled:
                completion()
            default:
                break
            }
        }
        conn.start(queue: queue)
        // Safety: ensure completion fires even if the connection lingers.
        queue.asyncAfter(deadline: .now() + 2) {
            conn.cancel()
        }
    }

    /// Test hook: feed a synthetic UDP datagram through the reassembly path.
    func ingestForTest(_ data: Data) {
        handlePacket(data)
    }

    /// Builds the 84-byte NTR debugger command that starts remoteplay.
    static func buildInitPacket(config: NTRConfig) -> Data {
        var p = Data(count: 84)
        // Magic 0x12345678 (little-endian on the wire).
        p[0] = 0x78; p[1] = 0x56; p[2] = 0x34; p[3] = 0x12
        // seq = 3000 (0x0BB8)
        p[4] = 0xB8; p[5] = 0x0B; p[6] = 0x00; p[7] = 0x00
        // type = 0
        p[8] = 0x00; p[9] = 0x00; p[10] = 0x00; p[11] = 0x00
        // cmd = 901 (0x0385) — remoteplay
        p[12] = 0x85; p[13] = 0x03; p[14] = 0x00; p[15] = 0x00
        // arg0: priority factor (0x10), priority screen (0x11)
        p[0x10] = config.priorityFactor
        p[0x11] = UInt8(config.priorityScreen.rawValue)
        // arg1: JPEG quality (0x14)
        p[0x14] = config.quality
        // arg2: QoS, doubled (0x1A)
        p[0x1A] = config.qos &* 2
        // Remaining bytes (incl. data length at 0x50) stay zero.
        return p
    }
}
