using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Tags;

namespace NetworkAtc.Core.Customization;

public sealed class TagLayouts
{
    /// <summary>Aircraft nobody works, or someone else works.</summary>
    public string Untracked { get; set; } = DefaultUntracked;
    /// <summary>Aircraft the controller works or that are being handed over to or from them.</summary>
    public string Tracked { get; set; } = DefaultTracked;
    /// <summary>
    /// Extra lines added under the tag while the mouse is over the aircraft or its tag, as in EuroScope.
    /// The lines above stay where they are, so the field under the mouse never moves.
    /// </summary>
    public string Detailed { get; set; } = DefaultDetailed;
    /// <summary>Warnings (EMERG, CLAM, DUPE, SQ) above the tag, in the warning color.</summary>
    public bool ShowWarnings { get; set; } = true;

    public const string DefaultUntracked = "{callsign} {ho}\n{alt}{vs} {gs10}";
    public const string DefaultTracked = "{callsign} {comm} {ho}\n{alt}{vs} {cfl|---} {ahdg}\n{gs10} {type} {aspd}";
    public const string DefaultDetailed = "{dep} {dest} {rfl} {squawk}\n{proc|SID/STAR} {rwy} {next}\n{scratch|+ scratch}";

    /// <summary>The layouts before EuroScope coordination fields existed (profile version 2).</summary>
    public static readonly string[] OldDefaults =
    [
        "{callsign}\n{fl}{vs}",
        "{callsign} {wtc}\n{fl}{vs} {cfl|---}\n{gs10} {dest} {ahdg} {aspd}",
        "{callsign} {type}/{wtc} {squawk}\n{fl}{vs} {cfl|CFL} {rfl}\n{gs} {ahdg|HDG} {aspd|SPD} {dest}\n{scratch|+ scratch}",
    ];

    /// <summary>The defaults of profile versions 3–4 (the detailed tag was a whole separate tag then).</summary>
    public static readonly string[] Version3Defaults =
    [
        "{warn}\n{callsign} {ho}\n{fl}{vs} {gs10}",
        "{warn}\n{callsign} {comm}{wtc}\n{fl}{vs} {cfl|---} {ho}\n{gs10} {dest} {ahdg} {aspd}",
        "{warn}\n{callsign} {type}/{wtc} {squawk} {asq}\n{fl}{vs} {cfl|CFL} {rfl}\n{gs} {ahdg|HDG} {aspd|SPD} {dest}\n{proc|SID/STAR} {rwy} {owner|—} {next}\n{scratch|+ scratch}",
    ];

    public double FontSize { get; set; } = 11;
    public string FontFamily { get; set; } = "IBM Plex Mono";
    /// <summary>The tag font before version 8.</summary>
    public const string OldFontFamily = "Cascadia Mono, Consolas";
    public bool ShowTagLeader { get; set; } = true;
    public double DefaultOffsetX { get; set; } = 18;
    public double DefaultOffsetY { get; set; } = -30;
}

public sealed class TargetSettings
{
    public int HistoryDots { get; set; } = 6;
    /// <summary>Length of the prediction line in minutes (0 = off).</summary>
    public double PredictionMinutes { get; set; } = 1;
    public double SymbolSize { get; set; } = 7;
    /// <summary>Hide aircraft outside this altitude band (feet).</summary>
    public int FilterFloor { get; set; } = -1000;
    public int FilterCeiling { get; set; } = 99999;
    public bool ShowGroundTraffic { get; set; } = true;
}

public sealed class StationSettings
{
    public string Callsign { get; set; } = "";
    public string Frequency { get; set; } = "199.998";
    public Facility Facility { get; set; } = Facility.Observer;
    public int VisualRange { get; set; } = 100;
    public int Rating { get; set; } = 2;
}

/// <summary>Which events play a sound.</summary>
public sealed class SoundSettings
{
    public bool Enabled { get; set; } = true;
    public bool RadioMessage { get; set; } = true;
    public bool PrivateMessage { get; set; } = true;
    public bool HandoffRequest { get; set; } = true;
    public bool HandoffAccepted { get; set; } = true;
    public bool HandoffRefused { get; set; } = true;
    public bool PointOut { get; set; } = true;
    public bool ConflictAlert { get; set; } = true;
    public bool Connection { get; set; } = true;
    public double Volume { get; set; } = 0.6;
}

public sealed class ConnectionSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6809;
    public int Cid { get; set; }
    public string ProtectedPassword { get; set; } = "";
    public string RealName { get; set; } = "";
}

/// <summary>A frequency the controller listens to (and may transmit on) besides the primary one.</summary>
public sealed class VoiceFrequency
{
    public string Frequency { get; set; } = "";
    public bool Receive { get; set; } = true;
    public bool Transmit { get; set; }
    public double Volume { get; set; } = 1;
}

/// <summary>Radio voice: frequencies, audio devices, push-to-talk ("Voice" dialog).</summary>
public sealed class VoiceOptions
{
    public const int DefaultPort = 3782;
    /// <summary>At most this many extra frequencies: the server takes 8 transceivers, the primary uses one.</summary>
    public const int MaxExtraFrequencies = 7;

    /// <summary>Connect the voice server together with the network connection.</summary>
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = DefaultPort;
    public bool PrimaryReceive { get; set; } = true;
    public bool PrimaryTransmit { get; set; } = true;
    public double PrimaryVolume { get; set; } = 1;
    public List<VoiceFrequency> Frequencies { get; set; } = [];
    /// <summary>Audio devices by name ("" = Windows default): indices change when devices are plugged in.</summary>
    public string InputDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "";
    public double MicGain { get; set; } = 1;
    public double OutputVolume { get; set; } = 1;
    /// <summary>Push-to-talk control as stored by the voice library: "key:162", "joy:0:4" or "" (none).</summary>
    public string PushToTalk { get; set; } = "";
}

/// <summary>Position, size and visibility of a floating list window.</summary>
public sealed class WindowLayout
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 220;
    public bool Visible { get; set; }

    public WindowLayout() { }
    public WindowLayout(double x, double y, double width, double height, bool visible) =>
        (X, Y, Width, Height, Visible) = (x, y, width, height, visible);
}

public sealed class RecentSector
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastUsed { get; set; }
}

public sealed class PanelSettings
{
    public bool ShowFlightPlan { get; set; } = true;
    public double UiScale { get; set; } = 1.0;
    public double UiFontSize { get; set; } = 12.5;
}

/// <summary>
/// A complete workspace: look, layers, tags, keys, station and connection. Profiles are JSON files
/// in the profiles folder; the user can keep several (e.g. "Tower", "Radar", "Light").
/// </summary>
public sealed class Profile
{
    /// <summary>Profile format version, used to migrate old profiles.</summary>
    public const int CurrentVersion = 8;
    public int Version { get; set; }

    public string Name { get; set; } = "Default";
    public Theme Theme { get; set; } = new();
    /// <summary>Use the colors defined in the sector file instead of the theme's map colors.</summary>
    public bool UseSectorFileColors { get; set; }
    public string SectorFile { get; set; } = "";

    /// <summary>Layer name → visible. Unknown layers are visible.</summary>
    public Dictionary<string, bool> Layers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ARTCC"] = true, ["ARTCC HIGH"] = false, ["ARTCC LOW"] = false, ["SID"] = false, ["STAR"] = false,
        ["LOW AIRWAY"] = false, ["HIGH AIRWAY"] = false, ["GEO"] = true, ["REGIONS"] = true, ["LABELS"] = true,
        ["RUNWAYS"] = true, ["AIRPORTS"] = true, ["FIXES"] = false, ["FIX NAMES"] = false, ["VOR"] = true, ["NDB"] = true,
        ["FREETEXT"] = false, ["SECTORLINES"] = true, ["RANGE RINGS"] = true,
    };

    /// <summary>Airports the controller works: departure and arrival lists are built for them.</summary>
    public List<string> ActiveAirports { get; set; } = [];

    /// <summary>Active runways per airport (EuroScope "Active airport/runway" dialog).</summary>
    public Dictionary<string, Sectors.RunwayUse> ActiveRunways { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Current ATIS letter per airport.</summary>
    public Dictionary<string, string> AtisLetters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The controller's ATIS stations (UUEE_ATIS…): frequency, text, voice.</summary>
    public List<Atis.AtisSettings> Atis { get; set; } = [];
    /// <summary>Controller info (ATIS) lines sent to pilots on request; aliases variables like $atiscode(UUEE) are expanded.</summary>
    public List<string> ControllerInfo { get; set; } = ["$mycallsign $myfreq", "Information $atiscode($myairport)"];
    public SoundSettings Sounds { get; set; } = new();
    /// <summary>Load METARs for the active airports from aviationweather.gov.</summary>
    public bool FetchMetar { get; set; } = true;
    /// <summary>Cleared level adherence: warn when the aircraft deviates from its CFL by more than this, feet.</summary>
    public int ClamFeet { get; set; } = 300;

    /// <summary>Floating list windows by id (see <see cref="DefaultWindows"/>).</summary>
    public Dictionary<string, WindowLayout> Windows { get; set; } = DefaultWindows();

    public List<RecentSector> RecentSectors { get; set; } = [];
    /// <summary>EuroScope plugins of the last imported .prf and what became of each (shown in the PLUGINS menu).</summary>
    public List<string> ImportedPlugins { get; set; } = [];
    /// <summary>EuroScope plugin DLLs loaded into the plugin host at start, in load order.</summary>
    public List<string> EsPlugins { get; set; } = [];
    /// <summary>EuroScope display type of the radar screen: "" is the standard radar, else a plugin's display.</summary>
    public string EsDisplayType { get; set; } = "";
    /// <summary>Data the plugins saved into the display (the ASR of EuroScope), name → value.</summary>
    public Dictionary<string, string> EsDisplayData { get; set; } = [];
    /// <summary>Sector the plugin lists above belong to ("" in profiles from before plugins were kept per sector).</summary>
    public string PluginsSector { get; set; } = "";
    /// <summary>Plugins of the other sectors, by sector file: each sector (profile of a division) has its own.</summary>
    public Dictionary<string, SectorPluginSet> SectorPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private SectorPluginSet PluginSnapshot() => new()
    {
        EsPlugins = [.. EsPlugins],
        ImportedPlugins = [.. ImportedPlugins],
        EsDisplayType = EsDisplayType,
        EsDisplayData = new(EsDisplayData),
    };

    /// <summary>
    /// Another sector is opened: the plugins in use are kept for the sector they belong to and the plugins of the
    /// new sector (none if it never had any) take their place. False when nothing changes.
    /// </summary>
    public bool SwitchPluginsTo(string sector)
    {
        if (string.Equals(PluginsSector, sector, StringComparison.OrdinalIgnoreCase)) return false;
        if (PluginsSector.Length == 0)
        {
            // An older profile: its plugins belong to the sector it opens with.
            PluginsSector = sector;
            return false;
        }
        SectorPlugins[PluginsSector] = PluginSnapshot();
        var next = SectorPlugins.GetValueOrDefault(sector) ?? new SectorPluginSet();
        EsPlugins = [.. next.EsPlugins];
        ImportedPlugins = [.. next.ImportedPlugins];
        EsDisplayType = next.EsDisplayType;
        EsDisplayData = new(next.EsDisplayData);
        PluginsSector = sector;
        return true;
    }

    /// <summary>
    /// After a profile import: the plugins now listed belong to <paramref name="sector"/>, and those in use before
    /// the import (<paramref name="before"/>) stay with their own sector.
    /// </summary>
    public void AssignPluginsTo(string sector, Profile before)
    {
        if (before.PluginsSector.Length > 0 && !string.Equals(before.PluginsSector, sector, StringComparison.OrdinalIgnoreCase))
            SectorPlugins[before.PluginsSector] = before.PluginSnapshot();
        SectorPlugins.Remove(sector);
        PluginsSector = sector;
    }
    public bool ShowSectorSelection { get; set; } = true;

    public static Dictionary<string, WindowLayout> DefaultWindows() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["departures"] = new(16, 16, 470, 190, true),
        ["arrivals"] = new(16, 220, 470, 190, true),
        ["traffic"] = new(16, 424, 300, 220, false),
        ["flightplan"] = new(-340, 16, 320, 420, true),   // negative X: from the right edge
        ["atc"] = new(-340, 450, 320, 180, false),
        ["conflicts"] = new(400, 16, 300, 140, false),
        ["messages"] = new(-560, -230, 540, 210, true),   // negative Y: from the bottom edge
        ["sil"] = new(500, 16, 330, 160, false),
        ["sel"] = new(500, 190, 330, 160, false),
    };

    public void RememberSector(string path, string name)
    {
        RecentSectors.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        RecentSectors.Insert(0, new RecentSector { Path = path, Name = name, LastUsed = DateTime.UtcNow });
        if (RecentSectors.Count > 10) RecentSectors.RemoveRange(10, RecentSectors.Count - 10);
    }

    public TagLayouts Tags { get; set; } = new();
    public TargetSettings Targets { get; set; } = new();
    public StationSettings Station { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();
    public PanelSettings Panels { get; set; } = new();
    public VoiceOptions Voice { get; set; } = new();

    public int TransitionAltitude { get; set; } = 10000;
    public double RangeRingSpacingNm { get; set; } = 10;
    public int RangeRingCount { get; set; } = 5;
    public double StcaHorizontalNm { get; set; } = 5;
    public int StcaVerticalFeet { get; set; } = 1000;
    public bool StcaEnabled { get; set; } = true;
    /// <summary>Squawk codes handed out by ".sq" without a code, e.g. "4101-4177".</summary>
    public string SquawkRange { get; set; } = "4101-4177";

    /// <summary>Text shortcuts: ".hi" → "Good day, radar contact".</summary>
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [".rc"] = "radar contact",
        [".ctc"] = "contact $1 on $2, good day",
    };

    /// <summary>Tag field → what left and right clicks on it do (see <see cref="TagActions"/>).</summary>
    public Dictionary<string, TagClickBinding> TagClicks { get; set; } = DefaultTagClicks();

    public static Dictionary<string, TagClickBinding> DefaultTagClicks() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["callsign"] = new(TagActions.ToggleTrack, TagActions.AircraftMenu),
        ["fl"] = new(TagActions.ClearedLevel, TagActions.ClearedLevel),
        ["alt"] = new(TagActions.ClearedLevel, TagActions.ClearedLevel),
        ["cfl"] = new(TagActions.ClearedLevel, TagActions.ClearedLevel),
        ["hdg"] = new(TagActions.Heading, TagActions.Heading),
        ["ahdg"] = new(TagActions.Heading, TagActions.Heading),
        ["gs"] = new(TagActions.Speed, TagActions.Speed),
        ["gs10"] = new(TagActions.Speed, TagActions.Speed),
        ["aspd"] = new(TagActions.Speed, TagActions.Speed),
        ["squawk"] = new(TagActions.Squawk, TagActions.Squawk),
        ["asq"] = new(TagActions.Squawk, TagActions.Squawk),
        ["scratch"] = new(TagActions.Scratchpad, TagActions.Scratchpad),
        ["type"] = new(TagActions.FlightPlan, TagActions.AircraftMenu),
        ["dep"] = new(TagActions.FlightPlan, TagActions.AircraftMenu),
        ["dest"] = new(TagActions.FlightPlan, TagActions.AircraftMenu),
        ["rfl"] = new(TagActions.FlightPlan, TagActions.AircraftMenu),
        ["owner"] = new(TagActions.Handoff, TagActions.Handoff),
        ["ho"] = new(TagActions.Handoff, TagActions.Handoff),
        ["next"] = new(TagActions.Handoff, TagActions.Handoff),
        ["proc"] = new(TagActions.Procedure, TagActions.Procedure),
        ["sid"] = new(TagActions.Procedure, TagActions.Procedure),
        ["star"] = new(TagActions.Procedure, TagActions.Procedure),
        ["rwy"] = new(TagActions.Runway, TagActions.Runway),
        ["clr"] = new(TagActions.Clearance, TagActions.Clearance),
    };

    /// <summary>
    /// Action for a click on a tag field. Fields without a binding: plugin fields with a click handler
    /// run it, everything else selects the aircraft (left) or opens its menu (right).
    /// </summary>
    public string ResolveTagClick(string? field, bool right, bool pluginHandles)
    {
        if (field != null && TagClicks.TryGetValue(field, out var b)) return right ? b.Right : b.Left;
        if (field != null && pluginHandles) return TagActions.Plugin;
        return right ? TagActions.AircraftMenu : TagActions.Select;
    }

    /// <summary>Action → key gesture ("Ctrl+F", "F5", "Add").</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = new(DefaultKeyBindings, StringComparer.OrdinalIgnoreCase);

    /// <summary>Last map view.</summary>
    public double ViewCenterLatitude { get; set; }
    public double ViewCenterLongitude { get; set; }
    public double ViewNmPerPixel { get; set; } = 0.15;

    public static IReadOnlyDictionary<string, string> DefaultKeyBindings { get; } = new Dictionary<string, string>
    {
        ["ZoomIn"] = "Add",
        ["ZoomOut"] = "Subtract",
        ["CenterOnSector"] = "Home",
        ["FocusCommandLine"] = "OemPeriod",
        ["ToggleAircraftList"] = "F2",
        ["ToggleMessages"] = "F3",
        ["ToggleFlightPlan"] = "F4",
        ["ToggleLayers"] = "F6",
        ["OpenSettings"] = "Ctrl+OemComma",
        ["Connect"] = "Ctrl+K",
        ["TrackSelected"] = "F8",
        ["ClearSelection"] = "Escape",
        ["ToggleDepartures"] = "F5",
        ["ToggleArrivals"] = "F7",
        ["ToggleConflicts"] = "F9",
        ["ToggleAtc"] = "F11",
        ["OpenSector"] = "Ctrl+O",
        ["AssumeOrAccept"] = "F12",
        ["HandoffNext"] = "Ctrl+H",
        ["ReleaseOrRefuse"] = "Ctrl+D",
        ["ToggleRoute"] = "Ctrl+T",
        ["ActiveRunways"] = "Ctrl+R",
        ["ToggleSil"] = "Ctrl+I",
        ["ToggleSel"] = "Ctrl+E",
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Network-ATC");

    public bool IsLayerVisible(string layer) => !Layers.TryGetValue(layer, out var v) || v;

    /// <summary>
    /// Version 6: the program is in English. Russian texts that came from the old defaults (untouched by the user)
    /// become their English defaults; anything the user wrote stays as it is.
    /// </summary>
    private static void MigrateToEnglish(Profile p)
    {
        if (p.Tags != null)
        {
            p.Tags.Untracked = p.Tags.Untracked?.Replace("{scratch|+ заметка}", "{scratch|+ scratch}") ?? TagLayouts.DefaultUntracked;
            p.Tags.Tracked = p.Tags.Tracked?.Replace("{scratch|+ заметка}", "{scratch|+ scratch}") ?? TagLayouts.DefaultTracked;
            p.Tags.Detailed = p.Tags.Detailed?.Replace("{scratch|+ заметка}", "{scratch|+ scratch}") ?? TagLayouts.DefaultDetailed;
        }
        if (p.ControllerInfo != null)
            for (int i = 0; i < p.ControllerInfo.Count; i++)
                if (p.ControllerInfo[i] == "Информация $atiscode($myairport)") p.ControllerInfo[i] = "Information $atiscode($myairport)";
        string[] oldAtis =
        [
            "$airport ATIS ИНФОРМАЦИЯ $atiscode($airport) $time",
            "ВПП ВЗЛЁТ $deprwy($airport) ПОСАДКА $arrrwy($airport)",
            "$metar($airport)",
            "СООБЩИТЕ О ПОЛУЧЕНИИ ИНФОРМАЦИИ $atiscode($airport)",
        ];
        foreach (var atis in p.Atis ?? [])
        {
            if (atis == null) continue;
            if (atis.Text != null && atis.Text.SequenceEqual(oldAtis)) atis.Text = [.. NetworkAtc.Core.Atis.AtisSettings.DefaultText];
            // The spoken ATIS is English now: a Russian voice would read it with a Russian accent.
            if (atis.Language == "ru") atis.Language = "en";
        }
    }

    public Profile Clone() => JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(this, Json), Json)!;

    public static Profile Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json) ?? new Profile { Version = CurrentVersion };
                // Keep new default key bindings / layers that older profile files don't have.
                foreach (var (k, v) in DefaultKeyBindings) p.KeyBindings.TryAdd(k, v);
                foreach (var (k, v) in new Profile().Layers) p.Layers.TryAdd(k, v);
                p.TagClicks = new Dictionary<string, TagClickBinding>(p.TagClicks ?? [], StringComparer.OrdinalIgnoreCase);
                p.Windows = new Dictionary<string, WindowLayout>(p.Windows ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in DefaultWindows()) p.Windows.TryAdd(k, v);
                p.ActiveAirports ??= [];
                p.RecentSectors ??= [];
                p.EsPlugins ??= [];
                p.EsDisplayType ??= "";
                p.EsDisplayData ??= [];
                p.PluginsSector ??= "";
                p.SectorPlugins = new Dictionary<string, SectorPluginSet>(p.SectorPlugins ?? [], StringComparer.OrdinalIgnoreCase);
                if (p.Version < 6) MigrateToEnglish(p);
                // Version 7: new default look (Graphite). A profile that kept the old default takes it; own colors stay.
                if (p.Version < 7 && p.Theme != null && Theme.IsVersion6Default(p.Theme)) p.Theme = new Theme();
                // Version 8: IBM Plex Mono comes with the program and is the tag font, unless another was chosen.
                if (p.Version < 8 && p.Tags != null && p.Tags.FontFamily == TagLayouts.OldFontFamily) p.Tags.FontFamily = new TagLayouts().FontFamily;
                if (p.Version < 2)
                {
                    // Version 2 introduced the SkyNetwork look (between Aurora and EuroScope).
                    p.Theme = new Theme();
                }
                if (p.Version < 3)
                {
                    // Version 3: tags gained coordination fields; only untouched default layouts are replaced.
                    p.Tags ??= new TagLayouts();
                    if (p.Tags.Untracked == TagLayouts.OldDefaults[0]) p.Tags.Untracked = TagLayouts.DefaultUntracked;
                    if (p.Tags.Tracked == TagLayouts.OldDefaults[1]) p.Tags.Tracked = TagLayouts.DefaultTracked;
                    if (p.Tags.Detailed == TagLayouts.OldDefaults[2]) p.Tags.Detailed = TagLayouts.DefaultDetailed;
                }
                if (p.Version < 5)
                {
                    // Version 5: EuroScope tags. Untouched default layouts (of any older version) are replaced;
                    // the detailed layout became extra lines under the normal tag.
                    p.Tags ??= new TagLayouts();
                    if (TagLayouts.OldDefaults.Contains(p.Tags.Untracked) || p.Tags.Untracked == TagLayouts.Version3Defaults[0]) p.Tags.Untracked = TagLayouts.DefaultUntracked;
                    if (TagLayouts.OldDefaults.Contains(p.Tags.Tracked) || p.Tags.Tracked == TagLayouts.Version3Defaults[1]) p.Tags.Tracked = TagLayouts.DefaultTracked;
                    if (TagLayouts.OldDefaults.Contains(p.Tags.Detailed) || p.Tags.Detailed == TagLayouts.Version3Defaults[2]) p.Tags.Detailed = TagLayouts.DefaultDetailed;
                    p.Tags.ShowWarnings = true;
                }
                if (p.Version < 4)
                {
                    // Version 4: radio voice. Older profiles have no voice settings: start from the defaults
                    // (voice on, port 3782, primary frequency RX+TX, no push-to-talk key).
                    p.Voice = new VoiceOptions();
                }
                p.Version = CurrentVersion;
                p.Voice ??= new VoiceOptions();
                p.Voice.Frequencies ??= [];
                p.Voice.Frequencies.RemoveAll(f => f == null);
                if (p.Voice.Port is <= 0 or > 65535) p.Voice.Port = VoiceOptions.DefaultPort;
                p.ActiveRunways = new Dictionary<string, Sectors.RunwayUse>(p.ActiveRunways ?? [], StringComparer.OrdinalIgnoreCase);
                p.AtisLetters = new Dictionary<string, string>(p.AtisLetters ?? [], StringComparer.OrdinalIgnoreCase);
                p.ControllerInfo ??= [];
                p.Atis ??= [];
                p.Atis.RemoveAll(a => a == null);
                p.Sounds ??= new SoundSettings();
                foreach (var (k, v) in DefaultTagClicks()) p.TagClicks.TryAdd(k, v);
                return p;
            }
        }
        catch (JsonException)
        {
            // Corrupt profile: start from defaults.
        }
        return new Profile { Version = CurrentVersion };
    }

    public void Save(string path)
    {
        Version = CurrentVersion;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static Theme LoadTheme(string path) =>
        JsonSerializer.Deserialize<Theme>(File.ReadAllText(path), Json) ?? new Theme();

    public static void SaveTheme(Theme theme, string path) => File.WriteAllText(path, JsonSerializer.Serialize(theme, Json));
}

/// <summary>The EuroScope plugins of one sector and what they keep in the display.</summary>
public sealed class SectorPluginSet
{
    public List<string> EsPlugins { get; set; } = [];
    public List<string> ImportedPlugins { get; set; } = [];
    public string EsDisplayType { get; set; } = "";
    public Dictionary<string, string> EsDisplayData { get; set; } = [];
}
