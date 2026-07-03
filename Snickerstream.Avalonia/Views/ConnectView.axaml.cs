using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
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

    public ConnectView() : this(null!) { }   // designer

    public ConnectView(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        LoadFromSettings();
        WireEvents();
        _loaded = true;
    }

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

        BindSlider(SldPriority, v => { S.PriorityFactor = (int)v; });
        BindSlider(SldQuality, v => { S.ImageQuality = (int)v; });
        BindSlider(SldQos, v => { S.Qos = (int)v; });
        BindSlider(SldHzQuality, v => { S.HzQuality = (int)v; });
        BindSlider(SldHzCpu, v => { S.HzCpuLimit = (int)v; });

        BtnConnect.Click += (_, _) => OnConnect();
        BtnCancel.Click += (_, _) => CancelConnect();
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
    }

    private void ApplyPriority(bool top)
    {
        Highlight(BtnPrioTop, top);
        Highlight(BtnPrioBottom, !top);
    }

    private static void Highlight(Button b, bool on)
        => b.Foreground = on ? Brushes.White : new SolidColorBrush(Color.Parse("#80FFFFFF"));

    private string CurrentIp => $"{Norm(Oct1.Text)}.{Norm(Oct2.Text)}.{Norm(Oct3.Text)}.{Norm(Oct4.Text)}";
    private static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? "0" : s;

    // ===================== Connect =====================

    private void OnConnect()
    {
        if (!IPAddress.TryParse(CurrentIp, out _)) { SetStatus("Invalid IP address", StatusKind.Error); return; }
        if (!int.TryParse(TxtPort.Text, out var port) || port is < 1 or > 65535) { SetStatus("Invalid listen port", StatusKind.Error); return; }

        var ip = CurrentIp;
        S.Save();

        IStreamClient client = S.Protocol == Protocol.NTR
            ? new NTRClient(ip, port, S.ImageQuality, S.PriorityFactor, S.PriorityScreenTop, S.Qos)
            : new HzModClient(ip, S.HzQuality, S.HzCpuLimit);

        _connecting = client;
        SetConnectingUi(true);
        SetStatus("Connecting… (1/3)", StatusKind.Connecting);

        client.Status += msg => Dispatcher.UIThread.Post(() => SetStatus(msg, StatusKind.Connecting));
        client.FirstFrame += () => Dispatcher.UIThread.Post(() =>
        {
            var c = _connecting;
            _connecting = null;
            SetConnectingUi(false);
            if (c != null) _owner.ShowStream(c, S.Protocol);
        });
        client.Failed += msg => Dispatcher.UIThread.Post(() =>
        {
            CleanupConnecting();
            SetStatus(msg, StatusKind.Error);
        });

        client.Start();
    }

    private void CancelConnect()
    {
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
