using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Snickerstream4Win.Views;

/// <summary>Small code-built modal dialogs (text prompt, list picker) styled to the dark theme.</summary>
public static class Dialogs
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x20));

    public static string? PromptText(Window owner, string title, string prompt, string initial = "")
    {
        var win = MakeWindow(owner, title, 380);
        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            Foreground = Brushes.White,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var box = new TextBox
        {
            Text = initial,
            Style = (Style)Application.Current.FindResource(typeof(TextBox)),
            MinHeight = 40,
            Padding = new Thickness(10, 8, 10, 8),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(box);

        string? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = MakeButton("Cancel", "GhostButton");
        var ok = MakeButton("OK", "AccentButton");
        cancel.Click += (_, _) => { win.Close(); };
        ok.Click += (_, _) => { result = box.Text; win.Close(); };
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        win.Content = Wrap(panel);
        box.Focus();
        box.SelectAll();
        win.ShowDialog();
        return result;
    }

    public static string? ChooseFromList(Window owner, string title, IReadOnlyList<string> items)
    {
        var win = MakeWindow(owner, title, 380);
        var panel = new StackPanel { Margin = new Thickness(20) };

        var list = new ListBox
        {
            MaxHeight = 260,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White
        };
        foreach (var it in items) list.Items.Add(it);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        panel.Children.Add(list);

        string? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = MakeButton("Cancel", "GhostButton");
        var del = MakeButton("Delete", "DangerButton");
        cancel.Click += (_, _) => win.Close();
        del.Click += (_, _) => { result = list.SelectedItem as string; win.Close(); };
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(del);
        panel.Children.Add(buttons);

        win.Content = Wrap(panel);
        win.ShowDialog();
        return result;
    }

    /// <summary>
    /// Shows recognized OCR text in an editable box (auto-copied to the clipboard) with a "Hex mode"
    /// toggle that re-recognizes the same capture via <paramref name="reRun"/> (returns the new text).
    /// </summary>
    public static void ShowOcrResult(Window owner, string text, bool hexMode, Func<bool, Task<string?>> reRun)
    {
        var win = MakeWindow(owner, "Copied text", 460);
        // Open at the bottom-right of the window (above the control bar) so the top screen — where the
        // HUD/values are — stays visible for comparison, instead of centered over it.
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Loaded += (_, _) =>
        {
            double w = win.ActualWidth, h = win.ActualHeight;
            var wa = SystemParameters.WorkArea;
            double left = owner.Left + owner.ActualWidth - w - 48;
            double top = owner.Top + owner.ActualHeight - h - 100;   // clear the control bar
            win.Left = Math.Max(wa.Left, Math.Min(left, wa.Left + wa.Width - w));
            win.Top = Math.Max(wa.Top, Math.Min(top, wa.Top + wa.Height - h));
        };
        // Drag the window from anywhere that isn't the text box / buttons / toggle.
        win.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
            win.DragMove();
        };
        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = "Recognized text — copied to the clipboard:",
            Foreground = Brushes.White,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var box = new TextBox
        {
            Text = text,
            Style = (Style)Application.Current.FindResource(typeof(TextBox)),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10, 8, 10, 8),
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono, monospace")
        };
        panel.Children.Add(box);

        try { Clipboard.SetText(text); } catch { }

        // Hex-mode toggle: re-runs OCR on the same capture (Tesseract, hex-whitelisted).
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var hexToggle = new CheckBox { IsChecked = hexMode, VerticalAlignment = VerticalAlignment.Center };
        try { hexToggle.Style = (Style)Application.Current.FindResource("ToggleSwitch"); } catch { }
        hexRow.Children.Add(hexToggle);
        hexRow.Children.Add(new TextBlock
        {
            Text = "Hex mode  (seeds / RNG — restricts to 0-9 A-F)",
            Foreground = Brushes.White,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(hexRow);

        async void Rerun()
        {
            var prev = box.Text;
            box.IsEnabled = false;
            box.Text = "Reading…";
            try
            {
                var t = (await reRun(hexToggle.IsChecked == true)) ?? "";
                t = t.Trim();
                box.Text = t.Length == 0 ? prev : t;
                if (t.Length > 0) { try { Clipboard.SetText(t); } catch { } }
            }
            catch { box.Text = prev; }
            box.IsEnabled = true;
        }
        hexToggle.Click += (_, _) => Rerun();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var copy = MakeButton("Copy", "GhostButton");
        var close = MakeButton("Close", "AccentButton");
        copy.Click += (_, _) => { try { Clipboard.SetText(box.Text); } catch { } };
        close.Click += (_, _) => win.Close();
        close.IsCancel = true;                 // Esc closes
        copy.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);

        win.Content = Wrap(panel);
        box.Focus();
        box.SelectAll();
        win.ShowDialog();
    }

    /// <summary>True if the clicked element is (inside) a text box, button or thumb — where a drag would
    /// interfere with selecting/clicking, so we don't start a window move there.</summary>
    private static bool IsInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is TextBoxBase or ButtonBase or Thumb) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
    }

    private static Window MakeWindow(Window owner, string title, double width) => new()
    {
        Title = title,
        Owner = owner,
        Width = width,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        ResizeMode = ResizeMode.NoResize
    };

    private static Border Wrap(UIElement child) => new()
    {
        Background = Bg,
        CornerRadius = new CornerRadius(14),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Child = child,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.5 }
    };

    private static Button MakeButton(string content, string styleKey) => new()
    {
        Content = content,
        Style = (Style)Application.Current.FindResource(styleKey),
        MinWidth = 84
    };
}
