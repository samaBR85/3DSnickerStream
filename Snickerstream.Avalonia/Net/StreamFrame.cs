namespace SnickerstreamV2.Net;

public enum Screen { Top, Bottom }

/// <summary>How <see cref="StreamFrame.Payload"/> is encoded, so the view decodes it correctly.</summary>
public enum FrameKind
{
    /// <summary>Standard JFIF JPEG (JPEG Compat / Reliable Stream) — decode via Avalonia Bitmap.</summary>
    Jpeg,
    /// <summary>NTR-HR "Uncompressed (UDP)" — bespoke YCbCr planar, chroma-subsampled, bit-packed.</summary>
    RawLossless,
    /// <summary>Already-decoded BGRA pixels (<see cref="StreamFrame.Width"/>×<see cref="StreamFrame.Height"/>,
    /// portrait). Used by "JPEG (Reliable Stream, Delta)", whose bespoke decoder holds cross-frame state
    /// and so must run in the network layer — the view just wraps the pixels.</summary>
    RawBgra,
}

/// <summary>A fully reassembled frame for one screen. Kind selects the decode path.</summary>
public sealed class StreamFrame
{
    public Screen Screen { get; }
    /// <summary>Raw payload bytes (JPEG bitstream, or packed lossless data per <see cref="Kind"/>).</summary>
    public byte[] Jpeg { get; }
    public FrameKind Kind { get; }
    /// <summary>NTR-HR downsample level (hdr[2] bits 2-3), RawLossless only.</summary>
    public int Downsample { get; }
    /// <summary>Interlace phase = frame_id % 2 (hdr[0]), RawLossless only.</summary>
    public int EvenOdd { get; }
    /// <summary>Pixel dimensions of a <see cref="FrameKind.RawBgra"/> payload (0 otherwise).</summary>
    public int Width { get; }
    public int Height { get; }

    public StreamFrame(Screen screen, byte[] jpeg, FrameKind kind = FrameKind.Jpeg, int downsample = 0, int evenOdd = 0,
                       int width = 0, int height = 0)
    {
        Screen = screen; Jpeg = jpeg; Kind = kind; Downsample = downsample; EvenOdd = evenOdd;
        Width = width; Height = height;
    }
}

/// <summary>
/// Common surface implemented by NTR and HzMod clients so the streaming view can
/// drive either one identically.
/// </summary>
public interface IStreamClient : IDisposable
{
    /// <summary>Raised on the threadpool with each complete JPEG frame.</summary>
    event Action<StreamFrame>? FrameReady;

    /// <summary>Human-readable connection status, e.g. "Connecting… (1/3)".</summary>
    event Action<string>? Status;

    /// <summary>Terminal failure; the view should return to the menu.</summary>
    event Action<string>? Failed;

    /// <summary>Fired once when the first frame arrives (idle/connecting -> streaming).</summary>
    event Action? FirstFrame;

    void Start();
    void Stop();

    /// <summary>Live quality change (re-init for NTR, re-send quality for HzMod).</summary>
    void SetQuality(int quality);
}
