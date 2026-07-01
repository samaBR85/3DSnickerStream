using System.Windows.Input;

namespace Snickerstream4Win.Models;

/// <summary>
/// Parses, formats and matches keyboard shortcut strings such as "S", "Shift+S",
/// "Escape", "F11", "Up". Matching is modifier-exact so "S" and "Shift+S" never collide.
/// </summary>
public static class ShortcutBinding
{
    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string binding, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(binding)) return false;

        foreach (var raw in binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": case "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        return key != Key.None;
    }

    public static bool Matches(string binding, Key key, ModifierKeys current)
        => TryParse(binding, out var mods, out var k) && k == key && mods == current;

    public static string Pretty(string binding)
    {
        if (!TryParse(binding, out var mods, out var key)) return binding;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(PrettyKey(key));
        return string.Join("+", parts);
    }

    private static string PrettyKey(Key key) => key switch
    {
        Key.Escape => "Esc",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        _ => key.ToString()
    };
}
