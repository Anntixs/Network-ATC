using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.Json.Serialization;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Sectors;

/// <summary>
/// Network-ATC's own sector format (.natc): one JSON file with the same data as a EuroScope
/// .sct + .ese pair, but with free-form map layers (any name, color and polylines) instead of the
/// fixed EuroScope sections. EuroScope sectors can be converted with <see cref="Save"/>.
/// </summary>
public static class NativeSector
{
    public const string Extension = ".natc";
    public const string FormatId = "network-atc-sector";
    public const int Version = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Keep Cyrillic and other letters readable in the file.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static bool IsNativeFile(string path) => Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    public static SectorFile Load(string path) => Parse(File.ReadAllText(path));

    public static SectorFile Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<Document>(json, Json) ?? throw new InvalidDataException("Пустой файл сектора");
        if (doc.Format != FormatId) throw new InvalidDataException("Это не сектор Network-ATC");
        if (doc.Version > Version) throw new InvalidDataException($"Сектор версии {doc.Version} новее программы (поддерживается {Version})");

        var s = new SectorFile
        {
            Name = doc.Info?.Name ?? "",
            DefaultCallsign = doc.Info?.DefaultCallsign ?? "",
            DefaultAirport = doc.Info?.DefaultAirport ?? "",
            Center = ToPoint(doc.Info?.Center),
            MagneticVariation = doc.Info?.MagneticVariation ?? 0,
        };
        foreach (var (k, v) in doc.Colors ?? []) s.Colors[k] = v;
        s.Vors.AddRange((doc.Vors ?? []).Select(ToNamed));
        s.Ndbs.AddRange((doc.Ndbs ?? []).Select(ToNamed));
        s.Airports.AddRange((doc.Airports ?? []).Select(ToNamed));
        s.Fixes.AddRange((doc.Fixes ?? []).Select(ToNamed));
        s.Runways.AddRange((doc.Runways ?? []).Select(r =>
            new Runway(r.Airport ?? "", r.Id1 ?? "", r.Id2 ?? "", r.Heading1, r.Heading2, ToPoint(r.End1), ToPoint(r.End2))));
        foreach (var map in doc.Maps ?? [])
        {
            if (string.IsNullOrWhiteSpace(map.Name)) continue;
            if (!s.Lines.TryGetValue(map.Name, out var lines)) s.Lines[map.Name] = lines = [];
            if (map.Color != null) s.LayerColors[map.Name] = map.Color;
            foreach (var pl in map.Lines ?? [])
            {
                var pts = (pl.Points ?? []).Select(ToPoint).ToList();
                for (int i = 1; i < pts.Count; i++) lines.Add(new SectorLine(pl.Name ?? "", pts[i - 1], pts[i], pl.Color));
            }
        }
        s.Regions.AddRange((doc.Regions ?? []).Where(r => r.Points is { Count: >= 3 })
            .Select(r => new Region(r.Name ?? "", r.Color, r.Points!.Select(ToPoint).ToList())));
        s.Labels.AddRange((doc.Labels ?? []).Select(l => new SectorLabel(l.Text ?? "", ToPoint(l.At), l.Color, l.Group ?? "")));
        s.FreeTexts.AddRange((doc.FreeTexts ?? []).Select(l => new SectorLabel(l.Text ?? "", ToPoint(l.At), l.Color, l.Group ?? "")));
        s.Positions.AddRange((doc.Positions ?? []).Select(p =>
            new AtcPosition(p.Callsign ?? "", p.RadioName ?? "", p.Frequency ?? "", p.Identifier ?? "", p.Prefix ?? "", p.Suffix ?? "")));
        foreach (var (id, pts) in doc.SectorLines ?? []) s.SectorLines[id] = pts.Select(ToPoint).ToList();
        s.Sectors.AddRange((doc.Sectors ?? []).Select(a => new AirspaceSector(a.Name ?? "", a.Floor, a.Ceiling, a.Owners ?? [], a.Borders ?? [])));
        if (s.Center == default && s.Airports.Count > 0) s.Center = s.Airports[0].Position;
        return s;
    }

    /// <summary>Write a sector (e.g. one loaded from EuroScope files) in the Network-ATC format.</summary>
    public static void Save(SectorFile s, string path) => File.WriteAllText(path, Serialize(s));

    public static string Serialize(SectorFile s)
    {
        var doc = new Document
        {
            Format = FormatId,
            Version = Version,
            Info = new InfoDto
            {
                Name = s.Name, DefaultCallsign = s.DefaultCallsign, DefaultAirport = s.DefaultAirport,
                Center = FromPoint(s.Center), MagneticVariation = s.MagneticVariation,
            },
            Colors = s.Colors.Count > 0 ? new Dictionary<string, string>(s.Colors) : null,
            Vors = s.Vors.Select(FromNamed).ToList(),
            Ndbs = s.Ndbs.Select(FromNamed).ToList(),
            Airports = s.Airports.Select(FromNamed).ToList(),
            Fixes = s.Fixes.Select(FromNamed).ToList(),
            Runways = s.Runways.Select(r => new RunwayDto
            {
                Airport = r.Airport, Id1 = r.Id1, Id2 = r.Id2, Heading1 = r.Heading1, Heading2 = r.Heading2,
                End1 = FromPoint(r.End1), End2 = FromPoint(r.End2),
            }).ToList(),
            Maps = s.Lines.Select(kv => new MapDto
            {
                Name = kv.Key,
                Color = s.LayerColors.GetValueOrDefault(kv.Key),
                Lines = JoinSegments(kv.Value),
            }).ToList(),
            Regions = s.Regions.Select(r => new RegionDto { Name = r.Name, Color = r.Color, Points = r.Points.Select(FromPoint).ToList() }).ToList(),
            Labels = s.Labels.Select(ToLabel).ToList(),
            FreeTexts = s.FreeTexts.Select(ToLabel).ToList(),
            Positions = s.Positions.Select(p => new PositionDto
            {
                Callsign = p.Callsign, RadioName = p.RadioName, Frequency = p.Frequency, Identifier = p.Identifier, Prefix = p.Prefix, Suffix = p.Suffix,
            }).ToList(),
            SectorLines = s.SectorLines.ToDictionary(kv => kv.Key, kv => kv.Value.Select(FromPoint).ToList()),
            Sectors = s.Sectors.Select(a => new AirspaceDto
            {
                Name = a.Name, Floor = a.Floor, Ceiling = a.Ceiling, Owners = a.Owners.ToList(), Borders = a.BorderLines.ToList(),
            }).ToList(),
        };
        return JsonSerializer.Serialize(doc, Json);
    }

    /// <summary>Consecutive segments with the same name and color that touch become one polyline.</summary>
    internal static List<PolylineDto> JoinSegments(IEnumerable<SectorLine> segments)
    {
        var result = new List<PolylineDto>();
        PolylineDto? current = null;
        GeoPoint last = default;
        foreach (var seg in segments)
        {
            if (current != null && current.Name == seg.Name && current.Color == seg.Color && last == seg.From)
            {
                current.Points!.Add(FromPoint(seg.To));
            }
            else
            {
                current = new PolylineDto { Name = seg.Name.Length > 0 ? seg.Name : null, Color = seg.Color, Points = [FromPoint(seg.From), FromPoint(seg.To)] };
                result.Add(current);
            }
            last = seg.To;
        }
        return result;
    }

    private static NamedPoint ToNamed(PointDto p) => new(p.Name ?? "", new GeoPoint(p.Lat, p.Lon), p.Frequency ?? "");
    private static PointDto FromNamed(NamedPoint p) =>
        new() { Name = p.Name, Lat = Round(p.Position.Latitude), Lon = Round(p.Position.Longitude), Frequency = p.Frequency.Length > 0 ? p.Frequency : null };
    private static GeoPoint ToPoint(double[]? p) => p is { Length: >= 2 } ? new GeoPoint(p[0], p[1]) : default;
    private static double[] FromPoint(GeoPoint p) => [Round(p.Latitude), Round(p.Longitude)];
    private static double Round(double v) => Math.Round(v, 6);
    private static LabelDto ToLabel(SectorLabel l) =>
        new() { Text = l.Text, At = FromPoint(l.Position), Color = l.Color, Group = l.Group.Length > 0 ? l.Group : null };

    // ---- file structure ----------------------------------------------------------------------

    internal sealed class Document
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public InfoDto? Info { get; set; }
        public Dictionary<string, string>? Colors { get; set; }
        public List<PointDto>? Vors { get; set; }
        public List<PointDto>? Ndbs { get; set; }
        public List<PointDto>? Airports { get; set; }
        public List<PointDto>? Fixes { get; set; }
        public List<RunwayDto>? Runways { get; set; }
        public List<MapDto>? Maps { get; set; }
        public List<RegionDto>? Regions { get; set; }
        public List<LabelDto>? Labels { get; set; }
        public List<LabelDto>? FreeTexts { get; set; }
        public List<PositionDto>? Positions { get; set; }
        public Dictionary<string, List<double[]>>? SectorLines { get; set; }
        public List<AirspaceDto>? Sectors { get; set; }
    }

    internal sealed class InfoDto
    {
        public string? Name { get; set; }
        public string? DefaultCallsign { get; set; }
        public string? DefaultAirport { get; set; }
        public double[]? Center { get; set; }
        public double MagneticVariation { get; set; }
    }

    internal sealed class PointDto
    {
        public string? Name { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string? Frequency { get; set; }
    }

    internal sealed class RunwayDto
    {
        public string? Airport { get; set; }
        public string? Id1 { get; set; }
        public string? Id2 { get; set; }
        public int Heading1 { get; set; }
        public int Heading2 { get; set; }
        public double[]? End1 { get; set; }
        public double[]? End2 { get; set; }
    }

    internal sealed class MapDto
    {
        public string? Name { get; set; }
        public string? Color { get; set; }
        public List<PolylineDto>? Lines { get; set; }
    }

    internal sealed class PolylineDto
    {
        public string? Name { get; set; }
        public string? Color { get; set; }
        public List<double[]>? Points { get; set; }
    }

    internal sealed class RegionDto
    {
        public string? Name { get; set; }
        public string? Color { get; set; }
        public List<double[]>? Points { get; set; }
    }

    internal sealed class LabelDto
    {
        public string? Text { get; set; }
        public double[]? At { get; set; }
        public string? Color { get; set; }
        public string? Group { get; set; }
    }

    internal sealed class PositionDto
    {
        public string? Callsign { get; set; }
        public string? RadioName { get; set; }
        public string? Frequency { get; set; }
        public string? Identifier { get; set; }
        public string? Prefix { get; set; }
        public string? Suffix { get; set; }
    }

    internal sealed class AirspaceDto
    {
        public string? Name { get; set; }
        public int Floor { get; set; }
        public int Ceiling { get; set; }
        public List<string>? Owners { get; set; }
        public List<string>? Borders { get; set; }
    }
}

/// <summary>Opens either format by extension.</summary>
public static class SectorLoader
{
    public static SectorFile Load(string path) =>
        NativeSector.IsNativeFile(path) ? NativeSector.Load(path) : SectorParser.LoadFiles(path);

    public static string Describe(string path) =>
        NativeSector.IsNativeFile(path) ? "Network-ATC" : "EuroScope";
}
