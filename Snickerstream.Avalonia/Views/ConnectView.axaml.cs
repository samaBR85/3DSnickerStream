using System.Diagnostics;
using System.Net;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SnickerstreamV2.Models;
using SnickerstreamV2.Net;

namespace SnickerstreamV2.Views;

public partial class ConnectView : UserControl
{
    private readonly MainWindow _owner;
    private AppSettings S => App.Settings;
    private IStreamClient? _connecting;
    private bool _loaded;

    private CancellationTokenSource? _scanCts;
    private readonly List<string> _foundIps = new();
    private bool _autoConnected;         // AutoConnect fired once for the current scan
    private int _scanGen;                // supersede stale/cancelled scans
    private bool _reconnectPending;      // Try Reconnect loop is active
    private DispatcherTimer? _reconnectTimer;

    private bool _applyingPreset;        // suppress preset re-detection while writing slider values
    private bool _suppressPreset;        // suppress the selection handler while rebuilding/selecting
    private const string TagCustom = "__custom__";
    private const string TagAdd = "__add__";
    private const string TagDelete = "__delete__";

    public ConnectView() : this(null!) { }   // designer

    public ConnectView(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        LoadFromSettings();
        WireEvents();
        _loaded = true;

        Loaded += (_, _) =>
        {
            RefreshChipHighlight();
            if (_owner == null) return;
            _ = MaybeCheckForUpdatesAsync();
            if (_owner.ConsumeReconnect()) ScheduleReconnect();                 // stream dropped → retry loop
            else if (S.ScanOnStartup && _owner.ConsumeStartupScan()) StartScan();  // only at app launch
        };
    }

    private static IBrush Brush(string key)
        => Application.Current!.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Transparent;

    // ===================== Load / persist =====================

    private void LoadFromSettings()
    {
        Oct1.Text = S.Ip1; Oct2.Text = S.Ip2; Oct3.Text = S.Ip3; Oct4.Text = S.Ip4;
        TxtPort.Text = S.ListenPort.ToString();

        SldPriority.Value = S.PriorityFactor;
        SldQuality.Value = S.ImageQuality;
        SldQos.Value = S.Qos;
        SldHzQuality.Value = S.HzQuality;
        SldHzCpu.Value = S.HzCpuLimit;

        ApplyProtocol(S.Protocol);
        ApplyPriority(S.PriorityScreenTop);
        UpdateLabels();

        ChkScanStartup.IsChecked = S.ScanOnStartup;
        ChkAutoConnect.IsChecked = S.AutoConnect;
        ChkTryReconnect.IsChecked = S.TryReconnect;
        UpdateScreenshotLabel();
        RebuildChips();
        BuildPresetItems();
        BuildCompressionItems();

        CmbUiScale.SelectedIndex = UiScaling.IndexFor(S.UiScale);
        ApplyUiScale();
    }

    private void ApplyUiScale()
    {
        double scale = UiScaling.Steps[CmbUiScale.SelectedIndex] * 0.9;   // 0.9 = the existing "compact" base look
        var t = (ScaleTransform)UiScaleHost.LayoutTransform!;
        t.ScaleX = scale;
        t.ScaleY = scale;

        // LayoutTransformControl reports the transformed DesiredSize correctly, but SizeToContent only
        // resolves it on the NEXT full layout pass triggered by a SizeToContent change — a runtime scale
        // change alone doesn't re-fit the window, leaving it the old size with the (now differently
        // sized) content adrift inside it. Toggling forces that re-fit.
        if (_owner != null && _owner.SizeToContent == SizeToContent.WidthAndHeight)
        {
            _owner.SizeToContent = SizeToContent.Manual;
            _owner.SizeToContent = SizeToContent.WidthAndHeight;
        }
    }

    // NTR-HR compression format dropdown. Only implemented modes are offered (grows each phase);
    // Tag carries the ntr_rp_config kcp_mode value. Phase 1: JPEG Compat (0) + Uncompressed (3).
    // Phase 2: JPEG (Reliable Stream) (1) — smooth, retransmitted over the KCP transport.
    private static readonly (string Label, int KcpMode)[] CompressionModes =
    {
        ("JPEG Compat (UDP)", 0),
        ("JPEG (Reliable Stream)", 1),
        ("JPEG (Reliable Stream, Delta)", 2),
        ("Uncompressed (UDP)", 3),
        ("Lossless (Reliable Stream)", 4),
        ("Lossless (Reliable Stream, Delta)", 5),
    };

    private void BuildCompressionItems()
    {
        CmbCompression.Items.Clear();
        int sel = 0;
        for (int i = 0; i < CompressionModes.Length; i++)
        {
            CmbCompression.Items.Add(new ComboBoxItem { Content = CompressionModes[i].Label, Tag = CompressionModes[i].KcpMode });
            if (CompressionModes[i].KcpMode == S.NtrKcpMode) sel = i;
        }
        CmbCompression.SelectedIndex = sel;
    }

    private void WireEvents()
    {
        BtnNtr.Click += (_, _) => { ApplyProtocol(Protocol.NTR); S.Protocol = Protocol.NTR; S.Save(); };
        BtnHz.Click += (_, _) => { ApplyProtocol(Protocol.HzMod); S.Protocol = Protocol.HzMod; S.Save(); };
        BtnPrioTop.Click += (_, _) => { ApplyPriority(true); S.PriorityScreenTop = true; S.Save(); };
        BtnPrioBottom.Click += (_, _) => { ApplyPriority(false); S.PriorityScreenTop = false; S.Save(); };

        foreach (var box in new[] { Oct1, Oct2, Oct3, Oct4 })
            box.TextChanged += (_, _) => { if (_loaded) OctetChanged(box); };
        TxtPort.TextChanged += (_, _) => { if (_loaded && int.TryParse(TxtPort.Text, out var p)) { S.ListenPort = System.Math.Clamp(p, 1, 65535); S.Save(); } };

        BindSlider(SldPriority, v => { S.PriorityFactor = (int)v; DetectPreset(); });
        BindSlider(SldQuality, v => { S.ImageQuality = (int)v; DetectPreset(); });
        BindSlider(SldQos, v => { S.Qos = (int)v; DetectPreset(); });
        BindSlider(SldHzQuality, v => { S.HzQuality = (int)v; DetectPreset(); });
        BindSlider(SldHzCpu, v => { S.HzCpuLimit = (int)v; });
        CmbPreset.SelectionChanged += (_, _) => PresetSelected();
        CmbCompression.SelectionChanged += (_, _) =>
        {
            if (_loaded && CmbCompression.SelectedItem is ComboBoxItem it && it.Tag is int m) { S.NtrKcpMode = m; S.Save(); }
        };

        BtnBookmark.Click += (_, _) => ToggleBookmark();
        BtnFind.Click += (_, _) => StartScan();
        BtnChooseFolder.Click += (_, _) => _ = ChooseFolder();
        BtnAbout.Click += (_, _) => ShowAbout();
        BtnUpdateDownload.Click += (_, _) => OpenUrl(_pendingUpdateUrl);
        BtnUpdateDismiss.Click += (_, _) => UpdateBanner.IsVisible = false;
        ChkScanStartup.IsCheckedChanged += (_, _) => { if (_loaded) { S.ScanOnStartup = ChkScanStartup.IsChecked == true; S.Save(); } };
        ChkAutoConnect.IsCheckedChanged += (_, _) => { if (_loaded) { S.AutoConnect = ChkAutoConnect.IsChecked == true; S.Save(); } };
        ChkTryReconnect.IsCheckedChanged += (_, _) => { if (_loaded) { S.TryReconnect = ChkTryReconnect.IsChecked == true; S.Save(); } };

        BtnConnect.Click += (_, _) => OnConnect();
        BtnCancel.Click += (_, _) => CancelConnect();

        CmbUiScale.SelectionChanged += (_, _) =>
        {
            if (!_loaded) return;
            S.UiScale = UiScaling.Steps[CmbUiScale.SelectedIndex];
            ApplyUiScale();
            S.Save();
        };
    }

    private void BindSlider(Slider s, System.Action<double> apply)
        => s.PropertyChanged += (_, e) =>
        {
            if (!_loaded || e.Property != RangeBase.ValueProperty) return;
            apply(s.Value); UpdateLabels(); S.Save();
        };

    private void OctetChanged(TextBox box)
    {
        var t = box.Text ?? "";
        if (int.TryParse(t, out var v) && v > 255) { box.Text = "255"; return; }  // re-enters, then persists
        S.Ip1 = Oct1.Text ?? "0"; S.Ip2 = Oct2.Text ?? "0"; S.Ip3 = Oct3.Text ?? "0"; S.Ip4 = Oct4.Text ?? "0";
        S.Save();
        RefreshChipHighlight();
    }

    private void FillIp(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return;
        Oct1.Text = parts[0]; Oct2.Text = parts[1]; Oct3.Text = parts[2]; Oct4.Text = parts[3];
    }

    // ===================== Saved IP chips + bookmark =====================

    private void RebuildChips()
    {
        ChipsPanel.Items.Clear();
        foreach (var ip in S.SavedIps)
            ChipsPanel.Items.Add(BuildChip(ip));
        RefreshChipHighlight();
    }

    private Control BuildChip(string ip)
    {
        var text = new TextBlock { Text = ip, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
        var close = new TextBlock { Text = "✕", FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextMutedBrush") };
        close.PointerPressed += (_, e) => { e.Handled = true; RemoveSavedIp(ip); };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(text);
        sp.Children.Add(close);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            BorderBrush = Brush("FieldBorderBrush"),
            Child = sp,
            Tag = ip
        };
        chip.PointerPressed += (_, _) => FillIp(ip);
        return chip;
    }

    private void RefreshChipHighlight()
    {
        foreach (var item in ChipsPanel.Items)
        {
            if (item is Border chip && chip.Tag is string ip)
            {
                bool current = ip == CurrentIp;
                chip.Background = current ? Brush("BrandBrush") : Brush("FieldBackgroundBrush");
                chip.BorderThickness = new Thickness(current ? 0 : 1);
            }
        }
        UpdateBookmarkColor();
    }

    private void ToggleBookmark()
    {
        var ip = CurrentIp;
        if (S.SavedIps.Contains(ip)) S.SavedIps.Remove(ip);
        else S.RememberIp(ip);
        S.Save();
        RebuildChips();
    }

    private void RemoveSavedIp(string ip)
    {
        S.SavedIps.Remove(ip);
        S.Save();
        RebuildChips();
    }

    private void UpdateBookmarkColor()
        => BtnBookmark.Foreground = S.SavedIps.Contains(CurrentIp) ? Brush("BrandEndBrush") : Brush("TextMutedBrush");

    // ===================== Network scan =====================

    private async void StartScan()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        int gen = ++_scanGen;
        _foundIps.Clear();
        _autoConnected = false;
        TxtScanStatus.IsVisible = true;
        TxtScanStatus.Foreground = Brush("TextSecondaryBrush");
        TxtScanStatus.Text = "Scanning…";

        bool hz = S.Protocol == Protocol.HzMod;
        var token = _scanCts.Token;
        try
        {
            await NetworkScanner.ScanAsync(hz, token, ip =>
                Dispatcher.UIThread.Post(() => { if (gen == _scanGen) AddFound(ip); }));
        }
        catch { }

        if (gen == _scanGen && _foundIps.Count == 0)
            TxtScanStatus.Text = "No device found";
    }

    private void AddFound(string ip)
    {
        if (_foundIps.Contains(ip)) return;
        _foundIps.Add(ip);

        string first = _foundIps[0];
        TxtScanStatus.Foreground = new SolidColorBrush(Color.Parse("#43D17A"));   // found = green
        TxtScanStatus.Text = _foundIps.Count == 1 ? $"Found {first}" : $"Found {first}  (+{_foundIps.Count - 1})";
        FillIp(first);
        RefreshChipHighlight();

        if (S.AutoConnect && !_autoConnected)
        {
            _autoConnected = true;
            OnConnect();
        }
    }

    // ===================== Screenshot folder =====================

    private void UpdateScreenshotLabel()
    {
        var f = string.IsNullOrWhiteSpace(S.ScreenshotFolder) ? AppSettings.DefaultScreenshotFolder : S.ScreenshotFolder;
        TxtScreenshotFolder.Text = ShortenPath(f);   // short label so it never collides with the Change button
        ToolTip.SetTip(TxtScreenshotFolder, f);      // full path on hover
    }

    private static string ShortenPath(string path)
    {
        var parts = path.TrimEnd('\\', '/').Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : "…\\" + parts[^2] + "\\" + parts[^1];
    }

    private async Task ChooseFolder()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var start = string.IsNullOrWhiteSpace(S.ScreenshotFolder) ? AppSettings.DefaultScreenshotFolder : S.ScreenshotFolder;
        IStorageFolder? startFolder = null;
        try { startFolder = await top.StorageProvider.TryGetFolderFromPathAsync(start); } catch { }

        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for screenshots",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            S.ScreenshotFolder = path;
            S.Save();
            UpdateScreenshotLabel();
        }
    }

    // ===================== Updates / About =====================

    private string _pendingUpdateUrl = AppInfo.ReleasesUrl;

    private async Task MaybeCheckForUpdatesAsync()
    {
        if (!S.CheckUpdatesOnStartup) return;
        var info = await UpdateChecker.CheckAsync();
        if (info is { Available: true })
            Dispatcher.UIThread.Post(() => ShowUpdateBanner(info));
    }

    private void ShowUpdateBanner(UpdateInfo info)
    {
        UpdateBannerText.Text = $"Update available — v{info.LatestVersion}";
        _pendingUpdateUrl = info.Url;
        UpdateBanner.IsVisible = true;
    }

    private void ShowAbout()
    {
        if (_owner == null) return;
        var about = new AboutWindow(async () =>
        {
            var info = await UpdateChecker.CheckAsync();
            if (info == null) return "Could not check (offline?).";
            if (info.Available)
            {
                ShowUpdateBanner(info);
                return $"Update available — v{info.LatestVersion}";
            }
            return "You're on the latest version.";
        });
        about.ShowDialog(_owner);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void UpdateLabels()
    {
        ValPriority.Text = ((int)SldPriority.Value).ToString();
        ValQuality.Text = ((int)SldQuality.Value).ToString();
        ValQos.Text = ((int)SldQos.Value).ToString();
        ValHzQuality.Text = ((int)SldHzQuality.Value).ToString();
        ValHzCpu.Text = ((int)SldHzCpu.Value).ToString();
    }

    private void ApplyProtocol(Protocol p)
    {
        bool ntr = p == Protocol.NTR;
        PanelNtr.IsVisible = ntr;
        PanelHz.IsVisible = !ntr;
        Highlight(BtnNtr, ntr);
        Highlight(BtnHz, !ntr);
        if (_loaded) UpdatePresetLabel();
    }

    private void ApplyPriority(bool top)
    {
        Highlight(BtnPrioTop, top);
        Highlight(BtnPrioBottom, !top);
    }

    private static void Highlight(Button b, bool on)
        => b.Classes.Set("selected", on);   // brand-tinted background via the .ghost.selected style

    private string CurrentIp => $"{Norm(Oct1.Text)}.{Norm(Oct2.Text)}.{Norm(Oct3.Text)}.{Norm(Oct4.Text)}";
    private static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? "0" : s;

    // ===================== Quality presets =====================

    private void BuildPresetItems()
    {
        _suppressPreset = true;
        CmbPreset.Items.Clear();

        CmbPreset.Items.Add(MakePresetItem("Custom", TagCustom));
        foreach (var p in QualityPreset.BuiltIns)
            CmbPreset.Items.Add(MakePresetItem(p.Name, p));
        foreach (var p in S.CustomPresets)
            CmbPreset.Items.Add(MakePresetItem($"{p.Name}  (custom)", p));

        CmbPreset.Items.Add(MakePresetItem("＋  Add custom preset…", TagAdd));
        if (S.CustomPresets.Count > 0)
            CmbPreset.Items.Add(MakePresetItem("🗑  Delete custom preset…", TagDelete));

        _suppressPreset = false;
        UpdatePresetLabel();
    }

    private static ComboBoxItem MakePresetItem(string text, object tag) => new() { Content = text, Tag = tag };

    private void PresetSelected()
    {
        if (_suppressPreset) return;
        if (CmbPreset.SelectedItem is not ComboBoxItem item) return;

        switch (item.Tag)
        {
            case QualityPreset p: ApplyPreset(p); break;
            case string s when s == TagAdd: _ = AddCustomPreset(); break;
            case string s when s == TagDelete: _ = DeleteCustomPreset(); break;
            // TagCustom = no-op label
        }
    }

    private void ApplyPreset(QualityPreset p)
    {
        _applyingPreset = true;
        SldPriority.Value = p.Factor;
        SldQuality.Value = p.Quality;
        SldQos.Value = p.Qos;
        SldHzQuality.Value = p.Quality;   // HzMod preset = quality only
        _applyingPreset = false;
        UpdatePresetLabel();
    }

    private async Task AddCustomPreset()
    {
        var name = await Dialogs.PromptText(_owner, "Add custom preset", "Preset name:", "My preset");
        if (!string.IsNullOrWhiteSpace(name))
        {
            S.CustomPresets.RemoveAll(x => x.Name == name);
            S.CustomPresets.Add(new QualityPreset(name.Trim(), S.PriorityFactor, S.ImageQuality, S.Qos));
            S.Save();
            BuildPresetItems();
        }
        else UpdatePresetLabel();
    }

    private async Task DeleteCustomPreset()
    {
        if (S.CustomPresets.Count == 0) { UpdatePresetLabel(); return; }
        var pick = await Dialogs.ChooseFromList(_owner, "Delete custom preset", S.CustomPresets.Select(p => p.Name).ToList());
        if (pick != null)
        {
            S.CustomPresets.RemoveAll(x => x.Name == pick);
            S.Save();
            BuildPresetItems();
        }
        else UpdatePresetLabel();
    }

    private void DetectPreset()
    {
        if (_applyingPreset) return;
        UpdatePresetLabel();
    }

    /// <summary>Selects the matching preset in the dropdown, or "Custom" if none match.</summary>
    private void UpdatePresetLabel()
    {
        if (CmbPreset.ItemCount == 0) return;
        var match = FindMatchingPreset();
        _suppressPreset = true;
        ComboBoxItem? target = null;
        foreach (var obj in CmbPreset.Items)
        {
            if (obj is not ComboBoxItem ci) continue;
            if (match != null && ReferenceEquals(ci.Tag, match)) { target = ci; break; }
            if (match == null && (ci.Tag as string) == TagCustom) { target = ci; break; }
        }
        CmbPreset.SelectedItem = target;
        _suppressPreset = false;
    }

    private QualityPreset? FindMatchingPreset()
    {
        bool hz = S.Protocol == Protocol.HzMod;
        IEnumerable<QualityPreset> all = QualityPreset.BuiltIns.Concat(S.CustomPresets);
        return hz
            ? all.FirstOrDefault(p => p.Quality == S.HzQuality)
            : all.FirstOrDefault(p => p.Matches(S.PriorityFactor, S.ImageQuality, S.Qos));
    }

    // ===================== Connect =====================

    private void OnConnect()
    {
        if (!IPAddress.TryParse(CurrentIp, out _)) { SetStatus("Invalid IP address", StatusKind.Error); return; }
        if (!int.TryParse(TxtPort.Text, out var port) || port is < 1 or > 65535) { SetStatus("Invalid listen port", StatusKind.Error); return; }

        var ip = CurrentIp;
        S.RememberIp(ip);
        RebuildChips();
        S.Save();

        IStreamClient client = S.Protocol == Protocol.NTR
            // Uncompressed(UDP) tuning (pinned for now; a UI knob comes later):
            //   bandwidth 32 Mbit — 16 caps the 3DS at ~20 fps for a 102 KB bias-1 frame; 32 lifts that.
            //   losslessColor 1 → color_bias 1 (Y6/Cb5/Cr5): good colour, still deliverable over UDP.
            ? new NTRClient(ip, port, S.ImageQuality, S.PriorityFactor, S.PriorityScreenTop, S.Qos,
                            S.NtrKcpMode, S.NtrKcpMode == 0 ? S.NtrBandwidth : 32, 1)
            : new HzModClient(ip, S.HzQuality, S.HzCpuLimit);

        _connecting = client;
        SetConnectingUi(true);
        SetStatus("Connecting… (1/3)", StatusKind.Connecting);

        client.Status += msg => Dispatcher.UIThread.Post(() => SetStatus(msg, StatusKind.Connecting));
        client.FirstFrame += () => Dispatcher.UIThread.Post(() =>
        {
            var c = _connecting;
            _connecting = null;
            _reconnectPending = false;
            SetConnectingUi(false);
            if (c != null) _owner.ShowStream(c, S.Protocol);
        });
        client.Failed += msg => Dispatcher.UIThread.Post(() =>
        {
            CleanupConnecting();
            SetStatus(msg, StatusKind.Error);
            if (S.TryReconnect) ScheduleReconnect();   // keep retrying until it connects / Cancel
        });

        client.Start();
    }

    /// <summary>Try Reconnect loop: after a short delay, attempt to connect again to the current IP.</summary>
    private void ScheduleReconnect()
    {
        if (!S.TryReconnect) return;
        _reconnectPending = true;
        _reconnectTimer?.Stop();
        SetStatus("Reconnecting…", StatusKind.Connecting);
        SetConnectingUi(true);                          // show Cancel so the user can stop the loop
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reconnectTimer.Tick += (_, _) =>
        {
            _reconnectTimer?.Stop();
            if (_reconnectPending && _connecting == null) OnConnect();
        };
        _reconnectTimer.Start();
    }

    private void CancelConnect()
    {
        _reconnectPending = false;
        _reconnectTimer?.Stop();
        CleanupConnecting();
        SetStatus("Idle", StatusKind.Idle);
    }

    private void CleanupConnecting()
    {
        try { _connecting?.Stop(); } catch { }
        _connecting?.Dispose();
        _connecting = null;
        SetConnectingUi(false);
    }

    private void SetConnectingUi(bool connecting)
    {
        BtnCancel.IsVisible = connecting;
        BtnConnect.IsEnabled = !connecting;
        BtnConnect.Content = connecting ? "Connecting…" : "Connect";
    }

    private enum StatusKind { Idle, Connecting, Error }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusText.Foreground = kind == StatusKind.Error ? Brushes.OrangeRed : new SolidColorBrush(Color.Parse("#B3FFFFFF"));
        StatusDot.Fill = kind switch
        {
            StatusKind.Connecting => Brushes.Orange,
            StatusKind.Error => Brushes.OrangeRed,
            _ => new SolidColorBrush(Color.Parse("#888888"))
        };
    }
}
