using System.Globalization;
using System.Text.RegularExpressions;
using NetworkAtc.Core.Geo;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Import;

/// <summary>
/// Custom map layers built from plugin data files. They are kept in a <see cref="SectorFile"/> (lines by
/// layer name, layer colors and labels grouped by layer), the same way .natc sectors keep their own
/// map layers, so they can be merged into the loaded sector and saved with it.
/// </summary>
public sealed class MapLayerBuilder(SectorFile target, IReadOnlyDictionary<string, GeoPoint> points)
{
    public SectorFile Target => target;
    /// <summary>Fix names that are not in the sector file.</summary>
    public HashSet<string> UnknownPoints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Layer(string layer, string? color)
    {
        if (!target.Lines.ContainsKey(layer)) target.Lines[layer] = [];
        if (color != null) target.LayerColors.TryAdd(layer, color);
    }

    public void Polyline(string layer, IReadOnlyList<GeoPoint> pts, string? color, string name = "", bool closed = false)
    {
        if (pts.Count < 2) return;
        Layer(layer, color);
        var lines = target.Lines[layer];
        for (int i = 1; i < pts.Count; i++) lines.Add(new SectorLine(name, pts[i - 1], pts[i], color));
        if (closed && pts.Count > 2 && pts[^1] != pts[0]) lines.Add(new SectorLine(name, pts[^1], pts[0], color));
    }

    public void Circle(string layer, GeoPoint center, double radiusNm, string? color, string name = "", int segments = 36)
    {
        if (radiusNm <= 0) return;
        var pts = Enumerable.Range(0, segments + 1).Select(i => GeoMath.Offset(center, i * 360.0 / segments, radiusNm)).ToList();
        Polyline(layer, pts, color, name);
    }

    /// <summary>A small cross (about 100 m), for symbols.</summary>
    public void Cross(string layer, GeoPoint at, string? color, string name = "")
    {
        const double arm = 0.05;
        Polyline(layer, [GeoMath.Offset(at, 0, arm), GeoMath.Offset(at, 180, arm)], color, name);
        Polyline(layer, [GeoMath.Offset(at, 90, arm), GeoMath.Offset(at, 270, arm)], color, name);
    }

    public void Label(string layer, string text, GeoPoint at, string? color)
    {
        if (text.Trim().Length == 0) return;
        Layer(layer, color);
        target.Labels.Add(new SectorLabel(text.Trim(), at, color, layer));
    }

    /// <summary>
    /// Reads a point at <paramref name="i"/>: a "lat:lon" pair (N055.58.20.000:E037.24.53.000 or decimal
    /// degrees) or the name of a fix, VOR, NDB or airport from the sector file.
    /// </summary>
    public bool TryPoint(IReadOnlyList<string> t, ref int i, out GeoPoint p)
    {
        p = default;
        if (i >= t.Count) return false;
        if (i + 1 < t.Count && SectorParser.TryParseCoordinate(t[i], out var lat) && SectorParser.TryParseCoordinate(t[i + 1], out var lon))
        {
            p = new GeoPoint(lat, lon);
            i += 2;
            return true;
        }
        string name = t[i].Trim();
        if (name.Length > 0 && points.TryGetValue(name, out p))
        {
            i++;
            return true;
        }
        if (name.Length > 0 && !SectorParser.TryParseCoordinate(name, out _)) UnknownPoints.Add(name);
        return false;
    }

    public List<GeoPoint> Points(IReadOnlyList<string> t, int start)
    {
        var result = new List<GeoPoint>();
        int i = start;
        while (i < t.Count && TryPoint(t, ref i, out var p)) result.Add(p);
        return result;
    }

    /// <summary>Named points of a sector file, for data files that refer to fixes by name.</summary>
    public static Dictionary<string, GeoPoint> PointsOf(SectorFile? sector)
    {
        var points = new Dictionary<string, GeoPoint>(StringComparer.OrdinalIgnoreCase);
        if (sector == null) return points;
        foreach (var p in sector.Fixes.Concat(sector.Ndbs).Concat(sector.Vors).Concat(sector.Airports)) points[p.Name] = p.Position;
        return points;
    }
}

/// <summary>What a map file gave.</summary>
public sealed class MapFileStats
{
    public int Maps { get; set; }
    public int Active { get; set; }
    /// <summary>Maps shown only under a condition (runway, schedule, controller…), imported switched off.</summary>
    public int Conditional { get; set; }
    public HashSet<string> UnknownKeywords { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// TopSky map files (TopSkyMaps.txt; the Ground Radar plugin maps use the same syntax):
/// FOLDER:name, MAP:name, COLORDEF:name:r:g:b, COLOR:name, ACTIVE:1, LINE:p1:p2, COORD:p, COORDLINE,
/// COORDPOLY:n, CIRCLE:p:radius nm, TEXT:p:text, SYMBOL:symbol:p[:label]. A point is "lat:lon" or a fix name.
/// Each map becomes a layer "prefix · folder · map".
/// </summary>
public static class TopSkyMaps
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "ZOOM", "STYLE", "LAYER", "FONTSIZE", "FONTSTYLE", "TEXTALIGN", "ASRDATA", "OVERRIDE_SCT_MAP", "OVERRIDESCTMAP", "SCREEN_SPECIFIC",
        "GLOBAL", "HIDEMENU", "CATEGORY", "SYMBOLDEF", "MOVETO", "LINETO", "SETPIXEL", "ARC", "ELLIPSE_CIRCLE", "ELLIPTIC_ARC",
        "FILLARC", "POLYGON", "SYMBOLSIZE", "AIRPORT", "RUNWAY", "SIDSTAR", "DISPLAYTYPE",
    };

    /// <summary>Colors named in TopSkySettings.txt: "Color_Active_Map=160,160,160" → "Active_Map" → "#A0A0A0".</summary>
    public static Dictionary<string, string> SettingsColors(string text)
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in EsNames.Lines(text))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            if (!key.StartsWith("Color_", StringComparison.OrdinalIgnoreCase)) continue;
            if (Rgb(line[(eq + 1)..].Split(',', ':')) is { } c) colors[key[6..]] = c;
        }
        return colors;
    }

    internal static string? Rgb(IReadOnlyList<string> f) =>
        f.Count >= 3 && EsNames.TryInt(f[0], out var r) && EsNames.TryInt(f[1], out var g) && EsNames.TryInt(f[2], out var b)
            ? $"#{Math.Clamp(r, 0, 255):X2}{Math.Clamp(g, 0, 255):X2}{Math.Clamp(b, 0, 255):X2}" : null;

    public static MapFileStats Parse(string text, string prefix, MapLayerBuilder maps, IReadOnlyDictionary<string, string> namedColors,
        Dictionary<string, bool> visibility)
    {
        var stats = new MapFileStats();
        var colors = new Dictionary<string, string>(namedColors, StringComparer.OrdinalIgnoreCase);
        string folder = "";
        string? layer = null, color = null;
        var coords = new List<GeoPoint>();

        void Flush(bool closed)
        {
            if (layer != null) maps.Polyline(layer, coords, color, closed: closed);
            coords = [];
        }

        foreach (var line in EsNames.Lines(text))
        {
            var f = line.Trim().Split(':');
            string key = f[0].Trim().ToUpperInvariant();
            switch (key)
            {
                case "FOLDER":
                    Flush(false);
                    folder = string.Join(':', f.Skip(1)).Trim();
                    break;
                case "MAP":
                    Flush(false);
                    string name = string.Join(':', f.Skip(1)).Trim();
                    layer = string.Join(" · ", new[] { prefix, folder, name }.Where(x => x.Length > 0));
                    color = null;
                    stats.Maps++;
                    maps.Layer(layer, null);
                    visibility.TryAdd(layer, false);
                    break;
                case "COLORDEF" when f.Length >= 5:
                    if (Rgb(f[2..]) is { } def) colors[f[1].Trim()] = def;
                    break;
                case "COLOR" when f.Length >= 2:
                    Flush(false);
                    color = Rgb(f[1..]) ?? colors.GetValueOrDefault(f[1].Trim()) ?? color;
                    if (layer != null && color != null) maps.Target.LayerColors.TryAdd(layer, color);
                    break;
                case "ACTIVE" when layer != null:
                    string cond = f.Length > 1 ? f[1].Trim() : "";
                    if (cond == "1") visibility[layer] = true;
                    else if (cond != "0") stats.Conditional++;
                    break;
                case "LINE" when layer != null:
                    maps.Polyline(layer, maps.Points(f, 1), color);
                    break;
                case "COORD" when layer != null:
                    int ci = 1;
                    if (maps.TryPoint(f, ref ci, out var cp)) coords.Add(cp);
                    break;
                case "COORDLINE":
                    Flush(false);
                    break;
                case "COORDPOLY":
                    Flush(true);
                    break;
                case "CIRCLE" when layer != null:
                    int i = 1;
                    if (maps.TryPoint(f, ref i, out var center) && i < f.Length && EsNames.TryDouble(f[i], out var radius))
                        maps.Circle(layer, center, radius, color);
                    break;
                case "TEXT" when layer != null:
                    int ti = 1;
                    if (maps.TryPoint(f, ref ti, out var at)) maps.Label(layer, string.Join(':', f.Skip(ti)), at, color);
                    break;
                case "SYMBOL" when layer != null && f.Length >= 3:
                    int si = 2;
                    if (maps.TryPoint(f, ref si, out var sp))
                    {
                        maps.Cross(layer, sp, color, f[1].Trim());
                        if (si < f.Length) maps.Label(layer, f[si], sp, color);
                    }
                    break;
                default:
                    if (!Ignored.Contains(key) && key.Length > 0 && f.Length > 0 && key.All(c => char.IsLetter(c) || c == '_'))
                        stats.UnknownKeywords.Add(key);
                    break;
            }
        }
        Flush(false);
        stats.Active = visibility.Count(kv => kv.Value && kv.Key.StartsWith(prefix + " · ", StringComparison.OrdinalIgnoreCase));
        return stats;
    }
}

/// <summary>
/// TopSky areas file (TopSkyAreas.txt): restricted and danger areas.
/// AREA:id[:name], CATEGORY:name, LIMITS:lower:upper (hundreds of feet), LABEL:p, COORD:p, CIRCLE:p:radius nm.
/// Each category becomes a layer "prefix · Зоны · category"; the area outline is labelled "id lower–upper".
/// </summary>
public static class TopSkyAreas
{
    private sealed class Area
    {
        public string Name = "";
        public string Category = "";
        public string Limits = "";
        public GeoPoint? LabelAt;
        public readonly List<GeoPoint> Coords = [];
        public readonly List<(GeoPoint Center, double Radius)> Circles = [];
    }

    public static int Parse(string text, string prefix, MapLayerBuilder maps, IReadOnlyDictionary<string, string> namedColors,
        Dictionary<string, bool> visibility)
    {
        var areas = new List<Area>();
        var categoryColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string defaultCategory = "";
        Area? area = null;
        foreach (var line in EsNames.Lines(text))
        {
            var f = line.Trim().Split(':');
            switch (f[0].Trim().ToUpperInvariant())
            {
                case "CATEGORYDEF" when f.Length >= 2:
                    // CATEGORYDEF:name:r:g:b… or a named color.
                    string? c = f.Length >= 5 ? TopSkyMaps.Rgb(f[2..]) : f.Length >= 3 ? namedColors.GetValueOrDefault(f[2].Trim()) : null;
                    if (c != null) categoryColors[f[1].Trim()] = c;
                    break;
                case "AREA" when f.Length >= 2:
                    area = new Area { Name = f[1].Trim(), Category = defaultCategory };
                    areas.Add(area);
                    break;
                case "CATEGORY" when f.Length >= 2:
                    if (area != null && area.Coords.Count == 0 && area.Circles.Count == 0) area.Category = f[1].Trim();
                    else defaultCategory = f[1].Trim();
                    break;
                case "LIMITS" when area != null && f.Length >= 3:
                    area.Limits = $"{f[1].Trim()}–{f[2].Trim()}";
                    break;
                case "LABEL" when area != null:
                    int li = 1;
                    if (maps.TryPoint(f, ref li, out var lp)) area.LabelAt = lp;
                    break;
                case "COORD" when area != null:
                    int ci = 1;
                    if (maps.TryPoint(f, ref ci, out var cp)) area.Coords.Add(cp);
                    break;
                case "CIRCLE" when area != null:
                    int i = 1;
                    if (maps.TryPoint(f, ref i, out var center) && i < f.Length && EsNames.TryDouble(f[i], out var r)) area.Circles.Add((center, r));
                    break;
            }
        }

        int count = 0;
        foreach (var a in areas)
        {
            if (a.Coords.Count < 3 && a.Circles.Count == 0) continue;
            string layer = $"{prefix} · Зоны" + (a.Category.Length > 0 ? " · " + a.Category : "");
            string? color = categoryColors.GetValueOrDefault(a.Category);
            maps.Polyline(layer, a.Coords, color, a.Name, closed: true);
            foreach (var (center, radius) in a.Circles) maps.Circle(layer, center, radius, color, a.Name);
            var at = a.LabelAt ?? (a.Coords.Count > 0
                ? new GeoPoint(a.Coords.Average(p => p.Latitude), a.Coords.Average(p => p.Longitude))
                : a.Circles[0].Center);
            maps.Label(layer, a.Limits.Length > 0 ? $"{a.Name} {a.Limits}" : a.Name, at, color);
            visibility.TryAdd(layer, false);
            count++;
        }
        return count;
    }
}

/// <summary>
/// Ground Radar plugin stands (GRpluginStands.txt): "STAND:UUEE:24:N055.58.20.000:E037.24.53.000:20"
/// (airport, stand, position, radius in metres), optionally followed by COORD lines with the stand outline;
/// WTC, USE, AREA and other attribute lines are ignored. Each airport becomes a layer "Стоянки UUEE".
/// </summary>
public static class GroundRadarStands
{
    public static Dictionary<string, int> Parse(string text, MapLayerBuilder maps, Dictionary<string, bool> visibility)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? layer = null, name = null;
        GeoPoint center = default;
        double radiusM = 0;
        var outline = new List<GeoPoint>();

        void Flush()
        {
            if (layer == null || name == null) return;
            if (outline.Count >= 3) maps.Polyline(layer, outline, null, name, closed: true);
            else maps.Circle(layer, center, (radiusM > 0 ? radiusM : 15) / 1852.0, null, name, 12);
            maps.Label(layer, name, center, null);
            outline = [];
            name = null;
        }

        foreach (var line in EsNames.Lines(text))
        {
            var f = line.Trim().Split(':');
            switch (f[0].Trim().ToUpperInvariant())
            {
                case "STAND" when f.Length >= 4:
                    Flush();
                    int i = 3;
                    if (!maps.TryPoint(f, ref i, out center)) break;
                    string airport = f[1].Trim().ToUpperInvariant();
                    layer = "Стоянки " + airport;
                    name = f[2].Trim();
                    radiusM = i < f.Length && EsNames.TryDouble(f[i], out var r) ? r : 0;
                    counts[airport] = counts.GetValueOrDefault(airport) + 1;
                    visibility.TryAdd(layer, true);
                    break;
                case "COORD" when name != null:
                    int ci = 1;
                    if (maps.TryPoint(f, ref ci, out var p)) outline.Add(p);
                    break;
            }
        }
        Flush();
        return counts;
    }
}

/// <summary>CCAMS squawk assignment: the first "4101-4177" style range found in its local files.</summary>
public static partial class CcamsConfig
{
    [GeneratedRegex(@"\b([0-7]{4})\s*-\s*([0-7]{4})\b")]
    private static partial Regex RangeRegex();

    public static string? FindRange(string text)
    {
        foreach (Match m in RangeRegex().Matches(text))
        {
            int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (a < b && a != 0) return $"{m.Groups[1].Value}-{m.Groups[2].Value}";
        }
        return null;
    }
}
