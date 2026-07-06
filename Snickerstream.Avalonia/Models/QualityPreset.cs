namespace SnickerstreamV2.Models;

/// <summary>
/// A one-click quality/framerate preset that writes priority factor, image quality and QoS.
/// Built-ins are fixed; users can save/delete custom ones (persisted in settings).
/// </summary>
public class QualityPreset
{
    public string Name { get; set; } = "";
    public int Factor { get; set; }
    public int Quality { get; set; }
    public int Qos { get; set; }

    public QualityPreset() { }
    public QualityPreset(string name, int factor, int quality, int qos)
    {
        Name = name; Factor = factor; Quality = quality; Qos = qos;
    }

    public static readonly IReadOnlyList<QualityPreset> BuiltIns = new[]
    {
        new QualityPreset("Best quality",    2, 90, 10),
        new QualityPreset("Great quality",   5, 80, 18),
        new QualityPreset("Good quality",    5, 75, 18),
        new QualityPreset("Balanced",        5, 70, 20),
        new QualityPreset("Good framerate",  8, 60, 26),
        new QualityPreset("Great framerate", 8, 50, 26),
        new QualityPreset("Best framerate", 10, 40, 34),
    };

    public bool Matches(int factor, int quality, int qos)
        => Factor == factor && Quality == quality && Qos == qos;
}

/// <summary>App identity / version, kept in sync with the GitHub release tag.</summary>
public static class AppInfo
{
    public const string Version = "2.5";
    public const string Repo = "samaBR85/3DSnickerStream";
    public const string ReleasesUrl = "https://github.com/samaBR85/3DSnickerStream/releases";
}
