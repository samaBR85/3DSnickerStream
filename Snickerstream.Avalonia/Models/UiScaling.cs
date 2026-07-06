namespace SnickerstreamV2.Models;

/// <summary>
/// Shared fixed-step UI scale (separate from the stream's own "Zoom", which only affects the 3DS
/// screens) — used by both ConnectView and StreamView's chrome. A <c>Slider</c> in each view snaps to
/// one of these steps by index; <see cref="AppSettings.UiScale"/> stores the resulting multiplier.
/// </summary>
public static class UiScaling
{
    public static readonly double[] Steps = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

    public static int IndexFor(double scale)
    {
        int best = 2;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < Steps.Length; i++)
        {
            double d = System.Math.Abs(Steps[i] - scale);
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        return best;
    }

    public static string Label(double scale) => $"{(int)System.Math.Round(scale * 100)}%";
}
