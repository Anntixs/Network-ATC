using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Sectors;

public sealed record NamedPoint(string Name, GeoPoint Position, string Frequency = "");

public sealed record Runway(string Airport, string Id1, string Id2, int Heading1, int Heading2, GeoPoint End1, GeoPoint End2);

/// <summary>A line segment from a sector file section ([ARTCC], [SID], [GEO]...); Color is "#RRGGBB" or null.</summary>
public sealed record SectorLine(string Name, GeoPoint From, GeoPoint To, string? Color);

public sealed record Region(string Name, string? Color, IReadOnlyList<GeoPoint> Points);

public sealed record SectorLabel(string Text, GeoPoint Position, string? Color, string Group = "");

/// <summary>A controller position from the .ese [POSITIONS] section.</summary>
public sealed record AtcPosition(string Callsign, string RadioName, string Frequency, string Identifier, string Prefix, string Suffix,
    int? SquawkStart = null, int? SquawkEnd = null);

public enum ProcedureKind { Sid, Star }

/// <summary>A SID or STAR from the .ese [SIDSSTARS] section: SID:UUEE:24R:DEMO1A:SHR DEMO5.</summary>
public sealed record Procedure(ProcedureKind Kind, string Airport, string Runway, string Name, IReadOnlyList<string> Route);

/// <summary>An airspace sector from the .ese [AIRSPACE] section.</summary>
public sealed record AirspaceSector(string Name, int Floor, int Ceiling, IReadOnlyList<string> Owners, IReadOnlyList<string> BorderLines);

/// <summary>Everything loaded from a EuroScope .sct (and optional .ese) file.</summary>
public sealed class SectorFile
{
    public string Name { get; set; } = "";
    public string DefaultCallsign { get; set; } = "";
    public string DefaultAirport { get; set; } = "";
    public GeoPoint Center { get; set; }
    public double MagneticVariation { get; set; }

    public Dictionary<string, string> Colors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<NamedPoint> Vors { get; } = [];
    public List<NamedPoint> Ndbs { get; } = [];
    public List<NamedPoint> Airports { get; } = [];
    public List<NamedPoint> Fixes { get; } = [];
    public List<Runway> Runways { get; } = [];

    /// <summary>Line layers by section name: ARTCC, ARTCC HIGH, ARTCC LOW, SID, STAR, LOW AIRWAY, HIGH AIRWAY, GEO.</summary>
    public Dictionary<string, List<SectorLine>> Lines { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Default color of a map layer (native sectors), used when a line has no color of its own.</summary>
    public Dictionary<string, string> LayerColors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Region> Regions { get; } = [];
    public List<SectorLabel> Labels { get; } = [];

    // .ese
    public List<AtcPosition> Positions { get; } = [];
    public List<SectorLabel> FreeTexts { get; } = [];
    public Dictionary<string, List<GeoPoint>> SectorLines { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AirspaceSector> Sectors { get; } = [];
    public List<Procedure> Procedures { get; } = [];

    /// <summary>Non-fatal problems found while parsing (line number and reason).</summary>
    public List<string> Warnings { get; } = [];

    public IEnumerable<SectorLine> AllLines => Lines.Values.SelectMany(l => l);

    /// <summary>The EuroScope line sections, in drawing order.</summary>
    public static IReadOnlyList<string> StandardLayers { get; } =
        ["GEO", "LOW AIRWAY", "HIGH AIRWAY", "ARTCC LOW", "ARTCC HIGH", "ARTCC", "SID", "STAR"];

    /// <summary>Map layers that are not EuroScope sections (only in Network-ATC sectors).</summary>
    public IEnumerable<string> CustomLayers => Lines.Keys.Where(k => !StandardLayers.Contains(k, StringComparer.OrdinalIgnoreCase));
}
