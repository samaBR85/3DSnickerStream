using System;
using System.Runtime.InteropServices;

namespace SnickerstreamV2;

/// <summary>
/// macOS/Apple-Silicon MetalFX spatial upscaler (see <c>native/metalfx_helper.m</c>). MetalFX is Apple's
/// ML-assisted upscaler (built on AMD FSR2) — the highest-quality option on Apple Silicon. A frame's native
/// BGRA8 bytes go to the native helper, which runs <c>MTLFXSpatialScaler</c> and returns the upscaled BGRA8
/// bytes; the app then draws that like any other upscaled frame (so rotation/gap/zoom still apply).
/// Availability is probed once and cached; everything is a no-op returning <c>false</c>/<c>null</c> off macOS.
/// </summary>
internal static class MetalFxUpscaler
{
    private const string Lib = "metalfx_helper";   // libmetalfx_helper.dylib, bundled next to the app binary

    [DllImport(Lib, EntryPoint = "mfx_available")]
    private static extern int NativeAvailable();

    [DllImport(Lib, EntryPoint = "mfx_upscale")]
    private static extern int NativeUpscale(byte[] src, int inW, int inH, int outW, int outH, byte[] dst);

    private static readonly Lazy<bool> _available = new(() =>
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try { return NativeAvailable() == 1; }
        catch { return false; }   // dylib missing / load failure → feature simply unavailable
    });

    /// <summary>True if MetalFX spatial scaling can run on this machine.</summary>
    public static bool Available => _available.Value;

    [ThreadStatic] private static byte[]? _buf;

    /// <summary>
    /// Upscales a native BGRA8 frame (<paramref name="src"/>, <paramref name="inW"/>×<paramref name="inH"/>)
    /// to <paramref name="outW"/>×<paramref name="outH"/>. Returns a buffer owned by this class (valid until
    /// the next call on the same thread — copy out before reuse), or <c>null</c> if MetalFX failed/unavailable.
    /// </summary>
    public static byte[]? Upscale(byte[] src, int inW, int inH, int outW, int outH)
    {
        if (!Available || outW <= 0 || outH <= 0) return null;
        int need = checked(outW * outH * 4);
        if (_buf == null || _buf.Length != need) _buf = new byte[need];
        try { return NativeUpscale(src, inW, inH, outW, outH, _buf) == 0 ? _buf : null; }
        catch { return null; }
    }
}
