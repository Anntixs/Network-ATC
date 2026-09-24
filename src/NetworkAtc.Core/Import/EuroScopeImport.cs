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
    public SectorFile Maps { get; } = new() { Name = "Карты плагинов" };
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
        if (!File.Exists(prfPath)) throw new FileNotFoundException("Файл профиля EuroScope не найден", prfPath);
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

        if (prf.VoiceFile != null) report.Skip("Настройки голосовой связи EuroScope не переносятся: настройте их в окне «Голосовая связь»");
        var known = new[] { "sector", "sectorfile", "SettingsfileSYMBOLOGY", "SettingsfileTAGS", "SettingsfileSCREEN", "Settingsfile",
            "SettingsfileGENERAL", "SettingsfileVOICE", "aliasfile", "alias" };
        var unused = prf.Settings.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unused.Count > 0) report.Skip($"Файлы профиля без аналога: {ImportReport.Short(unused)}");
        if (prf.Other.Count > 0) report.Skip($"Прочие строки профиля не используются ({prf.Other.Count})");
        if (prf.HadPassword) report.Skip("Пароль из профиля не импортируется");

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
            report.Warn($"Не удалось прочитать {Path.GetFileName(path)}: {e.Message}");
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
            report.Warn($"{what}: файл не найден ({raw})");
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
            report.Skip("В профиле не указан файл сектора");
            return;
        }
        var sct = prf.Resolve(raw);
        if (sct == null)
        {
            report.Warn($"Сектор: файл не найден ({raw})");
            return;
        }
        result.SectorPath = sct;
        result.EsePath = EuroScopePaths.FindFile(Path.GetDirectoryName(sct), Path.GetFileNameWithoutExtension(sct) + ".ese");
        result.Profile.SectorFile = sct;
        try
        {
            result.Sector = SectorParser.LoadFiles(sct, result.EsePath);
            report.Ok($"Сектор «{result.Sector.Name}»: {Path.GetFileName(sct)}" + (result.EsePath != null ? " + " + Path.GetFileName(result.EsePath) : " (без .ese)"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            report.Warn($"Сектор {Path.GetFileName(sct)} не прочитан: {e.Message}");
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
            done.Add("позывной " + profile.Station.Callsign);
        }
        if (Get("realname") is { } name)
        {
            profile.Connection.RealName = name;
            done.Add("имя");
        }
        if (Get("certificate") is { } cid)
        {
            if (EsNames.TryInt(cid, out var id) && id > 0)
            {
                profile.Connection.Cid = id;
                done.Add("номер");
            }
        }
        if (Get("rating") is { } rating && EsNames.TryInt(rating, out var r) && r is >= 1 and <= 12)
        {
            profile.Station.Rating = r;
            done.Add("рейтинг");
        }
        if (Get("facility") is { } facility && EsNames.TryInt(facility, out var fac) && fac is >= 0 and <= 6)
        {
            profile.Station.Facility = (Facility)fac;
            done.Add("тип позиции");
        }
        if (Get("range") is { } range && EsNames.TryInt(range, out var vis) && vis > 0)
        {
            profile.Station.VisualRange = vis;
            done.Add("дальность видимости");
        }
        if ((Get("frequency") ?? Get("freq")) is { } freq &&
            double.TryParse(freq, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz) && mhz is >= 118 and < 137)
        {
            profile.Station.Frequency = mhz.ToString("0.000", CultureInfo.InvariantCulture);
            done.Add("частота");
        }
        if (Get("server") is { } server)
        {
            if (server.Contains('.') && !server.Contains(' '))
            {
                profile.Connection.Host = server;
                done.Add("сервер");
            }
            else
            {
                report.Skip($"Сервер «{server}» не перенесён: выберите сервер SkyNetwork при подключении");
            }
        }
        var atis = s.Where(kv => kv.Key.StartsWith("atis", StringComparison.OrdinalIgnoreCase) && kv.Value.Length > 0)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Value).ToList();
        if (atis.Count > 0)
        {
            used += atis.Count;
            profile.ControllerInfo = atis;
            done.Add($"информация диспетчера ({atis.Count} стр.)");
        }
        if (done.Count > 0) report.Ok("Последний сеанс: " + string.Join(", ", done));
        int rest = s.Count(kv => kv.Value.Length > 0) - used;
        if (rest > 0) report.Skip($"Параметры последнего сеанса без аналога: {rest}");
    }

    // ---- symbology ---------------------------------------------------------------------------------

    private static void ImportSymbology(EuroScopeProfile prf, EuroScopeImportResult result)
    {
        var report = result.Report;
        var text = ReadReferenced(prf, prf.SymbologyFile, "Симвология", report, out _);
        if (text == null)
        {
            if (prf.SymbologyFile == null) report.Skip("В профиле нет файла символогии: цвета не изменены");
            return;
        }
        var items = EuroScopeSymbology.Parse(text);
        var theme = result.Profile.Theme.Clone();
        theme.Name = prf.Name;
        var unused = EuroScopeSymbology.ApplyTo(items, theme);
        if (items.Count == unused.Count)
        {
            report.Warn("Симвология: не найдено ни одного известного цвета");
            return;
        }
        result.Theme = theme;
        result.Profile.Theme = theme;
        report.Ok($"Цвета: {items.Count - unused.Count} из {items.Count} элементов символогии → тема «{theme.Name}»");
        if (unused.Count > 0) report.Skip($"Элементы символогии без аналога: {ImportReport.Short(unused.Select(i => $"{i.Group}:{i.Name}"))}");
    }

    // ---- tags --------------------------------------------------------------------------------------

    private static void ImportTags(EuroScopeProfile prf, EuroScopeAsr? asr, Profile profile, ImportReport report)
    {
        var text = ReadReferenced(prf, prf.TagsFile, "Теги", report, out _);
        if (text == null) return;
        var families = EuroScopeTags.Parse(text);
        if (families.Count == 0)
        {
            report.Warn("Теги: в файле нет определений тегов");
            return;
        }
        var family = families.FirstOrDefault(f => asr?.TagFamily != null && f.Name.Equals(asr.TagFamily, StringComparison.OrdinalIgnoreCase))
                     ?? families.FirstOrDefault(f => f.Types.Any(t => t.State != null && t.Items.Count > 0) && !f.Name.Contains("built in", StringComparison.OrdinalIgnoreCase))
                     ?? families[0];
        var skipped = new List<string>();
        var states = EuroScopeTags.ApplyTo(family, profile, skipped, out int clicks);
        string label = family.Name.Length > 0 ? $"семейство «{family.Name}»" : "теги";
        if (states.Count > 0)
        {
            var names = states.Select(s => s switch
            {
                EsTagState.Untracked => "без сопровождения",
                EsTagState.Tracked => "сопровождаемый",
                _ => "подробный",
            });
            report.Ok($"Теги: {label} → макеты: {string.Join(", ", names)}; действий по щелчку: {clicks}");
        }
        else
        {
            report.Warn($"Теги: {label} не удалось перенести, оставлены прежние макеты");
        }
        if (skipped.Count > 0) report.Skip($"Теги, не перенесено: {ImportReport.Short(skipped)}");
        var others = families.Where(f => f != family && f.Name.Length > 0).Select(f => f.Name).ToList();
        if (others.Count > 0) report.Skip($"Другие семейства тегов не импортированы: {ImportReport.Short(others)}");
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

        foreach (var (raw, what) in new[] { (prf.GeneralFile, "Общие настройки"), (prf.ScreenFile, "Настройки экрана") })
        {
            var text = ReadReferenced(prf, raw, what, result.Report, out var path);
            if (text != null) Apply(Path.GetFileName(path!), EuroScopeScreenSettings.Parse(text), true);
        }
        // Display settings of the ASR (altitude filter, history dots, leader) override the general ones.
        if (asr != null) Apply("ASR", asr.Values, false);

        var p = result.Profile;
        if (applied.Count > 0)
            result.Report.Ok($"Настройки: эшелон перехода {p.TransitionAltitude}, точек истории {p.Targets.HistoryDots}, " +
                             $"линия прогноза {p.Targets.PredictionMinutes.ToString(CultureInfo.InvariantCulture)} мин, " +
                             $"фильтр высот {p.Targets.FilterFloor}–{p.Targets.FilterCeiling}");
        if (result.SkippedSettings.Count > 0)
            result.Report.Skip($"Настройки без аналога ({result.SkippedSettings.Count}): " +
                               ImportReport.Short(result.SkippedSettings.Select(s => s[(s.IndexOf(": ", StringComparison.Ordinal) + 2)..])));
    }

    // ---- aliases -----------------------------------------------------------------------------------

    private static void ImportAliases(EuroScopeProfile prf, Profile profile, ImportReport report)
    {
        var text = ReadReferenced(prf, prf.AliasFile, "Алиасы", report, out _);
        if (text == null) return;
        var aliases = EuroScopeAliases.Parse(text);
        foreach (var (k, v) in aliases) profile.Aliases[k] = v;
        if (aliases.Count > 0) report.Ok($"Алиасы: {aliases.Count}");
        else report.Warn("Алиасы: в файле нет строк вида «.alias текст»");
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
                result.Report.Warn($"ASR (клавиша {key}): файл не найден ({raw})");
                continue;
            }
            result.AsrFiles.Add(path);
            if (first != null)
            {
                result.Report.Skip($"ASR {Path.GetFileName(path)} (клавиша {key}) не импортирован: в профиле Network-ATC один набор слоёв");
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
            done.Add($"видимость слоёв ({asr.ElementCount} элементов)");
        }
        if (asr.View() is var (center, scale))
        {
            profile.ViewCenterLatitude = center.Latitude;
            profile.ViewCenterLongitude = center.Longitude;
            profile.ViewNmPerPixel = scale;
            done.Add("область экрана");
        }
        if (done.Count > 0) report.Ok($"ASR {file}: {string.Join(", ", done)}");
        else report.Warn($"ASR {file}: нечего перенести");
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
                report.Ok($"Плагин {file}: будет запущен");
            }
            else report.Skip($"Плагин {file}: DLL не найдена");
            result.Profile.ImportedPlugins.Add(lower.StartsWith("topsky") ? $"{file}: карты и зоны"
                : lower.StartsWith("grplugin") ? $"{file}: стоянки и карты"
                : lower.StartsWith("ccams") ? $"{file}: диапазон кодов"
                : runs ? $"{file}: запускается" : $"{file}: DLL не найдена");
        }
        foreach (var (layer, visible) in visibility) result.Profile.Layers[layer] = visible;
        if (maps.UnknownPoints.Count > 0)
            report.Warn($"Точки, которых нет в секторе, пропущены в картах плагинов: {ImportReport.Short(maps.UnknownPoints)}");
    }

    private static string? PluginFile(string? dir, string name, string plugin, ImportReport report, bool required)
    {
        var path = EuroScopePaths.FindFile(dir, name);
        if (path == null && required) report.Warn($"{plugin}: нет файла {name}" + (dir == null ? " (папка плагина не найдена)" : ""));
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
            parts.Add($"{stats.Maps} карт (включено {stats.Active})");
            if (stats.Conditional > 0) report.Skip($"TopSky: условия включения карт не поддерживаются ({stats.Conditional}), такие карты выключены");
            if (stats.UnknownKeywords.Count > 0) report.Skip($"TopSky: строки карт без аналога: {ImportReport.Short(stats.UnknownKeywords)}");
        }
        if (ReadFile(PluginFile(dir, "TopSkyAreas.txt", name, report, false), report) is { } areaText)
            parts.Add($"{TopSkyAreas.Parse(areaText, name, maps, colors, visibility)} зон (выключены, включите в списке слоёв)");
        if (parts.Count > 0) report.Ok($"TopSky: {string.Join(", ", parts)}");
        report.Skip("TopSky: сам плагин не работает в Network-ATC, перенесены только карты и зоны");
    }

    private static void ImportGroundRadar(string? dir, MapLayerBuilder maps, Dictionary<string, bool> visibility, ImportReport report)
    {
        const string name = "Ground Radar";
        var parts = new List<string>();
        if (ReadFile(PluginFile(dir, "GRpluginStands.txt", name, report, true), report) is { } stands)
        {
            var counts = GroundRadarStands.Parse(stands, maps, visibility);
            if (counts.Count > 0) parts.Add("стоянки " + string.Join(", ", counts.Select(kv => $"{kv.Key} ({kv.Value})")));
        }
        if (ReadFile(PluginFile(dir, "GRpluginMaps.txt", name, report, false), report) is { } mapText)
            parts.Add($"{TopSkyMaps.Parse(mapText, name, maps, new Dictionary<string, string>(), visibility).Maps} карт");
        if (parts.Count > 0) report.Ok($"{name}: {string.Join(", ", parts)}");
        report.Skip($"{name}: сам плагин не работает в Network-ATC, перенесены только стоянки и карты");
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
            report.Ok($"CCAMS: диапазон кодов {range}");
        }
        else
        {
            report.Skip("CCAMS: локальных настроек диапазона кодов нет, используются диапазоны позиций из сектора");
        }
    }
}
