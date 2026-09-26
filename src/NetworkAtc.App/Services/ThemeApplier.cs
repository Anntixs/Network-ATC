using System.Windows;
using System.Windows.Media;
using NetworkAtc.Core.Customization;

namespace NetworkAtc.App.Services;

/// <summary>Pushes a theme into the application resources, so every DynamicResource updates live.</summary>
public static class ThemeApplier
{
    public static void Apply(Theme theme)
    {
        var r = Application.Current.Resources;
        r["BgBrush"] = Paint.Brush(theme.Background);
        r["PanelBrush"] = Paint.Brush(theme.Panel);
        r["BorderBrush"] = Paint.Brush(theme.PanelBorder);
        r["TextBrush"] = Paint.Brush(theme.Text);
        r["MutedBrush"] = Paint.Brush(theme.MutedText);
        r["AccentBrush"] = Paint.Brush(theme.Accent);
        r["DangerBrush"] = Paint.Brush(theme.Danger);
        r["SuccessBrush"] = Paint.Brush(theme.Success);
        r["SelectBrush"] = Paint.Brush(theme.TagSelected);
        r["WarningBrush"] = Paint.Brush(theme.Warning);
        // Hover: the panel color moved slightly towards the text color.
        Color p = Paint.ToColor(theme.Panel), t = Paint.ToColor(theme.Text);
        r["HoverBrush"] = Mix(p, t, 0.07);
        r["HeaderBrush"] = Mix(p, t, 0.035);
    }

    private static SolidColorBrush Mix(Color a, Color b, double f)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f)));
        brush.Freeze();
        return brush;
    }
}
