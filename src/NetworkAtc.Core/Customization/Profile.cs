using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Tags;

namespace NetworkAtc.Core.Customization;

public sealed class TagLayouts
{
    /// <summary>Aircraft nobody works.</summary>
    public string Untracked { get; set; } = "{callsign}\n{fl}{vs}";
    /// <summary>Aircraft the controller works.</summary>
    public string Tracked { get; set; } = "{callsign} {wtc}\n{fl}{vs} {cfl|---}\n{gs10} {dest} {ahdg} {aspd}";
    /// <summary>Selected or hovered aircraft.</summary>
    public string Detailed { get; set; } = "{callsign} {type}/{wtc} {squawk}\n{fl}{vs} {cfl|CFL} {rfl}\n{gs} {ahdg|HDG} {aspd|SPD} {dest}\n{scratch|+ заметка}";
    public double FontSize { get; set; } = 11;
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas";
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

public sealed class ConnectionSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6809;
    public int Cid { get; set; }
    public string ProtectedPassword { get; set; } = "";
    public string RealName { get; set; } = "";
}

public sealed class PanelSettings
{
    public bool ShowAircraftList { get; set; } = true;
    public bool ShowMessages { get; set; } = true;
    public bool ShowControllers { get; set; } = true;
    public bool ShowFlightPlan { get; set; } = true;
    public double SidePanelWidth { get; set; } = 300;
    public double MessagesHeight { get; set; } = 170;
    public double UiScale { get; set; } = 1.0;
    public double UiFontSize { get; set; } = 12.5;
}

/// <summary>
/// A complete workspace: look, layers, tags, keys, station and connection. Profiles are JSON files
/// in the profiles folder; the user can keep several (e.g. "Tower", "Radar", "Light").
/// </summary>
public sealed class Profile
{
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

    public TagLayouts Tags { get; set; } = new();
    public TargetSettings Targets { get; set; } = new();
    public StationSettings Station { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();
    public PanelSettings Panels { get; set; } = new();

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
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Network-ATC");

    public bool IsLayerVisible(string layer) => !Layers.TryGetValue(layer, out var v) || v;

    public Profile Clone() => JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(this, Json), Json)!;

    public static Profile Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json) ?? new Profile();
                // Keep new default key bindings / layers that older profile files don't have.
                foreach (var (k, v) in DefaultKeyBindings) p.KeyBindings.TryAdd(k, v);
                foreach (var (k, v) in new Profile().Layers) p.Layers.TryAdd(k, v);
                p.TagClicks = new Dictionary<string, TagClickBinding>(p.TagClicks ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in DefaultTagClicks()) p.TagClicks.TryAdd(k, v);
                return p;
            }
        }
        catch (JsonException)
        {
            // Corrupt profile: start from defaults.
        }
        return new Profile();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static Theme LoadTheme(string path) =>
        JsonSerializer.Deserialize<Theme>(File.ReadAllText(path), Json) ?? new Theme();

    public static void SaveTheme(Theme theme, string path) => File.WriteAllText(path, JsonSerializer.Serialize(theme, Json));
}
