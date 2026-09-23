using System.Windows;
using System.Windows.Media;

namespace NetworkAtc.App.Services;

/// <summary>Frozen brushes and pens cached by "#RRGGBB" / "#AARRGGBB" color strings.</summary>
public static class Paint
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string, double, bool), Pen> Pens = [];

    public static Color ToColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex)) return (Color)ColorConverter.ConvertFromString(hex.Trim());
        }
        catch (FormatException)
        {
        }
        return Colors.Magenta; // makes a typo in a theme obvious
    }

    public static bool IsValid(string? hex)
    {
        try { return !string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex.Trim()) is Color; }
        catch (FormatException) { return false; }
    }

    public static SolidColorBrush Brush(string? hex)
    {
        hex ??= "#FF00FF";
        if (Brushes.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush(ToColor(hex));
        b.Freeze();
        return Brushes[hex] = b;
    }

    public static Pen Pen(string? hex, double width = 1, bool dashed = false)
    {
        hex ??= "#FF00FF";
        if (Pens.TryGetValue((hex, width, dashed), out var p)) return p;
        p = new Pen(Brush(hex), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dashed) p.DashStyle = new DashStyle([4, 4], 0);
        p.Freeze();
        return Pens[(hex, width, dashed)] = p;
    }

    public static string WithAlpha(string hex, byte alpha)
    {
        var c = ToColor(hex);
        return $"#{alpha:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
