using System.Windows;
using System.Windows.Controls;
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
