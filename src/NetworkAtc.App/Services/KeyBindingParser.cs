using System.Windows.Input;

namespace NetworkAtc.App.Services;

/// <summary>Parses "Ctrl+Shift+F", "F5", "Add", "OemPeriod" into a key and modifiers.</summary>
public static class KeyBindingParser
{
    public static bool TryParse(string text, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        return key != Key.None;
    }

    public static string Format(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join('+', parts);
    }
}
