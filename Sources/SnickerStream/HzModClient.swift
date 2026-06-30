import Foundation
import Network

/// HzMod streaming client (TCP). **Beta — not yet verified against real hardware.**
///
/// Protocol (from RattletraPM/Snickerstream `include/HzMod.au3`):
/// - Connect TCP to `<3DS>:6464`.
/// - Send, in order: a CPU-limit packet, a quality packet, then the stream-start packet.
/// - The 3DS then streams JPEG frames back over the same TCP connection (top screen only;
///   HzMod doesn't stream the bottom screen).
///
/// Frame framing in the original is a 13-byte-ish header (byte 0 = type `0x04`=JPEG, a
/// little-endian size, then reserved bytes) followed by the JPEG. Because those header
/// offsets are ambiguous in the source and we can't test against hardware, this client is
/// resilient: it scans the TCP byte stream for complete JPEGs (`FF D8` … `FF D9`) and emits
/// each one, ignoring the surrounding header bytes entirely.
final class HzModClient: StreamClient {
    private let config: StreamConfig
    private let queue = DispatchQueue(label: "snickerstream.hzmod.tcp")
    private var conn: NWConnection?
    private var buffer = Data()
    private var stopped = false

    private let soi = Data([0xFF, 0xD8])
    private let eoi = Data([0xFF, 0xD9])
    private let maxFrameBytes = 4 * 1024 * 1024

    var onFrame: ((Screen, Data) -> Void)?
    var onStatus: ((String) -> Void)?

    // Control packets (see HzMod.au3).
    private static let header: [UInt8] = [0x7E, 0x05, 0x00, 0x00]
    private func cpuLimitPacket(_ v: UInt8) -> Data { Data(Self.header + [0xFF, 0x00, 0x00, 0x00, v]) }
    private func qualityPacket(_ v: UInt8) -> Data { Data(Self.header + [0x03, 0x00, 0x00, 0x00, v]) }
    private static let startPacket = Data([0x7E, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01])

    init(config: StreamConfig) {
        self.config = config
    }

    func start() {
        stopped = false
        connect()
    }

    func stop() {
        queue.async { [self] in
            stopped = true
            onFrame = nil
            onStatus = nil
            conn?.cancel()
            conn = nil
            buffer.removeAll(keepingCapacity: false)
        }
    }

    func retry() {
        // HzMod has a single TCP connection; reconnect from scratch.
        queue.async { [self] in
            conn?.cancel()
            conn = nil
            buffer.removeAll(keepingCapacity: true)
            connect()
        }
    }

    func setQuality(_ quality: UInt8) {
        let q = min(100, max(1, quality))
        queue.async { [self] in
            conn?.send(content: qualityPacket(q), completion: .idempotent)
        }
    }

    func swapPriority() {
        // HzMod streams the top screen only — nothing to swap.
        onStatus?("HzMod streams the top screen only")
    }

    // MARK: - Connection

    private func connect() {
        guard let port = NWEndpoint.Port(rawValue: 6464) else { return }
        let connection = NWConnection(host: NWEndpoint.Host(config.ip), port: port, using: .tcp)
        self.conn = connection
        connection.stateUpdateHandler = { [weak self] state in
            guard let self = self else { return }
            switch state {
            case .ready:
                self.onStatus?("HzMod connected — starting stream")
                self.sendHandshake(on: connection)
                self.receive(on: connection)
            case .failed(let err):
                self.onStatus?("HzMod connection failed: \(err.localizedDescription)")
            case .cancelled:
                break
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    private func sendHandshake(on connection: NWConnection) {
        connection.send(content: cpuLimitPacket(config.cpuLimit), completion: .idempotent)
        connection.send(content: qualityPacket(min(100, max(1, config.quality))), completion: .idempotent)
        connection.send(content: Self.startPacket, completion: .idempotent)
    }

    private func receive(on connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self = self, !self.stopped else { return }
            if let data = data, !data.isEmpty {
                self.buffer.append(data)
                self.extractFrames()
            }
            if error == nil && !isComplete {
                self.receive(on: connection)
            } else if isComplete {
                self.onStatus?("HzMod stream ended")
            }
        }
    }

    // MARK: - JPEG framing

    private func extractFrames() {
        while true {
            guard let soiRange = buffer.range(of: soi) else {
                // No frame start yet; keep at most a trailing byte (a split 0xFF).
                if buffer.count > 1 { buffer.removeSubrange(buffer.startIndex..<(buffer.endIndex - 1)) }
                return
            }
            // Discard any junk/header bytes before the JPEG start.
            if soiRange.lowerBound > buffer.startIndex {
                buffer.removeSubrange(buffer.startIndex..<soiRange.lowerBound)
            }
            // Look for the end-of-image after the start marker.
            let searchStart = buffer.index(buffer.startIndex, offsetBy: 2)
            guard let eoiRange = buffer.range(of: eoi, in: searchStart..<buffer.endIndex) else {
                // Frame still incomplete; guard against runaway growth on desync.
                if buffer.count > maxFrameBytes { buffer.removeAll(keepingCapacity: true) }
                return
            }
            let jpeg = buffer.subdata(in: buffer.startIndex..<eoiRange.upperBound)
            buffer.removeSubrange(buffer.startIndex..<eoiRange.upperBound)
            onFrame?(.top, jpeg)
        }
    }
}
