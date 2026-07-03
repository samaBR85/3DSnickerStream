using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SnickerstreamV2.Models;

namespace SnickerstreamV2.Views;

public partial class ShortcutsWindow : Window
{
    private AppSettings S => App.Settings;
    private Button? _recording;
    private readonly Dictionary<ShortcutAction, Button> _buttons = new();

    private static readonly (ShortcutAction action, string label)[] Rows =
    {
        (ShortcutAction.Screenshot,            "Screenshot"),
        (ShortcutAction.ScreenshotToClipboard, "Screenshot to clipboard"),
        (ShortcutAction.Disconnect,            "Disconnect"),
        (ShortcutAction.CycleLayout,           "Cycle layout"),
        (ShortcutAction.CycleFilter,           "Cycle filter"),
        (ShortcutAction.RotateScreen,          "Rotate screen"),
        (ShortcutAction.ToggleFullscreen,      "Toggle fullscreen"),
        (ShortcutAction.IncreaseQuality,       "Increase quality"),
        (ShortcutAction.DecreaseQuality,       "Decrease quality"),
        (ShortcutAction.SwapPriorityScreen,    "Swap priority screen"),
        (ShortcutAction.ToggleUi,              "Hide interface"),
        (ShortcutAction.CopyText,              "Copy text (OCR)"),
    };

    public ShortcutsWindow()
    {
        InitializeComponent();
        BuildRows();
        BtnReset.Click += (_, _) => { S.KeyBindings = AppSettings.DefaultKeyBindings(); RefreshLabels(); };
        BtnDone.Click += (_, _) => { S.Save(); Close(); };
        AddHandler(KeyDownEvent, OnKeyDownRec, RoutingStrategies.Tunnel);
    }

    private static IBrush Brush(string key)
        => Application.Current!.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Transparent;

    private void BuildRows()
    {
        foreach (var (action, label) in Rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

            var name = new TextBlock
            {
                Text = label,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("TextPrimaryBrush")
            };
            Grid.SetColumn(name, 0);

            var btn = new Button
            {
                Classes = { "ghost" },
                MinWidth = 128,
                Content = DisplayKey(action),
                FontWeight = FontWeight.SemiBold,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Tag = action
            };
            btn.Click += (_, _) => BeginRecording(action, btn);
            Grid.SetColumn(btn, 1);
            _buttons[action] = btn;

            var rowBorder = new Border
            {
                Background = Brush("FieldBackgroundBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 7),
                Margin = new Thickness(4, 3),
                Child = grid
            };
            grid.Children.Add(name);
            grid.Children.Add(btn);
            RowsPanel.Children.Add(rowBorder);
        }
    }

    private void BeginRecording(ShortcutAction action, Button btn)
    {
        _recording = btn;
        btn.Content = "Press a key…";
        btn.Tag = action;
    }

    private void OnKeyDownRec(object? sender, KeyEventArgs e)
    {
        if (_recording == null) return;
        var key = e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        e.Handled = true;
        if (_recording.Tag is ShortcutAction action)
        {
            // Esc with no modifiers cancels recording (unless we're rebinding Disconnect).
            if (key == Key.Escape && e.KeyModifiers == KeyModifiers.None && action != ShortcutAction.Disconnect)
            {
                _recording.Content = DisplayKey(action);
                _recording = null;
                return;
            }
            S.KeyBindings[action.ToString()] = ShortcutBinding.Format(e.KeyModifiers, key);
        }
        _recording = null;
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        foreach (var (action, btn) in _buttons) btn.Content = DisplayKey(action);
    }

    private string DisplayKey(ShortcutAction action)
    {
        if (!S.KeyBindings.TryGetValue(action.ToString(), out var k) || string.IsNullOrEmpty(k))
            k = AppSettings.DefaultKeyBindings().GetValueOrDefault(action.ToString(), "");
        return ShortcutBinding.Pretty(k);
    }
}
