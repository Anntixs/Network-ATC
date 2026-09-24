using System.Globalization;
using NetworkAtc.Core.Sectors;

namespace NetworkAtc.Core.Import;

/// <summary>A "Plugins PluginN" entry of a .prf: the DLL path and the display it was attached to.</summary>
public sealed record EuroScopePluginEntry(int Index, string Path, string Display);

/// <summary>
/// A EuroScope profile (.prf): tab separated "group, key, value" lines such as
/// "Settings	sector	\UUWV\UUWV.sct", "ASRFastKeys	1	\ASR\APP.asr", "Plugins	Plugin0	\Plugins\TopSky\TopSky.dll"
/// or "LastSession	callsign	UUEE_TWR". Paths are relative to the .prf folder (with a leading backslash) or absolute.
/// The connection password is never kept.
/// </summary>
public sealed class EuroScopeProfile
{
    public string FilePath { get; private init; } = "";
    public string Directory => System.IO.Path.GetDirectoryName(FilePath) ?? "";
    public string Name => System.IO.Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>"Settings" group: key (case-insensitive) → value.</summary>
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>ASR files by fast key number.</summary>
    public SortedDictionary<int, string> AsrFastKeys { get; } = [];
    public List<EuroScopePluginEntry> Plugins { get; } = [];
    /// <summary>"LastSession" group without the password.</summary>
    public Dictionary<string, string> LastSession { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Groups this program does not use, "group key" → value.</summary>
    public Dictionary<string, string> Other { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The profile had a saved password (it was dropped).</summary>
    public bool HadPassword { get; private set; }

    public static EuroScopeProfile Load(string path) => Parse(SectorParser.ReadText(path), Path.GetFullPath(path));

    public static EuroScopeProfile Parse(string text, string filePath = "")
    {
        var prf = new EuroScopeProfile { FilePath = filePath };
        var pluginDisplays = new Dictionary<int, string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var f = rawLine.TrimEnd('\r').Split('\t', 3);
            if (f.Length < 2) continue;
            string group = f[0].Trim(), key = f[1].Trim(), value = f.Length > 2 ? f[2].Trim() : "";
            if (group.Length == 0 || key.Length == 0) continue;
            switch (group.ToUpperInvariant())
            {
                case "SETTINGS":
                    prf.Settings[key] = value;
                    break;
                case "ASRFASTKEYS":
                    if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && value.Length > 0) prf.AsrFastKeys[n] = value;
                    break;
                case "PLUGINS":
                    if (!key.StartsWith("Plugin", StringComparison.OrdinalIgnoreCase)) break;
                    string rest = key[6..];
                    bool display = rest.EndsWith("Display", StringComparison.OrdinalIgnoreCase);
                    if (display) rest = rest[..^7];
                    if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) break;
                    if (display) pluginDisplays[index] = value;
                    else if (value.Length > 0) prf.Plugins.Add(new EuroScopePluginEntry(index, value, ""));
                    break;
                case "LASTSESSION":
                    if (key.Contains("password", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.Length > 0) prf.HadPassword = true;
                        break;
                    }
                    prf.LastSession[key] = value;
                    break;
                default:
                    if (key.Contains("password", StringComparison.OrdinalIgnoreCase)) break;
                    prf.Other[group + " " + key] = value;
                    break;
            }
        }
        for (int i = 0; i < prf.Plugins.Count; i++)
            if (pluginDisplays.TryGetValue(prf.Plugins[i].Index, out var d)) prf.Plugins[i] = prf.Plugins[i] with { Display = d };
        prf.Plugins.Sort((a, b) => a.Index.CompareTo(b.Index));
        return prf;
    }

    /// <summary>The value of the first "Settings" key that is present (keys differ in case between EuroScope versions).</summary>
    public string? Setting(params string[] keys)
    {
        foreach (var k in keys)
            if (Settings.TryGetValue(k, out var v) && v.Length > 0) return v;
        return null;
    }

    public string? SectorFile => Setting("sector", "sectorfile");
    public string? SymbologyFile => Setting("SettingsfileSYMBOLOGY");
    public string? TagsFile => Setting("SettingsfileTAGS");
    public string? ScreenFile => Setting("SettingsfileSCREEN");
    public string? GeneralFile => Setting("Settingsfile", "SettingsfileGENERAL");
    public string? VoiceFile => Setting("SettingsfileVOICE");
    public string? AliasFile => Setting("aliasfile", "alias");

    /// <summary>Resolves a path from the profile against its folder (see <see cref="EuroScopePaths.Resolve"/>).</summary>
    public string? Resolve(string? path) => EuroScopePaths.Resolve(Directory, path);
}

/// <summary>
/// EuroScope stores paths as "\Settings\Tags.txt" (relative to the profile folder), "..\x" or
/// absolute paths from the machine that made the package. Files are found case-insensitively, so a
/// package made on Windows also opens elsewhere; an absolute path that does not exist is looked up
/// by its trailing folders under the profile folder.
/// </summary>
public static class EuroScopePaths
{
    public static string? Resolve(string baseDirectory, string? path, bool directory = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string raw = path.Trim().Trim('"');
        try
        {
            bool windowsAbsolute = raw.Length >= 2 && raw[1] == ':' || raw.StartsWith(@"\\", StringComparison.Ordinal);
            bool unixAbsolute = raw.StartsWith('/');
            if ((windowsAbsolute && OperatingSystem.IsWindows() || unixAbsolute) && Exists(raw, directory)) return Path.GetFullPath(raw);

            var segments = raw.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (windowsAbsolute && segments.Count > 0 && segments[0].EndsWith(':')) segments.RemoveAt(0);
            if (!windowsAbsolute && Walk(baseDirectory, segments, directory) is { } relative) return relative;
            if (windowsAbsolute || unixAbsolute)
            {
                // A path from another machine: try its tail under the profile folder, longest first.
                for (int skip = 1; skip < segments.Count; skip++)
                    if (Walk(baseDirectory, segments.Skip(skip).ToList(), directory) is { } found) return found;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unreadable folder or a malformed path: treated as missing.
        }
        return null;
    }

    private static bool Exists(string path, bool directory) => directory ? System.IO.Directory.Exists(path) : File.Exists(path);

    private static string? Walk(string start, List<string> segments, bool directory)
    {
        if (segments.Count == 0) return null;
        string current = start;
        for (int i = 0; i < segments.Count; i++)
        {
            string s = segments[i];
            bool last = i == segments.Count - 1;
            if (s == ".") continue;
            if (s == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }
            string exact = Path.Combine(current, s);
            bool wantDirectory = !last || directory;
            if (Exists(exact, wantDirectory))
            {
                current = exact;
                continue;
            }
            if (!System.IO.Directory.Exists(current)) return null;
            var entries = wantDirectory ? System.IO.Directory.EnumerateDirectories(current) : System.IO.Directory.EnumerateFiles(current);
            string? match = entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), s, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;
            current = match;
        }
        return Exists(current, directory) ? Path.GetFullPath(current) : null;
    }

    /// <summary>A file in a folder, by name, ignoring case.</summary>
    public static string? FindFile(string? directory, string name)
    {
        if (directory == null || !System.IO.Directory.Exists(directory)) return null;
        try
        {
            return System.IO.Directory.EnumerateFiles(directory)
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
