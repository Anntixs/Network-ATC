using System.Windows.Media;

namespace NetworkAtc.App.Services;

/// <summary>
/// Fonts by name: IBM Plex comes with the program (Fonts folder, embedded), anything else is looked up in Windows.
/// </summary>
public static class AppFonts
{
    private static readonly Uri Base = new("pack://application:,,,/");
    private static readonly Dictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase);

    public const string Mono = "IBM Plex Mono";
    public const string Ui = "IBM Plex Sans Condensed";

    /// <summary>The first family of a comma-separated list ("IBM Plex Mono, Consolas").</summary>
    public static FontFamily Family(string list)
    {
        string name = list.Split(',')[0].Trim();
        if (name.Length == 0) name = Mono;
        if (Families.TryGetValue(name, out var f)) return f;
        f = name.StartsWith("IBM Plex", StringComparison.OrdinalIgnoreCase)
            ? new FontFamily(Base, "./Fonts/#" + name)
            : new FontFamily(name);
        return Families[name] = f;
    }
}
