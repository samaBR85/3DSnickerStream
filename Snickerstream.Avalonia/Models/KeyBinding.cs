using Avalonia.Input;

namespace SnickerstreamV2.Models;

/// <summary>
/// Parses, formats and matches keyboard shortcut strings such as "S", "Shift+S",
/// "Escape", "F11", "Up". Matching is modifier-exact so "S" and "Shift+S" never collide.
/// <para>Avalonia port of the WPF version: uses <see cref="KeyModifiers"/> (Meta == the Windows/Command
/// key) and <see cref="Key"/> from Avalonia.Input. Public API is unchanged apart from the modifier type.</para>
/// </summary>
public static class ShortcutBinding
{
    public static string Format(KeyModifiers mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string binding, out KeyModifiers mods, out Key key)
    {
        mods = KeyModifiers.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(binding)) return false;

        foreach (var raw in binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= KeyModifiers.Control; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "win": case "windows": case "meta": case "cmd": mods |= KeyModifiers.Meta; break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        return key != Key.None;
    }

    public static bool Matches(string binding, Key key, KeyModifiers current)
        => TryParse(binding, out var mods, out var k) && k == key && mods == current;

    public static string Pretty(string binding)
    {
        if (!TryParse(binding, out var mods, out var key)) return binding;
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
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
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        _ => key.ToString()
    };
}
