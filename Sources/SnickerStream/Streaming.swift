import Foundation

/// The 3DS-side streaming homebrew to talk to.
enum StreamProtocol: String, CaseIterable, Identifiable, Codable {
    case ntr = "NTR"
    case hzmod = "HzMod"
    var id: String { rawValue }
}

/// Configuration for a streaming session (covers both NTR and HzMod).
struct StreamConfig {
    var proto: StreamProtocol = .ntr
    var ip: String
    var listenPort: UInt16 = 8001          // NTR UDP listen port
    var priorityScreen: Screen = .top      // NTR
    var priorityFactor: UInt8 = 5          // NTR
    var quality: UInt8 = 70                 // both
    var qos: UInt8 = 20                     // NTR
    var cpuLimit: UInt8 = 128               // HzMod CPU cap (0–255)
}

/// Common surface for a streaming backend (NTR or HzMod).
protocol StreamClient: AnyObject {
    /// Delivers a complete JPEG for a screen (called off the main thread).
    var onFrame: ((Screen, Data) -> Void)? { get set }
    /// Status/log line for the UI.
    var onStatus: ((String) -> Void)? { get set }

    func start()
    func stop()
    /// Re-attempt the connection/init (used by the watchdog on no-frame retries).
    func retry()
    /// Live quality change (NTR re-inits; HzMod sends a quality packet).
    func setQuality(_ quality: UInt8)
    /// Swap the priority screen (NTR only; no-op where unsupported).
    func swapPriority()
}
