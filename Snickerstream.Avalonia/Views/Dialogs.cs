using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SnickerstreamV2.Views;

/// <summary>Small themed modal dialogs (chromeless dark card), built in code to match the app.</summary>
public static class Dialogs
{
    public static async Task<string?> PromptText(Window owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Width = 300 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = label, Foreground = Brush("TextSecondaryBrush") });
        content.Children.Add(box);

        var ok = await ShowModal(owner, title, content, () => box.Focus());
        var t = box.Text?.Trim();
        return ok && !string.IsNullOrWhiteSpace(t) ? t : null;
    }

    public static async Task<string?> ChooseFromList(Window owner, string title, IList<string> items)
    {
        var list = new ListBox { ItemsSource = items, Height = 220, MinWidth = 300 };
        if (items.Count > 0) list.SelectedIndex = 0;

        var ok = await ShowModal(owner, title, list, () => list.Focus());
        return ok ? list.SelectedItem as string : null;
    }

    private static async Task<bool> ShowModal(Window owner, string title, Control content, Action? onOpened)
    {
        bool result = false;

        var ok = new Button { Content = "OK", MinWidth = 84, IsDefault = true, Classes = { "brand" } };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, IsCancel = true, Classes = { "ghost" } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brush("TextPrimaryBrush") });
        root.Children.Add(content);
        root.Children.Add(buttons);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF181820")),
            BorderBrush = Brush("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22),
            Margin = new Thickness(16),
            Child = root
        };

        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Content = card
        };
        ok.Click += (_, _) => { result = true; win.Close(); };
        cancel.Click += (_, _) => { result = false; win.Close(); };
        if (onOpened != null) win.Opened += (_, _) => onOpened();

        await win.ShowDialog(owner);
        return result;
    }

    private static IBrush Brush(string key)
        => Application.Current!.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Transparent;
}
