using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Snickerstream4Win.Models;
using Snickerstream4Win.Net;

namespace Snickerstream4Win.Views;

public partial class ConnectView : UserControl
{
    private readonly MainWindow _owner;
    private AppSettings S => App.Settings;

    private IStreamClient? _connectingClient;
    private CancellationTokenSource? _scanCts;
    private readonly List<string> _foundIps = new();
    private bool _loaded;
    private bool _applyingPreset;   // suppress preset re-detection while writing slider values
    private bool _suppressPreset;   // suppress selection handler while rebuilding/selecting
    private bool _autoConnected;    // AutoConnect fired once for this scan
    private int _scanGen;           // supersede stale/cancelled scans
    private bool _reconnectPending; // Try Reconnect loop is active
    private System.Windows.Threading.DispatcherTimer? _reconnectTimer;

    private const string TagCustom = "__custom__";
    private const string TagAdd = "__add__";
    private const string TagDelete = "__delete__";

    public ConnectView(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        LoadFromSettings();
        WireEvents();
        _loaded = true;
        Loaded += (_, _) =>
        {
            UpdateSliderLabels();
            UpdatePresetLabel();
            _ = MaybeCheckForUpdatesAsync();
            if (_owner.ConsumeReconnect()) ScheduleReconnect();               // stream dropped → retry loop
            else if (S.ScanOnStartup && _owner.ConsumeStartupScan())          // only at app launch
                StartScan();
        };
    }

    // ===================== Load / persist =====================

    private void LoadFromSettings()
    {
        Oct1.Text = S.Ip1; Oct2.Text = S.Ip2; Oct3.Text = S.Ip3; Oct4.Text = S.Ip4;

        SetProtocol(S.Protocol, persist: false);
        SetSegment(SegPrioTop, SegPrioBottom, S.PriorityScreenTop);

        SliderPriority.Value = S.PriorityFactor;
        SliderQuality.Value = S.ImageQuality;
        SliderQos.Value = S.Qos;
        SliderHzQuality.Value = S.HzQuality;
        SliderHzCpu.Value = S.HzCpuLimit;

        SliderTopScale.Value = S.TopScale;
        SliderBottomScale.Value = S.BottomScale;

        TglScanStartup.IsChecked = S.ScanOnStartup;
        TglAutoConnect.IsChecked = S.AutoConnect;
        TglTryReconnect.IsChecked = S.TryReconnect;

        TxtListenPort.Text = S.ListenPort.ToString();
        CmbLayout.SelectedIndex = (int)S.Layout;
        CmbInterp.SelectedIndex = (int)S.Interpolation;
        CmbRotation.SelectedIndex = RotationToIndex(S.Rotation);
        TxtMaxFps.Text = S.MaxFps.ToString();
        TglAmbient.IsChecked = S.AmbientGlow;
        UpdateScreenshotLabel();

        RebuildChips();
        BuildPresetItems();
    }

    private static int RotationToIndex(int rot) => rot switch { 0 => 0, 90 => 1, 180 => 2, _ => 3 };
    private static int IndexToRotation(int idx) => idx switch { 0 => 0, 1 => 90, 2 => 180, _ => 270 };

    private void WireEvents()
    {
        foreach (var box in new[] { Oct1, Oct2, Oct3, Oct4 })
        {
            box.PreviewTextInput += Octet_PreviewInput;
            box.TextChanged += Octet_TextChanged;
        }

        SegNtr.Click += (_, _) => SetProtocol(Protocol.NTR, true);
        SegHz.Click += (_, _) => SetProtocol(Protocol.HzMod, true);
        SegPrioTop.Click += (_, _) => { SetSegment(SegPrioTop, SegPrioBottom, true); S.PriorityScreenTop = true; };
        SegPrioBottom.Click += (_, _) => { SetSegment(SegPrioTop, SegPrioBottom, false); S.PriorityScreenTop = false; };

        SliderPriority.ValueChanged += (_, _) => { S.PriorityFactor = (int)SliderPriority.Value; UpdateSliderLabels(); DetectPreset(); };
        SliderQuality.ValueChanged += (_, _) => { S.ImageQuality = (int)SliderQuality.Value; UpdateSliderLabels(); DetectPreset(); };
        SliderQos.ValueChanged += (_, _) => { S.Qos = (int)SliderQos.Value; UpdateSliderLabels(); DetectPreset(); };
        SliderHzQuality.ValueChanged += (_, _) => { S.HzQuality = (int)SliderHzQuality.Value; UpdateSliderLabels(); DetectPreset(); };
        SliderHzCpu.ValueChanged += (_, _) => { S.HzCpuLimit = (int)SliderHzCpu.Value; UpdateSliderLabels(); };
        SliderTopScale.ValueChanged += (_, _) => { S.TopScale = Math.Round(SliderTopScale.Value, 1); UpdateSliderLabels(); };
        SliderBottomScale.ValueChanged += (_, _) => { S.BottomScale = Math.Round(SliderBottomScale.Value, 1); UpdateSliderLabels(); };

        TxtListenPort.TextChanged += (_, _) => { if (int.TryParse(TxtListenPort.Text, out var p)) S.ListenPort = Math.Clamp(p, 1, 65535); };
        TxtMaxFps.TextChanged += (_, _) => { if (int.TryParse(TxtMaxFps.Text, out var f)) S.MaxFps = Math.Max(0, f); };

        CmbLayout.SelectionChanged += (_, _) => { if (_loaded) S.Layout = (ScreenLayout)CmbLayout.SelectedIndex; };
        CmbInterp.SelectionChanged += (_, _) => { if (_loaded) S.Interpolation = (Interpolation)CmbInterp.SelectedIndex; };
        CmbRotation.SelectionChanged += (_, _) => { if (_loaded) S.Rotation = IndexToRotation(CmbRotation.SelectedIndex); };
        CmbPreset.SelectionChanged += (_, _) => PresetSelected();
        TglAmbient.Click += (_, _) => S.AmbientGlow = TglAmbient.IsChecked == true;

        TglScanStartup.Click += (_, _) => { S.ScanOnStartup = TglScanStartup.IsChecked == true; App.Settings.Save(); };
        TglAutoConnect.Click += (_, _) => { S.AutoConnect = TglAutoConnect.IsChecked == true; App.Settings.Save(); };
        TglTryReconnect.Click += (_, _) => { S.TryReconnect = TglTryReconnect.IsChecked == true; App.Settings.Save(); };

        BtnBookmark.Click += (_, _) => ToggleBookmark();
        BtnRadar.Click += (_, _) => StartScan();
        BtnChooseFolder.Click += (_, _) => ChooseFolder();
        BtnKeyboard.Click += (_, _) => new ShortcutsWindow { Owner = _owner }.ShowDialog();
        BtnInfo.Click += (_, _) => ShowAbout();
        BtnConnect.Click += (_, _) => OnConnect();
        BtnCancel.Click += (_, _) => CancelConnect();
        BtnUpdateDismiss.Click += (_, _) => UpdateBanner.Visibility = Visibility.Collapsed;
    }

    // ===================== Octets =====================

    private void Octet_PreviewInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !Regex.IsMatch(e.Text, "^[0-9]$");

    private void Octet_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (int.TryParse(tb.Text, out var v) && v > 255)
        {
            tb.Text = "255";
            tb.CaretIndex = tb.Text.Length;
        }
        PersistIp();

        if (tb.Text.Length >= 3)
        {
            var next = tb == Oct1 ? Oct2 : tb == Oct2 ? Oct3 : tb == Oct3 ? Oct4 : null;
            next?.Focus();
            next?.SelectAll();
        }
        RefreshChipHighlight();
        UpdateRadarColor();
    }

    private void PersistIp()
    {
        S.Ip1 = Oct1.Text; S.Ip2 = Oct2.Text; S.Ip3 = Oct3.Text; S.Ip4 = Oct4.Text;
    }

    private string CurrentIp => $"{Norm(Oct1.Text)}.{Norm(Oct2.Text)}.{Norm(Oct3.Text)}.{Norm(Oct4.Text)}";
    private static string Norm(string s) => string.IsNullOrWhiteSpace(s) ? "0" : s;

    private void FillIp(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return;
        Oct1.Text = parts[0]; Oct2.Text = parts[1]; Oct3.Text = parts[2]; Oct4.Text = parts[3];
        PersistIp();
        RefreshChipHighlight();
    }

    // ===================== Protocol / segments =====================

    private void SetProtocol(Protocol p, bool persist)
    {
        SetSegment(SegNtr, SegHz, p == Protocol.NTR);
        PanelNtr.Visibility = p == Protocol.NTR ? Visibility.Visible : Visibility.Collapsed;
        PanelHz.Visibility = p == Protocol.HzMod ? Visibility.Visible : Visibility.Collapsed;
        if (persist) S.Protocol = p;
        if (_loaded) UpdatePresetLabel();
    }

    private static void SetSegment(ToggleButton a, ToggleButton b, bool first)
    {
        a.IsChecked = first;
        b.IsChecked = !first;
    }

    private void UpdateSliderLabels()
    {
        ValPriority.Text = ((int)SliderPriority.Value).ToString();
        ValQuality.Text = ((int)SliderQuality.Value).ToString();
        ValQos.Text = ((int)SliderQos.Value).ToString();
        ValHzQuality.Text = ((int)SliderHzQuality.Value).ToString();
        ValHzCpu.Text = ((int)SliderHzCpu.Value).ToString();
        ValTopScale.Text = $"{SliderTopScale.Value:0.0}×";
        ValBottomScale.Text = $"{SliderBottomScale.Value:0.0}×";
    }

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

        CmbPreset.Items.Add(new Separator());
        CmbPreset.Items.Add(MakePresetItem("＋  Add custom preset…", TagAdd));
        if (S.CustomPresets.Count > 0)
            CmbPreset.Items.Add(MakePresetItem("🗑  Delete custom preset…", TagDelete));

        _suppressPreset = false;
        UpdatePresetLabel();
    }

    private static ComboBoxItem MakePresetItem(string text, object tag)
        => new() { Content = text, Tag = tag };

    private void PresetSelected()
    {
        if (_suppressPreset) return;
        if (CmbPreset.SelectedItem is not ComboBoxItem item) return;

        switch (item.Tag)
        {
            case QualityPreset p:
                ApplyPreset(p);
                break;
            case string s when s == TagCustom:
                break; // no-op label
            case string s when s == TagAdd:
                AddCustomPreset();
                break;
            case string s when s == TagDelete:
                DeleteCustomPreset();
                break;
        }
    }

    private void ApplyPreset(QualityPreset p)
    {
        _applyingPreset = true;
        SliderPriority.Value = p.Factor;
        SliderQuality.Value = p.Quality;
        SliderQos.Value = p.Qos;
        SliderHzQuality.Value = p.Quality;   // HzMod preset = quality only
        _applyingPreset = false;
        UpdatePresetLabel();
    }

    private void AddCustomPreset()
    {
        var name = Dialogs.PromptText(_owner, "Add custom preset", "Preset name:", "My preset");
        if (!string.IsNullOrWhiteSpace(name))
        {
            S.CustomPresets.RemoveAll(x => x.Name == name);
            S.CustomPresets.Add(new QualityPreset(name.Trim(), S.PriorityFactor, S.ImageQuality, S.Qos));
            App.Settings.Save();
            BuildPresetItems();
        }
        else UpdatePresetLabel();
    }

    private void DeleteCustomPreset()
    {
        if (S.CustomPresets.Count == 0) { UpdatePresetLabel(); return; }
        var pick = Dialogs.ChooseFromList(_owner, "Delete custom preset",
            S.CustomPresets.Select(p => p.Name).ToList());
        if (pick != null)
        {
            S.CustomPresets.RemoveAll(x => x.Name == pick);
            App.Settings.Save();
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
        if (CmbPreset.Items.Count == 0) return;
        var match = FindMatchingPreset();
        _suppressPreset = true;
        ComboBoxItem? target = null;
        foreach (var obj in CmbPreset.Items)
        {
            if (obj is ComboBoxItem ci)
            {
                if (match != null && ReferenceEquals(ci.Tag, match)) { target = ci; break; }
                if (match == null && (ci.Tag as string) == TagCustom) { target = ci; break; }
            }
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

    // ===================== Updates =====================

    private async Task MaybeCheckForUpdatesAsync()
    {
        if (!S.CheckUpdatesOnStartup) return;
        var info = await UpdateChecker.CheckAsync();
        if (info is { Available: true })
            Dispatcher.Invoke(() => ShowUpdateBanner(info));
    }

    private void ShowUpdateBanner(UpdateInfo info)
    {
        UpdateBannerText.Text = $"Update available — v{info.LatestVersion}";
        UpdateBanner.Visibility = Visibility.Visible;
        BtnUpdateDownload.Click -= OpenReleaseHandler;
        _pendingUpdateUrl = info.Url;
        BtnUpdateDownload.Click += OpenReleaseHandler;
    }

    private string _pendingUpdateUrl = AppInfo.ReleasesUrl;
    private void OpenReleaseHandler(object? s, RoutedEventArgs e) => OpenUrl(_pendingUpdateUrl);

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ===================== Saved IP chips =====================

    private void RebuildChips()
    {
        ChipsPanel.Items.Clear();
        foreach (var ip in S.SavedIps)
            ChipsPanel.Items.Add(BuildChip(ip));
        RefreshChipHighlight();
    }

    private Border BuildChip(string ip)
    {
        bool current = ip == CurrentIp;
        var text = new TextBlock
        {
            Text = ip,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White
        };
        var close = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = current ? Brushes.White : (Brush)FindResource("TextMutedBrush"),
            Cursor = Cursors.Hand
        };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveSavedIp(ip); };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(text);
        sp.Children.Add(close);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Background = current ? (Brush)FindResource("BrandBrush") : (Brush)FindResource("FieldBackgroundBrush"),
            BorderBrush = (Brush)FindResource("FieldBorderBrush"),
            BorderThickness = new Thickness(current ? 0 : 1),
            Child = sp,
            Tag = ip
        };
        chip.MouseLeftButtonUp += (_, _) => FillIp(ip);
        return chip;
    }

    private void RefreshChipHighlight()
    {
        foreach (var item in ChipsPanel.Items)
        {
            if (item is Border chip && chip.Tag is string ip)
            {
                bool current = ip == CurrentIp;
                chip.Background = current ? (Brush)FindResource("BrandBrush") : (Brush)FindResource("FieldBackgroundBrush");
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
        RebuildChips();
    }

    private void RemoveSavedIp(string ip)
    {
        S.SavedIps.Remove(ip);
        RebuildChips();
    }

    private void UpdateBookmarkColor()
    {
        bool saved = S.SavedIps.Contains(CurrentIp);
        BookmarkIcon.Fill = saved ? (Brush)FindResource("BrandEndBrush") : (Brush)FindResource("TextMutedBrush");
    }

    private void UpdateRadarColor()
    {
        bool found = _foundIps.Contains(CurrentIp);
        var color = found ? Brushes.LimeGreen : (Brush)FindResource("BrandEndBrush");
        foreach (var child in RadarIcon.Children)
        {
            if (child is Path p) { p.Stroke = color; p.Fill = color; }
            if (child is Ellipse el) el.Fill = color;
        }
    }

    // ===================== Network scan =====================

    private async void StartScan()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        int gen = ++_scanGen;               // any earlier scan is now stale
        _foundIps.Clear();
        _autoConnected = false;
        TxtScanStatus.Text = "Scanning…";
        UpdateRadarColor();

        bool hz = S.Protocol == Protocol.HzMod;
        var token = _scanCts.Token;
        try
        {
            await NetworkScanner.ScanAsync(hz, token, ip =>
                Dispatcher.Invoke(() => { if (gen == _scanGen) AddFound(ip); }));
        }
        catch { }

        if (gen == _scanGen && _foundIps.Count == 0)   // don't clobber a newer scan's result
            TxtScanStatus.Text = "No device found";
    }

    private void AddFound(string ip)
    {
        if (_foundIps.Contains(ip)) return;
        _foundIps.Add(ip);

        // Show the found device inline and fill the address boxes so Connect is ready.
        string first = _foundIps[0];
        TxtScanStatus.Text = _foundIps.Count == 1 ? $"Found {first}" : $"Found {first}  (+{_foundIps.Count - 1})";
        FillIp(first);
        UpdateRadarColor();

        // AutoConnect: connect to the first 3DS the scan finds (once).
        if (S.AutoConnect && !_autoConnected)
        {
            _autoConnected = true;
            OnConnect();
        }
    }

    // ===================== Folder / dialogs =====================

    private void ChooseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder for screenshots",
            InitialDirectory = System.IO.Directory.Exists(S.ScreenshotFolder) ? S.ScreenshotFolder : AppSettings.DefaultScreenshotFolder
        };
        if (dlg.ShowDialog(_owner) == true)
        {
            S.ScreenshotFolder = dlg.FolderName;
            UpdateScreenshotLabel();
        }
    }

    private void UpdateScreenshotLabel()
        => TxtScreenshotFolder.Text = System.IO.Path.GetFileName(S.ScreenshotFolder.TrimEnd('\\')) is { Length: > 0 } n
            ? n : S.ScreenshotFolder;

    private void ShowAbout()
    {
        var about = new AboutWindow(async () =>
        {
            var info = await UpdateChecker.CheckAsync();
            if (info == null) return "Could not check (offline?).";
            if (info.Available)
            {
                Dispatcher.Invoke(() => ShowUpdateBanner(info));
                return $"Update available — v{info.LatestVersion}";
            }
            return "You're on the latest version.";
        })
        { Owner = _owner };
        about.ShowDialog();
    }

    // ===================== Connect / watchdog =====================

    private void OnConnect()
    {
        if (!IPAddress.TryParse(CurrentIp, out _))
        {
            SetStatus("Invalid IP address", StatusKind.Error);
            return;
        }
        if (!int.TryParse(TxtListenPort.Text, out var port) || port is < 1 or > 65535)
        {
            SetStatus("Invalid listen port", StatusKind.Error);
            return;
        }

        var ip = CurrentIp;
        S.RememberIp(ip);
        RebuildChips();
        App.Settings.Save();

        IStreamClient client = S.Protocol == Protocol.NTR
            ? new NTRClient(ip, port, S.ImageQuality, S.PriorityFactor, S.PriorityScreenTop, S.Qos)
            : new HzModClient(ip, S.HzQuality, S.HzCpuLimit);

        _connectingClient = client;
        SetConnectingUi(true);
        SetStatus("Connecting… (1/3)", StatusKind.Connecting);

        client.Status += msg => Dispatcher.Invoke(() => SetStatus(msg, StatusKind.Connecting));
        client.FirstFrame += () => Dispatcher.Invoke(() =>
        {
            var c = _connectingClient;
            _connectingClient = null;
            _reconnectPending = false;
            SetConnectingUi(false);
            if (c != null) _owner.ShowStream(c, S.Protocol);
        });
        client.Failed += msg => Dispatcher.Invoke(() =>
        {
            CleanupConnecting();
            SetStatus(msg, StatusKind.Error);
            if (S.TryReconnect) ScheduleReconnect();   // keep retrying until it connects / Cancel
        });

        client.Start();
    }

    /// <summary>Try Reconnect loop: after a delay, attempt to connect again to the current IP.</summary>
    private void ScheduleReconnect()
    {
        if (!S.TryReconnect) return;
        _reconnectPending = true;
        _reconnectTimer?.Stop();
        SetStatus("Reconnecting…", StatusKind.Connecting);
        SetConnectingUi(true);                          // show Cancel so the user can stop the loop
        _reconnectTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reconnectTimer.Tick += (_, _) =>
        {
            _reconnectTimer?.Stop();
            if (_reconnectPending && _connectingClient == null) OnConnect();
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
        try { _connectingClient?.Stop(); } catch { }
        _connectingClient?.Dispose();
        _connectingClient = null;
        SetConnectingUi(false);
    }

    private void SetConnectingUi(bool connecting)
    {
        ConnectSpinner.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
        BtnCancel.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
        BtnConnect.IsEnabled = !connecting;
        ConnectLabel.Text = connecting ? "Connecting…" : "Connect";
    }

    private enum StatusKind { Idle, Connecting, Streaming, Error }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusText.Foreground = kind == StatusKind.Error ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("TextSecondaryBrush");
        StatusDot.Fill = kind switch
        {
            StatusKind.Streaming => Brushes.LimeGreen,
            StatusKind.Connecting => Brushes.Orange,
            StatusKind.Error => (Brush)FindResource("DangerBrush"),
            _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
        };
    }
}
