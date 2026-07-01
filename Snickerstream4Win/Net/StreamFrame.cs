namespace Snickerstream4Win.Net;

public enum Screen { Top, Bottom }

/// <summary>A fully reassembled, validated JPEG frame for one screen.</summary>
public sealed class StreamFrame
{
    public Screen Screen { get; }
    public byte[] Jpeg { get; }
    public StreamFrame(Screen screen, byte[] jpeg) { Screen = screen; Jpeg = jpeg; }
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
