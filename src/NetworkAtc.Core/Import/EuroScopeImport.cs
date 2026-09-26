using System.Globalization;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Sectors;

namespace NetworkAtc.Core.Import;

/// <summary>Everything taken from a EuroScope profile package.</summary>
public sealed class EuroScopeImportResult
{
    public EuroScopeProfile Source { get; init; } = new();
    /// <summary>A copy of the current profile with the imported settings; the input profile is not changed.</summary>
    public Profile Profile { get; init; } = new();
    /// <summary>Theme built from the symbology file (also set as <see cref="Customization.Profile.Theme"/>), or null.</summary>
    public Theme? Theme { get; set; }
    /// <summary>The .sct and .ese files of the profile, or null when not found.</summary>
    public string? SectorPath { get; set; }
    public string? EsePath { get; set; }
    /// <summary>The loaded EuroScope sector with <see cref="Maps"/> merged in, or null.</summary>
    public SectorFile? Sector { get; set; }
    /// <summary>ASR files by fast key (resolved paths); the first one was imported.</summary>
    public List<string> AsrFiles { get; } = [];
    /// <summary>Map layers made from plugin data (TopSky maps and areas, Ground Radar stands and maps).</summary>
    public SectorFile Maps { get; } = new() { Name = "Plugin maps" };
    /// <summary>Settings from the EuroScope settings files that have no counterpart ("file: key").</summary>
    public List<string> SkippedSettings { get; } = [];
    public ImportReport Report { get; } = new();

    /// <summary>Adds the plugin map layers (lines, colors and labels) to a sector.</summary>
    public void MergeMapsInto(SectorFile sector)
    {
        foreach (var (layer, lines) in Maps.Lines)
        {
            if (!sector.Lines.TryGetValue(layer, out var target)) sector.Lines[layer] = target = [];
            target.AddRange(lines);
        }
        foreach (var (layer, color) in Maps.LayerColors) sector.LayerColors.TryAdd(layer, color);
        sector.Labels.AddRange(Maps.Labels);
    }

    /// <summary>
    /// Writes the sector with the plugin maps as a Network-ATC sector (.natc) and points the profile to it,
    /// so the maps stay after a restart. Without a EuroScope sector only the plugin maps are written.
    /// </summary>
    public void SaveNativeSector(string path)
    {
        var sector = Sector;
        if (sector == null)
        {
            sector = new SectorFile { Name = Source.Name };
            MergeMapsInto(sector);
        }
        NativeSector.Save(sector, path);
        Profile.SectorFile = path;
    }
}

/// <summary>
/// Imports a whole EuroScope profile (.prf): the sector, the settings files (symbology, tags, screen,
/// general, aliases), the first ASR and the data files of known plugins. Plugin DLLs cannot run here,
/// so only their data is taken. Anything that could not be used goes to <see cref="EuroScopeImportResult.Report"/>.
/// </summary>
public static class EuroScopeImport
{
    /// <summary>Only a missing or unreadable .prf throws; problems with the files it refers to are reported.</summary>
    public static EuroScopeImportResult Import(string prfPath, Profile current)
    {
        if (!File.Exists(prfPath)) throw new FileNotFoundException("EuroScope profile file not found", prfPath);
        var prf = EuroScopeProfile.Load(prfPath);
        var profile = current.Clone();
        // The JSON copy loses the case-insensitive keys of the dictionaries.
        profile.Layers = new Dictionary<string, bool>(profile.Layers, StringComparer.OrdinalIgnoreCase);
        profile.Aliases = new Dictionary<string, string>(profile.Aliases, StringComparer.OrdinalIgnoreCase);
        profile.TagClicks = new Dictionary<string, Tags.TagClickBinding>(profile.TagClicks, StringComparer.OrdinalIgnoreCase);
        profile.Windows = new Dictionary<string, WindowLayout>(profile.Windows, StringComparer.OrdinalIgnoreCase);
        profile.ActiveRunways = new Dictionary<string, RunwayUse>(profile.ActiveRunways, StringComparer.OrdinalIgnoreCase);
        profile.AtisLetters = new Dictionary<string, string>(profile.AtisLetters, StringComparer.OrdinalIgnoreCase);
        profile.Name = prf.Name;
        var result = new EuroScopeImportResult { Source = prf, Profile = profile };
        var report = result.Report;

        var asr = ImportAsrList(prf, result);
        ImportSector(prf, asr, result);
        ImportLastSession(prf, profile, report);
        ImportSymbology(prf, result);
        ImportTags(prf, asr, profile, report);
        ImportSettings(prf, asr, result);
        ImportAliases(prf, profile, report);
        if (asr != null) ApplyAsr(asr, profile, report, Path.GetFileName(result.AsrFiles[0]));
        ImportPlugins(prf, result);

        if (prf.VoiceFile != null) report.Skip("EuroScope voice settings are not imported: set them up in the Voice window");
        var known = new[] { "sector", "sectorfile", "SettingsfileSYMBOLOGY", "SettingsfileTAGS", "SettingsfileSCREEN", "Settingsfile",
            "SettingsfileGENERAL", "SettingsfileVOICE", "aliasfile", "alias" };
        var unused = prf.Settings.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unused.Count > 0) report.Skip($"Profile files with no equivalent: {ImportReport.Short(unused)}");
        if (prf.Other.Count > 0) report.Skip($"Other profile lines are not used ({prf.Other.Count})");
        if (prf.HadPassword) report.Skip("The password in the profile is not imported");

        if (result.Sector != null && result.Maps.Lines.Count > 0) result.MergeMapsInto(result.Sector);
        return result;
    }

    private static string? ReadFile(string? path, ImportReport report)
    {
        if (path == null) return null;
        try
        {
            return SectorParser.ReadText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Warn($"Could not read {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    /// <summary>Resolves and reads a file the profile refers to; a missing file is reported.</summary>
    private static string? ReadReferenced(EuroScopeProfile prf, string? raw, string what, ImportReport report, out string? resolved)
    {
        resolved = null;
        if (raw == null) return null;
        resolved = prf.Resolve(raw);
        if (resolved == null)
        {
            report.Warn($"{what}: file not found ({raw})");
            return null;
        }
        return ReadFile(resolved, report);
    }

    // ---- sector ----------------------------------------------------------------------------------

    private static void ImportSector(EuroScopeProfile prf, EuroScopeAsr? asr, EuroScopeImportResult result)
    {
        var report = result.Report;
        string? raw = prf.SectorFile ?? asr?.SectorFile;
        if (raw == null)
        {
            report.Skip("The profile has no sector file");
            return;
        }
        var sct = prf.Resolve(raw);
        if (sct == null)
        {
            report.Warn($"Sector: file not found ({raw})");
            return;
        }
        result.SectorPath = sct;
        result.EsePath = EuroScopePaths.FindFile(Path.GetDirectoryName(sct), Path.GetFileNameWithoutExtension(sct) + ".ese");
        result.Profile.SectorFile = sct;
        try
        {
            result.Sector = SectorParser.LoadFiles(sct, result.EsePath);
            report.Ok($"Sector \"{result.Sector.Name}\": {Path.GetFileName(sct)}" + (result.EsePath != null ? " + " + Path.GetFileName(result.EsePath) : " (no .ese)"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            report.Warn($"Sector {Path.GetFileName(sct)} could not be read: {e.Message}");
        }
    }

    // ---- last session ------------------------------------------------------------------------------

    private static void ImportLastSession(EuroScopeProfile prf, Profile profile, ImportReport report)
    {
        var s = prf.LastSession;
        var done = new List<string>();
        int used = 0;
        string? Get(string key)
        {
            if (!s.TryGetValue(key, out var v) || v.Length == 0) return null;
            used++;
            return v;
        }

        if (Get("callsign") is { } callsign)
        {
            profile.Station.Callsign = callsign.ToUpperInvariant();
            done.Add("callsign " + profile.Station.Callsign);
        }
        if (Get("realname") is { } name)
        {
            profile.Connection.RealName = name;
            done.Add("name");
        }
        if (Get("certificate") is { } cid)
        {
            if (EsNames.TryInt(cid, out var id) && id > 0)
            {
                profile.Connection.Cid = id;
                done.Add("CID");
            }
        }
        if (Get("rating") is { } rating && EsNames.TryInt(rating, out var r) && r is >= 1 and <= 12)
        {
            profile.Station.Rating = r;
            done.Add("rating");
        }
        if (Get("facility") is { } facility && EsNames.TryInt(facility, out var fac) && fac is >= 0 and <= 6)
        {
            profile.Station.Facility = (Facility)fac;
            done.Add("facility type");
        }
        if (Get("range") is { } range && EsNames.TryInt(range, out var vis) && vis > 0)
        {
            profile.Station.VisualRange = vis;
            done.Add("visibility range");
        }
        if ((Get("frequency") ?? Get("freq")) is { } freq &&
            double.TryParse(freq, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz) && mhz is >= 118 and < 137)
        {
            profile.Station.Frequency = mhz.ToString("0.000", CultureInfo.InvariantCulture);
            done.Add("frequency");
        }
        if (Get("server") is { } server)
        {
            if (server.Contains('.') && !server.Contains(' '))
            {
                profile.Connection.Host = server;
                done.Add("server");
            }
            else
            {
                report.Skip($"Server \"{server}\" not imported: choose a SkyNetwork server when connecting");
            }
        }
        var atis = s.Where(kv => kv.Key.StartsWith("atis", StringComparison.OrdinalIgnoreCase) && kv.Value.Length > 0)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Value).ToList();
        if (atis.Count > 0)
        {
            used += atis.Count;
            profile.ControllerInfo = atis;
            done.Add($"controller info ({atis.Count} lines)");
        }
        if (done.Count > 0) report.Ok("Last session: " + string.Join(", ", done));
        int rest = s.Count(kv => kv.Value.Length > 0) - used;
        if (rest > 0) report.Skip($"Last session settings with no equivalent: {rest}");
    }

    // ---- symbology ---------------------------------------------------------------------------------

    private static void ImportSymbology(EuroScopeProfile prf, EuroScopeImportResult result)
    {
        var report = result.Report;
        var text = ReadReferenced(prf, prf.SymbologyFile, "Symbology", report, out _);
        if (text == null)
        {
            if (prf.SymbologyFile == null) report.Skip("The profile has no symbology file: colors unchanged");
            return;
        }
        var items = EuroScopeSymbology.Parse(text);
        var theme = result.Profile.Theme.Clone();
        theme.Name = prf.Name;
        var unused = EuroScopeSymbology.ApplyTo(items, theme);
        if (items.Count == unused.Count)
        {
            report.Warn("Symbology: no known colors found");
            return;
        }
        result.Theme = theme;
        result.Profile.Theme = theme;
        report.Ok($"Colors: {items.Count - unused.Count} of {items.Count} symbology items → theme \"{theme.Name}\"");
        if (unused.Count > 0) report.Skip($"Symbology items with no equivalent: {ImportReport.Short(unused.Select(i => $"{i.Group}:{i.Name}"))}");
    }

    // ---- tags --------------------------------------------------------------------------------------

    private static void ImportTags(EuroScopeProfile prf, EuroScopeAsr? asr, Profile profile, ImportReport report)
    {
        var text = ReadReferenced(prf, prf.TagsFile, "Tags", report, out _);
        if (text == null) return;
        var families = EuroScopeTags.Parse(text);
        if (families.Count == 0)
        {
            report.Warn("Tags: the file has no tag definitions");
            return;
        }
        var family = families.FirstOrDefault(f => asr?.TagFamily != null && f.Name.Equals(asr.TagFamily, StringComparison.OrdinalIgnoreCase))
                     ?? families.FirstOrDefault(f => f.Types.Any(t => t.State != null && t.Items.Count > 0) && !f.Name.Contains("built in", StringComparison.OrdinalIgnoreCase))
                     ?? families[0];
        var skipped = new List<string>();
        var states = EuroScopeTags.ApplyTo(family, profile, skipped, out int clicks);
        string label = family.Name.Length > 0 ? $"family \"{family.Name}\"" : "tags";
        if (states.Count > 0)
        {
            var names = states.Select(s => s switch
            {
                EsTagState.Untracked => "untracked",
                EsTagState.Tracked => "tracked",
                _ => "detailed",
            });
            report.Ok($"Tags: {label} → layouts: {string.Join(", ", names)}; click actions: {clicks}");
        }
        else
        {
            report.Warn($"Tags: {label} could not be imported, previous layouts kept");
        }
        if (skipped.Count > 0) report.Skip($"Tags, not imported: {ImportReport.Short(skipped)}");
        var others = families.Where(f => f != family && f.Name.Length > 0).Select(f => f.Name).ToList();
        if (others.Count > 0) report.Skip($"Other tag families not imported: {ImportReport.Short(others)}");
    }

    // ---- screen and general settings --------------------------------------------------------------

    private static void ImportSettings(EuroScopeProfile prf, EuroScopeAsr? asr, EuroScopeImportResult result)
    {
        var applied = new List<string>();
        void Apply(string file, IEnumerable<(string Key, string Value)> values, bool recordSkipped)
        {
            foreach (var (key, value) in values)
            {
                if (EuroScopeScreenSettings.Apply(key, value, result.Profile)) applied.Add(key);
                else if (recordSkipped) result.SkippedSettings.Add($"{file}: {key}");
            }
        }

        foreach (var (raw, what) in new[] { (prf.GeneralFile, "General settings"), (prf.ScreenFile, "Screen settings") })
        {
            var text = ReadReferenced(prf, raw, what, result.Report, out var path);
            if (text != null) Apply(Path.GetFileName(path!), EuroScopeScreenSettings.Parse(text), true);
        }
        // Display settings of the ASR (altitude filter, history dots, leader) override the general ones.
        if (asr != null) Apply("ASR", asr.Values, false);

        var p = result.Profile;
        if (applied.Count > 0)
            result.Report.Ok($"Settings: transition altitude {p.TransitionAltitude}, history dots {p.Targets.HistoryDots}, " +
                             $"prediction line {p.Targets.PredictionMinutes.ToString(CultureInfo.InvariantCulture)} min, " +
                             $"altitude filter {p.Targets.FilterFloor}–{p.Targets.FilterCeiling}");
        if (result.SkippedSettings.Count > 0)
            result.Report.Skip($"Settings with no equivalent ({result.SkippedSettings.Count}): " +
                               ImportReport.Short(result.SkippedSettings.Select(s => s[(s.IndexOf(": ", StringComparison.Ordinal) + 2)..])));
    }

    // ---- aliases -----------------------------------------------------------------------------------

    private static void ImportAliases(EuroScopeProfile prf, Profile profile, ImportReport report)
    {
        var text = ReadReferenced(prf, prf.AliasFile, "Aliases", report, out _);
        if (text == null) return;
        var aliases = EuroScopeAliases.Parse(text);
        foreach (var (k, v) in aliases) profile.Aliases[k] = v;
        if (aliases.Count > 0) report.Ok($"Aliases: {aliases.Count}");
        else report.Warn("Aliases: the file has no lines like \".alias text\"");
    }

    // ---- ASR ---------------------------------------------------------------------------------------

    private static EuroScopeAsr? ImportAsrList(EuroScopeProfile prf, EuroScopeImportResult result)
    {
        EuroScopeAsr? first = null;
        foreach (var (key, raw) in prf.AsrFastKeys)
        {
            var path = prf.Resolve(raw);
            if (path == null)
            {
                result.Report.Warn($"ASR (key {key}): file not found ({raw})");
                continue;
            }
            result.AsrFiles.Add(path);
            if (first != null)
            {
                result.Report.Skip($"ASR {Path.GetFileName(path)} (key {key}) not imported: a Network-ATC profile has one set of layers");
                continue;
            }
            if (ReadFile(path, result.Report) is { } text) first = EuroScopeAsr.Parse(text);
        }
        return first;
    }

    private static void ApplyAsr(EuroScopeAsr asr, Profile profile, ImportReport report, string file)
    {
        var done = new List<string>();
        if (asr.ElementCount > 0)
        {
            foreach (var layer in EuroScopeAsr.ControlledLayers) profile.Layers[layer] = asr.VisibleLayers.Contains(layer);
            if (asr.VisibleLayers.Contains("LABELS")) profile.Layers["LABELS"] = true;
            done.Add($"layer visibility ({asr.ElementCount} items)");
        }
        if (asr.View() is var (center, scale))
        {
            profile.ViewCenterLatitude = center.Latitude;
            profile.ViewCenterLongitude = center.Longitude;
            profile.ViewNmPerPixel = scale;
            done.Add("screen area");
        }
        if (done.Count > 0) report.Ok($"ASR {file}: {string.Join(", ", done)}");
        else report.Warn($"ASR {file}: nothing to import");
    }

    // ---- plugins -----------------------------------------------------------------------------------

    private static void ImportPlugins(EuroScopeProfile prf, EuroScopeImportResult result)
    {
        var report = result.Report;
        var maps = new MapLayerBuilder(result.Maps, MapLayerBuilder.PointsOf(result.Sector));
        var visibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        result.Profile.ImportedPlugins = [];
        result.Profile.EsPlugins = [];
        foreach (var plugin in prf.Plugins)
        {
            var segments = plugin.Path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            string file = segments.Length > 0 ? segments[^1] : plugin.Path;
            string dll = prf.Resolve(plugin.Path) ?? "";
            string? dir = dll.Length > 0
                ? Path.GetDirectoryName(dll)
                : EuroScopePaths.Resolve(prf.Directory, plugin.Path[..Math.Max(0, plugin.Path.Length - file.Length)], directory: true);
            string lower = file.ToLowerInvariant();
            if (lower.StartsWith("topsky")) ImportTopSky(dir, maps, visibility, report);
            else if (lower.StartsWith("grplugin")) ImportGroundRadar(dir, maps, visibility, report);
            else if (lower.StartsWith("ccams")) ImportCcams(dir, result.Profile, report);
            // The DLL itself runs in the plugin host; the data files above also give our own maps and codes.
            bool runs = dll.Length > 0 && File.Exists(dll);
            if (runs)
            {
                result.Profile.EsPlugins.Add(dll);
                report.Ok($"Plugin {file}: will be loaded");
            }
            else report.Skip($"Plugin {file}: DLL not found");
            result.Profile.ImportedPlugins.Add(lower.StartsWith("topsky") ? $"{file}: maps and areas"
                : lower.StartsWith("grplugin") ? $"{file}: stands and maps"
                : lower.StartsWith("ccams") ? $"{file}: squawk range"
                : runs ? $"{file}: loaded" : $"{file}: DLL not found");
        }
        foreach (var (layer, visible) in visibility) result.Profile.Layers[layer] = visible;
        if (maps.UnknownPoints.Count > 0)
            report.Warn($"Points not in the sector were skipped in plugin maps: {ImportReport.Short(maps.UnknownPoints)}");
    }

    private static string? PluginFile(string? dir, string name, string plugin, ImportReport report, bool required)
    {
        var path = EuroScopePaths.FindFile(dir, name);
        if (path == null && required) report.Warn($"{plugin}: no {name} file" + (dir == null ? " (plugin folder not found)" : ""));
        return path;
    }

    private static void ImportTopSky(string? dir, MapLayerBuilder maps, Dictionary<string, bool> visibility, ImportReport report)
    {
        const string name = "TopSky";
        var colors = ReadFile(PluginFile(dir, "TopSkySettings.txt", name, report, false), report) is { } settings
            ? TopSkyMaps.SettingsColors(settings) : new Dictionary<string, string>();
        var parts = new List<string>();
        if (ReadFile(PluginFile(dir, "TopSkyMaps.txt", name, report, true), report) is { } mapText)
        {
            var stats = TopSkyMaps.Parse(mapText, name, maps, colors, visibility);
            parts.Add($"{stats.Maps} maps ({stats.Active} on)");
            if (stats.Conditional > 0) report.Skip($"TopSky: conditional maps are not supported ({stats.Conditional}), such maps are off");
            if (stats.UnknownKeywords.Count > 0) report.Skip($"TopSky: map lines with no equivalent: {ImportReport.Short(stats.UnknownKeywords)}");
        }
        if (ReadFile(PluginFile(dir, "TopSkyAreas.txt", name, report, false), report) is { } areaText)
            parts.Add($"{TopSkyAreas.Parse(areaText, name, maps, colors, visibility)} areas (off, turn them on in the layer list)");
        if (parts.Count > 0) report.Ok($"TopSky: {string.Join(", ", parts)}");
        report.Skip("TopSky: the plugin itself does not run in Network-ATC, only its maps and areas were imported");
    }

    private static void ImportGroundRadar(string? dir, MapLayerBuilder maps, Dictionary<string, bool> visibility, ImportReport report)
    {
        const string name = "Ground Radar";
        var parts = new List<string>();
        if (ReadFile(PluginFile(dir, "GRpluginStands.txt", name, report, true), report) is { } stands)
        {
            var counts = GroundRadarStands.Parse(stands, maps, visibility);
            if (counts.Count > 0) parts.Add("stands " + string.Join(", ", counts.Select(kv => $"{kv.Key} ({kv.Value})")));
        }
        if (ReadFile(PluginFile(dir, "GRpluginMaps.txt", name, report, false), report) is { } mapText)
            parts.Add($"{TopSkyMaps.Parse(mapText, name, maps, new Dictionary<string, string>(), visibility).Maps} maps");
        if (parts.Count > 0) report.Ok($"{name}: {string.Join(", ", parts)}");
    }

    private static void ImportCcams(string? dir, Profile profile, ImportReport report)
    {
        string? range = null;
        if (dir != null && Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir).Where(f => Path.GetFileName(f).StartsWith("ccams", StringComparison.OrdinalIgnoreCase) &&
                                                                          !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                if ((range = ReadFile(file, report) is { } t ? CcamsConfig.FindRange(t) : null) != null) break;
        }
        if (range != null)
        {
            profile.SquawkRange = range;
            report.Ok($"CCAMS: squawk range {range}");
        }
        else
        {
            report.Skip("CCAMS: no local squawk range settings, the position ranges from the sector are used");
        }
    }
}
