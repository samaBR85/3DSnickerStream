using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Snickerstream4Win.Models;

namespace Snickerstream4Win.Views;

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
    };

    public ShortcutsWindow()
    {
        InitializeComponent();
        BuildRows();
        BtnReset.Click += (_, _) => { S.KeyBindings = AppSettings.DefaultKeyBindings(); RefreshLabels(); };
        BtnDone.Click += (_, _) => { App.Settings.Save(); Close(); };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void BuildRows()
    {
        foreach (var (action, label) in Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = Glyph(action),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondaryBrush")
            };
            Grid.SetColumn(icon, 0);

            var name = new TextBlock
            {
                Text = label,
                FontSize = 14,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            };
            Grid.SetColumn(name, 1);

            var btn = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                MinWidth = 130,
                Content = DisplayKey(action),
                FontWeight = FontWeights.SemiBold,
                Tag = action
            };
            btn.Click += (_, _) => BeginRecording(action, btn);
            Grid.SetColumn(btn, 2);
            _buttons[action] = btn;

            var rowBorder = new Border
            {
                Background = (Brush)FindResource("FieldBackgroundBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 4, 0, 4)
            };
            grid.Children.Add(icon);
            grid.Children.Add(name);
            grid.Children.Add(btn);
            rowBorder.Child = grid;
            RowsPanel.Children.Add(rowBorder);
        }
    }

    private void BeginRecording(ShortcutAction action, Button btn)
    {
        _recording = btn;
        btn.Content = "Press a key…";
        btn.Tag = action;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording == null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        e.Handled = true;
        if (_recording.Tag is ShortcutAction action)
        {
            // Esc with no modifiers cancels recording (unless we're rebinding Disconnect).
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && action != ShortcutAction.Disconnect)
            {
                _recording.Content = DisplayKey(action);
                _recording = null;
                return;
            }
            S.KeyBindings[action.ToString()] = ShortcutBinding.Format(Keyboard.Modifiers, key);
        }
        _recording = null;
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        foreach (var (action, btn) in _buttons) btn.Content = DisplayKey(action);
    }

    private static string Glyph(ShortcutAction action) => action switch
    {
        ShortcutAction.Screenshot => "",            // Camera
        ShortcutAction.ScreenshotToClipboard => "", // Copy
        ShortcutAction.Disconnect => "",            // Block/Stop
        ShortcutAction.CycleLayout => "",           // ViewAll
        ShortcutAction.CycleFilter => "",           // Filter
        ShortcutAction.RotateScreen => "",          // Rotate
        ShortcutAction.ToggleFullscreen => "",      // FullScreen
        ShortcutAction.IncreaseQuality => "",       // Up
        ShortcutAction.DecreaseQuality => "",       // Down
        ShortcutAction.SwapPriorityScreen => "",    // Switch
        ShortcutAction.ToggleUi => "",       // Hide (eye)
        _ => ""
    };

    private string DisplayKey(ShortcutAction action)
    {
        if (!S.KeyBindings.TryGetValue(action.ToString(), out var k) || string.IsNullOrEmpty(k))
            k = AppSettings.DefaultKeyBindings().GetValueOrDefault(action.ToString(), "");
        return ShortcutBinding.Pretty(k);
    }
}
