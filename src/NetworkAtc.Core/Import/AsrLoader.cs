using NetworkAtc.Core.Customization;

namespace NetworkAtc.Core.Import;

/// <summary>What opening a EuroScope .asr found: its sector file, its display type, and notes for the user.</summary>
public sealed record AsrOpenResult(string? SectorPath, string DisplayType, IReadOnlyList<string> Plugins, List<string> Notes);

/// <summary>
/// Opens a EuroScope display file (.asr) the way EuroScope does: its sector file, the map elements it shows (only those),
/// the screen area, the display settings, and the display type with the data its plugin saved (for example the ground
/// radar display of the GRP plugin and its settings).
/// </summary>
public static class AsrLoader
{
    /// <summary>The display type of the standard EuroScope radar screen.</summary>
    public const string StandardDisplay = "Standard ES radar screen";

    public static AsrOpenResult Apply(string asrPath, Profile profile)
    {
        var asr = EuroScopeAsr.Parse(File.ReadAllText(asrPath));
        var notes = new List<string>();

        string? sector = asr.SectorFile is { } raw ? FindSector(raw, asrPath) : null;
        if (asr.SectorFile != null && sector == null) notes.Add($"sector file not found: {asr.SectorFile}");

        if (asr.ElementCount > 0)
        {
            foreach (var layer in EuroScopeAsr.ControlledLayers) profile.Layers[layer] = asr.VisibleLayers.Contains(layer);
            if (asr.VisibleLayers.Contains("LABELS")) profile.Layers["LABELS"] = true;
            profile.MapItems = asr.Items
                .Where(kv => kv.Key is not ("FIXES" or "VOR" or "NDB" or "AIRPORTS" or "RUNWAYS"))
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            notes.Add($"map elements: {asr.ElementCount}");
        }
        else profile.MapItems = new(StringComparer.OrdinalIgnoreCase);

        if (asr.View() is var (center, scale))
        {
            profile.ViewCenterLatitude = center.Latitude;
            profile.ViewCenterLongitude = center.Longitude;
            profile.ViewNmPerPixel = scale;
        }
        foreach (var (key, value) in asr.Values) EuroScopeScreenSettings.Apply(key, value, profile);

        bool standard = asr.DisplayType.Length == 0 || asr.DisplayType.Equals(StandardDisplay, StringComparison.OrdinalIgnoreCase);
        profile.EsDisplayType = standard ? "" : asr.DisplayType;
        profile.EsDisplayData = new Dictionary<string, string>(asr.PluginData);
        profile.GroundAirport = "";

        profile.RecentAsr.RemoveAll(p => p.Equals(asrPath, StringComparison.OrdinalIgnoreCase));
        profile.RecentAsr.Insert(0, asrPath);
        if (profile.RecentAsr.Count > 8) profile.RecentAsr.RemoveRange(8, profile.RecentAsr.Count - 8);
        return new AsrOpenResult(sector, profile.EsDisplayType, [.. asr.Plugins], notes);
    }

    /// <summary>
    /// The sector file named in the ASR: as written, or (the ASR moved with its profile to another computer) the file of
    /// that name next to the ASR or anywhere under the profile folder above it.
    /// </summary>
    public static string? FindSector(string raw, string asrPath)
    {
        raw = raw.Trim();
        if (File.Exists(raw)) return Path.GetFullPath(raw);
        string name = raw.Replace('\\', '/').Split('/').Last();
        if (name.Length == 0) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(asrPath));
        for (int up = 0; dir != null && up < 3; up++, dir = Path.GetDirectoryName(dir))
        {
            string relative = Path.Combine(dir, raw.TrimStart('\\', '/').Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(relative)) return relative;
            try
            {
                var found = Directory.EnumerateFiles(dir, name, new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 4, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true,
                }).FirstOrDefault();
                if (found != null) return found;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}
