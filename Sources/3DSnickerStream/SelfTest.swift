import Foundation
import ImageIO
import CoreGraphics
import UniformTypeIdentifiers

/// Headless validation of the NTR protocol logic — runs with `--selftest`.
/// Exercises packet construction and the UDP→JPEG reassembly path without a real 3DS.
enum SelfTest {
    static func run() -> Int32 {
        var failures = 0
        func check(_ name: String, _ cond: Bool) {
            print(cond ? "  ✓ \(name)" : "  ✗ \(name)")
            if !cond { failures += 1 }
        }

        print("3DSnickerStream self-test")

        // 1) Init packet structure.
        var cfg = StreamConfig(ip: "10.0.0.5")
        cfg.priorityScreen = .top
        cfg.priorityFactor = 5
        cfg.quality = 70
        cfg.qos = 20
        let p = NTRClient.buildInitPacket(config: cfg)
        check("init packet is 84 bytes", p.count == 84)
        check("magic 0x12345678", p[0] == 0x78 && p[1] == 0x56 && p[2] == 0x34 && p[3] == 0x12)
        check("seq 3000", p[4] == 0xB8 && p[5] == 0x0B)
        check("cmd 901", p[12] == 0x85 && p[13] == 0x03)
        check("priority factor @0x10", p[0x10] == 5)
        check("priority screen @0x11 (top=1)", p[0x11] == 1)
        check("quality @0x14", p[0x14] == 70)
        check("qos doubled @0x1A", p[0x1A] == 40)

        // 2) Build a real JPEG, split it across UDP packets, and verify reassembly.
        guard let jpeg = makeTestJPEG() else {
            print("  ✗ could not synthesize test JPEG")
            return 1
        }
        let client = NTRClient(config: cfg)
        var received: [(Screen, Data)] = []
        client.onFrame = { screen, data in received.append((screen, data)) }

        for packet in fragment(jpeg: jpeg, screen: .top, frameID: 7, chunk: 1000) {
            client.ingestForTest(packet)
        }
        check("one top frame reassembled", received.count == 1)
        if let first = received.first {
            check("frame is top screen", first.0 == .top)
            check("reassembled bytes match original", first.1 == jpeg)
            check("reassembled JPEG decodes", StreamViewModel.decodeJPEG(first.1) != nil)
        }

        // 3) A dropped middle packet must abort the frame (no bad frame emitted).
        received.removeAll()
        var frags = fragment(jpeg: jpeg, screen: .bottom, frameID: 9, chunk: 1000)
        if frags.count >= 3 { frags.remove(at: 1) }  // simulate packet loss
        for packet in frags { client.ingestForTest(packet) }
        check("corrupted frame dropped", received.isEmpty)

        print(failures == 0 ? "ALL PASSED" : "\(failures) FAILED")
        return failures == 0 ? 0 : 1
    }

    /// Splits a JPEG into NTR-style UDP datagrams (4-byte header + chunk).
    private static func fragment(jpeg: Data, screen: Screen, frameID: UInt8, chunk: Int) -> [Data] {
        var packets: [Data] = []
        var offset = 0
        var packetNo: UInt8 = 0
        while offset < jpeg.count {
            let end = min(offset + chunk, jpeg.count)
            let isLast = end >= jpeg.count
            var header = Data(count: 4)
            header[0] = frameID
            header[1] = (isLast ? 0x10 : 0x00) | UInt8(screen.rawValue)
            header[2] = 2
            header[3] = packetNo
            var pkt = header
            pkt.append(jpeg.subdata(in: offset..<end))
            packets.append(pkt)
            offset = end
            packetNo &+= 1
        }
        return packets
    }

    /// Encodes a small gradient image to JPEG so the reassembly test uses real data.
    private static func makeTestJPEG() -> Data? {
        let w = 400, h = 240
        let cs = CGColorSpaceCreateDeviceRGB()
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8,
                                  bytesPerRow: 0, space: cs,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue) else { return nil }
        ctx.setFillColor(CGColor(red: 0.1, green: 0.4, blue: 0.9, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        ctx.setFillColor(CGColor(red: 1, green: 0.6, blue: 0.1, alpha: 1))
        ctx.fillEllipse(in: CGRect(x: 80, y: 40, width: 160, height: 160))
        guard let img = ctx.makeImage() else { return nil }
        let out = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(out, UTType.jpeg.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(dest, img, nil)
        guard CGImageDestinationFinalize(dest) else { return nil }
        return out as Data
    }
}
