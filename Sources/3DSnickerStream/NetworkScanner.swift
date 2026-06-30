import Foundation
import Network
import Darwin

/// Scans the local /24 subnet for a 3DS by probing the NTR (8000) and HzMod (6464) ports.
enum NetworkScanner {
    /// Ports that indicate a streaming-capable 3DS is listening.
    static let probePorts: [UInt16] = [8000, 6464]

    private static let scanQueue = DispatchQueue(label: "3dsnickerstream.scan", attributes: .concurrent)

    /// Scans `<base>.1`…`<base>.254` and returns the IPs that answered, sorted numerically.
    static func scan(timeout: TimeInterval = 0.6, maxInFlight: Int = 48) async -> [String] {
        guard let local = localIPv4Address() else { return [] }
        let comps = local.split(separator: ".")
        guard comps.count == 4 else { return [] }
        let base = "\(comps[0]).\(comps[1]).\(comps[2])."

        var found: Set<String> = []
        await withTaskGroup(of: String?.self) { group in
            var inFlight = 0
            for i in 1...254 {
                let host = base + String(i)
                if host == local { continue }
                if inFlight >= maxInFlight, let r = await group.next() {
                    if let r = r { found.insert(r) }
                    inFlight -= 1
                }
                group.addTask { await isReachable(host, ports: probePorts, timeout: timeout) ? host : nil }
                inFlight += 1
            }
            for await r in group { if let r = r { found.insert(r) } }
        }
        return found.sorted(by: ipLess)
    }

    /// True if any of `ports` accepts a TCP connection on `host` within `timeout`.
    private static func isReachable(_ host: String, ports: [UInt16], timeout: TimeInterval) async -> Bool {
        await withTaskGroup(of: Bool.self) { group in
            for p in ports {
                group.addTask { await probe(host, port: p, timeout: timeout) }
            }
            for await ok in group where ok {
                group.cancelAll()
                return true
            }
            return false
        }
    }

    /// Resume-once wrapper so a probe always resolves exactly once, from whichever
    /// path fires first (connection state callback or the independent timeout).
    private final class ProbeBox: @unchecked Sendable {
        private let lock = NSLock()
        private var cont: CheckedContinuation<Bool, Never>?
        func arm(_ c: CheckedContinuation<Bool, Never>) { lock.lock(); cont = c; lock.unlock() }
        func finish(_ value: Bool, cleanup: () -> Void) {
            lock.lock(); let c = cont; cont = nil; lock.unlock()
            guard let c = c else { return }
            cleanup()
            c.resume(returning: value)
        }
    }

    /// Attempts a single TCP connection, resolving true on `.ready`, false on failure/timeout.
    /// The timeout runs on the cooperative pool (via Task.sleep) and resumes the continuation
    /// directly, so a probe never hangs even if the Network callback queue is saturated.
    private static func probe(_ host: String, port: UInt16, timeout: TimeInterval) async -> Bool {
        guard let nwPort = NWEndpoint.Port(rawValue: port),
              let addr = IPv4Address(host) else { return false }
        let conn = NWConnection(host: .ipv4(addr), port: nwPort, using: .tcp)
        let box = ProbeBox()
        return await withCheckedContinuation { (cont: CheckedContinuation<Bool, Never>) in
            box.arm(cont)
            conn.stateUpdateHandler = { state in
                switch state {
                case .ready:            box.finish(true) { conn.cancel() }
                case .failed, .cancelled: box.finish(false) { }
                default: break
                }
            }
            conn.start(queue: scanQueue)
            Task.detached {
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                box.finish(false) { conn.cancel() }
            }
        }
    }

    /// Numeric IPv4 comparison so 192.168.1.9 sorts before 192.168.1.10.
    private static func ipLess(_ a: String, _ b: String) -> Bool {
        let pa = a.split(separator: ".").compactMap { Int($0) }
        let pb = b.split(separator: ".").compactMap { Int($0) }
        return pa.lexicographicallyPrecedes(pb)
    }

    /// The Mac's primary IPv4 address on a real LAN — skips loopback and link-local
    /// (169.254.x) self-assigned addresses, preferring a private (10/172.16–31/192.168) range.
    static func localIPv4Address() -> String? {
        var candidates: [String] = []
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return nil }
        defer { freeifaddrs(ifaddr) }

        var ptr: UnsafeMutablePointer<ifaddrs>? = first
        while let cur = ptr {
            let flags = Int32(cur.pointee.ifa_flags)
            if let sa = cur.pointee.ifa_addr,
               sa.pointee.sa_family == UInt8(AF_INET),
               (flags & (IFF_UP | IFF_RUNNING | IFF_LOOPBACK)) == (IFF_UP | IFF_RUNNING) {
                let name = String(cString: cur.pointee.ifa_name)
                if name.hasPrefix("en") || name.hasPrefix("bridge") {
                    var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                    if getnameinfo(sa, socklen_t(sa.pointee.sa_len), &host, socklen_t(host.count),
                                   nil, 0, NI_NUMERICHOST) == 0 {
                        let ip = String(cString: host)
                        if !ip.hasPrefix("127.") && !ip.hasPrefix("169.254.") {
                            candidates.append(ip)
                        }
                    }
                }
            }
            ptr = cur.pointee.ifa_next
        }
        return candidates.first(where: isPrivate) ?? candidates.first
    }

    private static func isPrivate(_ ip: String) -> Bool {
        if ip.hasPrefix("10.") || ip.hasPrefix("192.168.") { return true }
        let p = ip.split(separator: ".")
        if p.count == 4, p[0] == "172", let second = Int(p[1]), (16...31).contains(second) { return true }
        return false
    }
}
