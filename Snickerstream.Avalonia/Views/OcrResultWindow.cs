using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SnickerstreamV2.Views;

/// <summary>
/// Non-modal result popup for "copy text from the stream": editable text (auto-copied), a Hex-mode
/// toggle that re-runs OCR, and a Copy/Close row. Chromeless, draggable, opens bottom-right of the owner.
/// </summary>
public static class OcrResultWindow
{
    public static void Show(Window owner, string text, bool hexMode, Func<bool, Task<string?>> reRun)
    {
        var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;

        var header = new TextBlock
        {
            Text = "Copied text", FontSize = 14, FontWeight = FontWeight.Bold,
            Foreground = Brush("TextPrimaryBrush")
        };

        var box = new TextBox
        {
            Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            // Size / heights are pre-divided by the 0.8 window scale so the field renders at full size.
            // MinHeight ≈ 10 lines of the 13px-rendered font; taller text scrolls up to MaxHeight.
            Width = 360, MinHeight = 225, MaxHeight = 300,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), FontSize = 16
        };

        var hexToggle = new CheckBox { Content = "Hex mode", IsChecked = hexMode, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { Text = "Copied to clipboard", FontSize = 11, Foreground = Brush("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        var optRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(hexToggle, 0);
        Grid.SetColumn(status, 1);
        status.Margin = new Thickness(14, 0, 0, 0);
        optRow.Children.Add(hexToggle);
        optRow.Children.Add(status);

        var copy = new Button { Content = "Copy", MinWidth = 72, Classes = { "brand" } };
        var close = new Button { Content = "Close", MinWidth = 72, Classes = { "ghost" } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(close);
        buttons.Children.Add(copy);

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(header);
        root.Children.Add(box);
        root.Children.Add(optRow);
        root.Children.Add(buttons);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF181820")),
            BorderBrush = Brush("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18),
            Margin = new Thickness(14),
            Child = root
        };

        var win = new Window
        {
            Title = "Copied text",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            // Scale the whole popup to 80% (the text field's heights above compensate so it stays full).
            Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(0.8, 0.8), Child = card }
        };

        clipboard?.SetTextAsync(text);

        hexToggle.IsCheckedChanged += async (_, _) =>
        {
            bool hx = hexToggle.IsChecked == true;
            status.Text = "Reading…";
            var res = await reRun(hx);
            if (res != null)
            {
                box.Text = res;
                clipboard?.SetTextAsync(res);
                status.Text = "Copied to clipboard";
            }
            else status.Text = "OCR unavailable";
        };
        copy.Click += (_, _) => { clipboard?.SetTextAsync(box.Text ?? ""); status.Text = "Copied to clipboard"; };
        close.Click += (_, _) => win.Close();

        // Drag the popup from anywhere except the interactive controls (textbox / buttons / toggle).
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed && !IsInteractive(e.Source as Visual))
                win.BeginMoveDrag(e);
        };

        // Open bottom-right of the owner (best effort; it's draggable).
        win.Opened += (_, _) =>
        {
            try
            {
                double s = owner.RenderScaling;
                int x = owner.Position.X + (int)((owner.ClientSize.Width - win.ClientSize.Width - 28) * s);
                int y = owner.Position.Y + (int)((owner.ClientSize.Height - win.ClientSize.Height - 96) * s);
                win.Position = new PixelPoint(x, y);
            }
            catch { /* leave at default position */ }
        };

        win.Show(owner);
    }

    /// <summary>True if the point is over an editable/clickable control (so dragging there is suppressed).</summary>
    private static bool IsInteractive(Visual? v)
    {
        while (v != null)
        {
            if (v is TextBox or Button or CheckBox) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private static IBrush Brush(string key)
        => Application.Current!.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Transparent;
}
