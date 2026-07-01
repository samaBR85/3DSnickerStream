using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snickerstream4Win.Models;

public enum Protocol { NTR, HzMod }
public enum ScreenLayout { Stacked, SideBySide, TopOnly, BottomOnly }
public enum Interpolation { Sharp, Linear, Smooth }

/// <summary>
/// Remappable in-stream actions. The string key/name is used both for UI labels
/// and for JSON persistence of the keybinding map.
/// </summary>
public enum ShortcutAction
{
    Screenshot,
    ScreenshotToClipboard,
    Disconnect,
    CycleLayout,
    CycleFilter,
    RotateScreen,
    ToggleFullscreen,
    IncreaseQuality,
    DecreaseQuality,
    SwapPriorityScreen
}

/// <summary>
/// All persisted user state. Saved as JSON in
/// %APPDATA%\Snickerstream4Win\settings.json.
/// </summary>
public class AppSettings
{
    // Remoteplay
    public Protocol Protocol { get; set; } = Protocol.NTR;
    public string Ip1 { get; set; } = "192";
    public string Ip2 { get; set; } = "168";
    public string Ip3 { get; set; } = "0";
    public string Ip4 { get; set; } = "0";

    public bool PriorityScreenTop { get; set; } = true;   // true = Top, false = Bottom
    public int PriorityFactor { get; set; } = 5;          // 0..10
    public int ImageQuality { get; set; } = 70;           // NTR 10..100
    public int Qos { get; set; } = 20;                    // 2..100

    // HzMod
    public int HzQuality { get; set; } = 70;              // 1..100
    public int HzCpuLimit { get; set; } = 0;              // 0..255

    // Display
    public int ListenPort { get; set; } = 8001;
    public ScreenLayout Layout { get; set; } = ScreenLayout.Stacked;
    public Interpolation Interpolation { get; set; } = Interpolation.Sharp;
    // User-facing rotation OFFSET. The 270° upright correction is baked in, so 0° = upright.
    // Rendered clockwise angle = (270 + Rotation) % 360.
    public int Rotation { get; set; } = 0;                // 0/90/180/270, default 0 = upright
    public int MaxFps { get; set; } = 30;                 // 0 = unlimited
    public bool AmbientGlow { get; set; } = true;
    public string ScreenshotFolder { get; set; } = "";

    // Per-screen scale weights (0.5..2.0). Affect proportional split in the stream view.
    public double TopScale { get; set; } = 1.0;
    public double BottomScale { get; set; } = 1.0;

    // Custom quality presets (built-ins live in QualityPreset.BuiltIns).
    public List<QualityPreset> CustomPresets { get; set; } = new();

    // Updates
    public bool CheckUpdatesOnStartup { get; set; } = true;

    // Bumped when the meaning of a persisted field changes, to drive migrations.
    public int SchemaVersion { get; set; } = 0;
    private const int CurrentSchema = 2;

    // Saved IPs (most recent first, max 8)
    public List<string> SavedIps { get; set; } = new();

    // Keybindings: action name -> WPF Key name (e.g. "S", "Escape", "Up")
    public Dictionary<string, string> KeyBindings { get; set; } = new();

    // ---- Persistence ----

    [JsonIgnore]
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "3DSnickerStream");

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    [JsonIgnore]
    public static string DefaultScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "3DSnickerStream");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s != null) { s.Normalize(); return s; }
            }
        }
        catch { /* fall through to defaults */ }
        var def = new AppSettings();
        def.Normalize();
        return def;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* best effort */ }
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ScreenshotFolder))
            ScreenshotFolder = DefaultScreenshotFolder;
        if (KeyBindings.Count == 0)
            KeyBindings = DefaultKeyBindings();
        // Add bindings introduced after the original set without clobbering user changes.
        foreach (var (k, v) in DefaultKeyBindings())
            KeyBindings.TryAdd(k, v);
        if (SavedIps.Count > 8)
            SavedIps = SavedIps.Take(8).ToList();
        TopScale = Math.Clamp(TopScale, 0.5, 2.0);
        BottomScale = Math.Clamp(BottomScale, 0.5, 2.0);

        // v2: Rotation became a 0°-upright OFFSET (was 270°-upright). Reset on upgrade.
        if (SchemaVersion < 2)
        {
            Rotation = 0;
            SchemaVersion = CurrentSchema;
        }
    }

    public static Dictionary<string, string> DefaultKeyBindings() => new()
    {
        [nameof(ShortcutAction.Screenshot)] = "S",
        [nameof(ShortcutAction.ScreenshotToClipboard)] = "Shift+S",
        [nameof(ShortcutAction.Disconnect)] = "Escape",
        [nameof(ShortcutAction.CycleLayout)] = "L",
        [nameof(ShortcutAction.CycleFilter)] = "F",
        [nameof(ShortcutAction.RotateScreen)] = "R",
        [nameof(ShortcutAction.ToggleFullscreen)] = "F11",
        [nameof(ShortcutAction.IncreaseQuality)] = "Up",
        [nameof(ShortcutAction.DecreaseQuality)] = "Down",
        [nameof(ShortcutAction.SwapPriorityScreen)] = "P",
    };

    /// <summary>Records a freshly-used IP at the front of the saved list (dedup, max 8).</summary>
    public void RememberIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        SavedIps.RemoveAll(x => x == ip);
        SavedIps.Insert(0, ip);
        if (SavedIps.Count > 8) SavedIps = SavedIps.Take(8).ToList();
    }

    [JsonIgnore]
    public string CurrentIp => $"{Ip1}.{Ip2}.{Ip3}.{Ip4}";
}
