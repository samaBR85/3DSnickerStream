#if WINDOWS_NEURAL
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SnickerstreamV2.Views;

/// <summary>
/// Neural ("AI") upscaler — runs a Real-ESRGAN Compact network (SRVGGNetCompact, ×4, BSD-3-Clause,
/// <c>realesr-general-x4v3</c>, embedded) on the GPU via the ONNX Runtime <b>DirectML</b> execution
/// provider. Windows-only (DirectML is a DX12 API); the whole file is compiled out elsewhere.
///
/// <para>Reuses the same byte[]-in / byte[]-out seam the macOS MetalFX path used: BGRA frame → planar RGB
/// float tensor → <c>Run</c> → BGRA ×4 buffer. DirectML has no parallel <c>Run</c>, so inference is
/// serialized. First use lazily builds the session; any failure (no DX12 GPU, driver, bundling) flips
/// <see cref="Available"/> to false and the caller falls back to a plain draw.</para>
/// </summary>
internal static class NeuralUpscaler
{
    public const int Scale = 4;

    private static readonly object _lock = new();
    private static InferenceSession? _session;
    private static string _inName = "input", _outName = "output";
    private static bool _tried, _ok;

    /// <summary>True once the DirectML session has initialised on a GPU. Triggers lazy init on first read.</summary>
    public static bool Available { get { EnsureInit(); return _ok; } }

    private static void EnsureInit()
    {
        if (_tried) return;
        lock (_lock)
        {
            if (_tried) return;
            _tried = true;
            try
            {
                var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                opts.EnableMemoryPattern = false;                 // recommended for the DML EP
                opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                opts.AppendExecutionProvider_DML(0);              // GPU device 0 (throws if no DX12 device)
                _session = new InferenceSession(ReadModel(), opts);
                _inName = _session.InputMetadata.Keys.First();
                _outName = _session.OutputMetadata.Keys.First();
                _ok = true;
            }
            catch
            {
                _session?.Dispose();
                _session = null;
                _ok = false;
            }
        }
    }

    private static byte[] ReadModel()
    {
        var asm = Assembly.GetExecutingAssembly();
        string res = asm.GetManifestResourceNames().First(n => n.EndsWith("realesr-general-x4v3.onnx", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(res)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Upscales a BGRA <paramref name="bgra"/> (<paramref name="w"/>×<paramref name="h"/>) ×4 on the GPU.
    /// Returns a fresh BGRA buffer with the real output dims in <paramref name="ow"/>/<paramref name="oh"/>,
    /// or null if unavailable or this frame failed. Thread-safe (Run is serialized).
    /// </summary>
    public static byte[]? Upscale(byte[] bgra, int w, int h, out int ow, out int oh)
    {
        ow = w * Scale; oh = h * Scale;
        EnsureInit();
        if (!_ok || _session == null || bgra.Length < w * h * 4) return null;
        try
        {
            int hw = w * h;
            var input = new DenseTensor<float>(new[] { 1, 3, h, w });
            var dst = input.Buffer.Span;
            for (int i = 0, p = 0; i < hw; i++, p += 4)
            {
                dst[i]           = bgra[p + 2] * (1f / 255f);   // R
                dst[hw + i]      = bgra[p + 1] * (1f / 255f);   // G
                dst[2 * hw + i]  = bgra[p]     * (1f / 255f);   // B
            }
            var feeds = new[] { NamedOnnxValue.CreateFromTensor(_inName, input) };

            float[] outArr;
            ReadOnlySpan<int> dims;
            lock (_lock)
            {
                if (_session == null) return null;
                using var results = _session.Run(feeds);
                var outT = results.First(v => v.Name == _outName).AsTensor<float>();
                dims = outT.Dimensions;                         // [1,3,OH,OW]
                oh = dims[2]; ow = dims[3];
                outArr = outT.ToArray();
            }

            int ohw = ow * oh;
            var outBuf = new byte[ohw * 4];
            for (int i = 0, p = 0; i < ohw; i++, p += 4)
            {
                outBuf[p]     = F2B(outArr[2 * ohw + i]);       // B
                outBuf[p + 1] = F2B(outArr[ohw + i]);           // G
                outBuf[p + 2] = F2B(outArr[i]);                 // R
                outBuf[p + 3] = 255;
            }
            return outBuf;
        }
        catch { return null; }
    }

    private static byte F2B(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);
}
#endif
