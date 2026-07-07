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

    /// <summary>One row per streambar control. Two-key rows (prev/next, decrease/increase pairs) show
    /// both bindings side by side instead of as two separate rows.</summary>
    private sealed record Row(string Label, params ShortcutAction[] Actions);

    private static readonly Row[] Rows =
    {
        new("Cycle layout",           ShortcutAction.CycleLayout),
        new("Cycle filter",           ShortcutAction.CycleFilter),
        new("Rotate screen",          ShortcutAction.RotateScreen),
        new("Cycle upscale",          ShortcutAction.CycleUpscalePrev, ShortcutAction.CycleUpscaleNext),
        new("Cycle effect",           ShortcutAction.CycleEffectPrev, ShortcutAction.CycleEffectNext),
        new("Effect intensity",       ShortcutAction.DecreaseEffectIntensity, ShortcutAction.IncreaseEffectIntensity),
        new("Cycle zoom",             ShortcutAction.CycleZoom),
        new("Cycle UI size",          ShortcutAction.CycleUiSize),
        new("Toggle adjust panels",   ShortcutAction.ToggleAdjustPanels),
        new("Toggle ambient glow",    ShortcutAction.ToggleAmbientGlow),
        new("Toggle pin FPS",         ShortcutAction.TogglePinFps),
        new("Screenshot",             ShortcutAction.Screenshot),
        new("Screenshot to clipboard", ShortcutAction.ScreenshotToClipboard),
        new("Copy text (OCR)",        ShortcutAction.CopyText),
        new("Toggle fullscreen",      ShortcutAction.ToggleFullscreen),
        new("Hide interface",         ShortcutAction.ToggleUi),
        new("Toggle hide bar",        ShortcutAction.ToggleHideBar),
        new("Swap priority screen",   ShortcutAction.SwapPriorityScreen),
        new("Cycle FPS cap",          ShortcutAction.CycleFpsCap),
        new("Quality",                ShortcutAction.DecreaseQuality, ShortcutAction.IncreaseQuality),
        new("Disconnect",             ShortcutAction.Disconnect),
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
        foreach (var row in Rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

            var name = new TextBlock
            {
                Text = row.Label,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("TextPrimaryBrush")
            };
            Grid.SetColumn(name, 0);

            bool paired = row.Actions.Length > 1;
            var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < row.Actions.Length; i++)
            {
                if (i > 0)
                    keys.Children.Add(new TextBlock
                    {
                        Text = "/",
                        FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brush("TextSecondaryBrush")
                    });

                var action = row.Actions[i];
                var btn = new Button
                {
                    Classes = { "ghost" },
                    MinWidth = paired ? 68 : 128,
                    Content = DisplayKey(action),
                    FontWeight = FontWeight.SemiBold,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Tag = action
                };
                btn.Click += (_, _) => BeginRecording(action, btn);
                _buttons[action] = btn;
                keys.Children.Add(btn);
            }
            Grid.SetColumn(keys, 1);

            var rowBorder = new Border
            {
                Background = Brush("FieldBackgroundBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 7),
                Margin = new Thickness(4, 3),
                Child = grid
            };
            grid.Children.Add(name);
            grid.Children.Add(keys);
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
