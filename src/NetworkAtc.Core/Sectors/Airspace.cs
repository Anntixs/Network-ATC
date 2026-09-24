using NetworkAtc.Core.Geo;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Sectors;

/// <summary>An .ese sector with its border as a closed polygon.</summary>
public sealed record SectorArea(AirspaceSector Sector, IReadOnlyList<GeoPoint> Polygon)
{
    public bool Contains(GeoPoint p, int altitudeFeet) =>
        altitudeFeet >= Sector.Floor && altitudeFeet < Sector.Ceiling && Airspace.InPolygon(p, Polygon);
}

/// <summary>A controller that is online (or ourselves) as the sector logic sees it.</summary>
public sealed record OnlineStation(string Callsign, string Frequency);

public static class Airspace
{
    /// <summary>
    /// Chains the BORDER lines of a sector into a polygon: each next line is appended in the direction
    /// whose start is nearest to the current end, the way EuroScope expects sector lines to meet.
    /// </summary>
    public static IReadOnlyList<GeoPoint> Polygon(AirspaceSector sector, SectorFile file)
    {
        var lines = sector.BorderLines
            .Select(n => file.SectorLines.TryGetValue(n, out var l) ? l : null)
            .Where(l => l is { Count: > 0 })
            .Select(l => (IReadOnlyList<GeoPoint>)l!)
            .ToList();
        var result = new List<GeoPoint>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            bool reverse;
            if (i == 0)
            {
                // Orient the first line so that its end meets the second one.
                reverse = lines.Count > 1 && NearestEnd(line[0], lines[1]) < NearestEnd(line[^1], lines[1]);
            }
            else
            {
                var end = result[^1];
                reverse = GeoMath.DistanceNm(end, line[^1]) < GeoMath.DistanceNm(end, line[0]);
            }
            foreach (var p in reverse ? line.Reverse() : line)
                if (result.Count == 0 || GeoMath.DistanceNm(result[^1], p) > 0.01) result.Add(p);
        }
        if (result.Count > 1 && GeoMath.DistanceNm(result[0], result[^1]) < 0.01) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static double NearestEnd(GeoPoint p, IReadOnlyList<GeoPoint> line) =>
        Math.Min(GeoMath.DistanceNm(p, line[0]), GeoMath.DistanceNm(p, line[^1]));

    public static IReadOnlyList<SectorArea> Areas(SectorFile file) =>
        file.Sectors.Select(s => new SectorArea(s, Polygon(s, file))).Where(a => a.Polygon.Count >= 3).ToList();

    /// <summary>Ray casting in the latitude/longitude plane (fine for sector-sized polygons).</summary>
    public static bool InPolygon(GeoPoint p, IReadOnlyList<GeoPoint> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Latitude > p.Latitude) != (b.Latitude > p.Latitude) &&
                p.Longitude < (b.Longitude - a.Longitude) * (p.Latitude - a.Latitude) / (b.Latitude - a.Latitude) + a.Longitude)
                inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// Who controls which sector right now: for every .ese sector the first position of its OWNER list
/// that is online owns it (EuroScope's top-down rule).
/// </summary>
public sealed class SectorOwnership
{
    private readonly SectorFile _file;
    private readonly IReadOnlyList<SectorArea> _areas;
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);

    public SectorOwnership(SectorFile file)
    {
        _file = file;
        _areas = Airspace.Areas(file);
    }

    public IReadOnlyList<SectorArea> Areas => _areas;

    /// <summary>Sector name → owner callsign, after the last <see cref="Update"/>.</summary>
    public IReadOnlyDictionary<string, string> Owners => _owners;

    /// <summary>The .ese position a callsign is logged in as (exact callsign, or prefix+suffix and frequency).</summary>
    public AtcPosition? PositionOf(OnlineStation station)
    {
        var cs = station.Callsign.ToUpperInvariant();
        var exact = _file.Positions.FirstOrDefault(p => p.Callsign.Equals(cs, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        int us = cs.IndexOf('_');
        if (us < 0) return null;
        string prefix = cs[..us], suffix = cs[(cs.LastIndexOf('_') + 1)..];
        return _file.Positions.FirstOrDefault(p =>
            p.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase) && p.Suffix.Equals(suffix, StringComparison.OrdinalIgnoreCase) &&
            (station.Frequency.Length == 0 || SameFrequency(p.Frequency, station.Frequency)));
    }

    private static bool SameFrequency(string a, string b) =>
        double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
        double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) &&
        Math.Abs(x - y) < 0.003;

    public void Update(IEnumerable<OnlineStation> online)
    {
        var byIdentifier = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in online)
            if (PositionOf(st) is { } pos) byIdentifier.TryAdd(pos.Identifier, st.Callsign);
        _owners.Clear();
        foreach (var area in _areas)
            foreach (var id in area.Sector.Owners)
                if (byIdentifier.TryGetValue(id, out var cs))
                {
                    _owners[area.Sector.Name] = cs;
                    break;
                }
    }

    /// <summary>Smallest sector containing the point at that altitude, or null.</summary>
    public SectorArea? AreaAt(GeoPoint p, int altitudeFeet) =>
        _areas.Where(a => a.Contains(p, altitudeFeet)).MinBy(a => a.Sector.Ceiling - a.Sector.Floor);

    public string? OwnerAt(GeoPoint p, int altitudeFeet) =>
        AreaAt(p, altitudeFeet) is { } a && _owners.TryGetValue(a.Sector.Name, out var cs) ? cs : null;

    /// <summary>
    /// The first controller other than <paramref name="current"/> whose airspace the aircraft reaches in
    /// the next <paramref name="lookaheadMinutes"/> minutes on its present track (at its cleared level if set).
    /// </summary>
    public string? NextController(Radar.Track t, string? current, int lookaheadMinutes = 20)
    {
        int alt = t.ClearedAltitude ?? t.Altitude;
        for (int m = 0; m <= lookaheadMinutes; m++)
        {
            var owner = OwnerAt(t.Predict(m), m == 0 ? t.Altitude : alt);
            if (owner != null && !owner.Equals(current, StringComparison.OrdinalIgnoreCase)) return owner;
        }
        return null;
    }

    /// <summary>True when the aircraft is in, or within <paramref name="lookaheadMinutes"/> of, airspace owned by <paramref name="callsign"/>.</summary>
    public bool IsInOrEntering(Radar.Track t, string callsign, int lookaheadMinutes = 10)
    {
        int alt = t.ClearedAltitude ?? t.Altitude;
        for (int m = 0; m <= lookaheadMinutes; m++)
            if (string.Equals(OwnerAt(t.Predict(m), m == 0 ? t.Altitude : alt), callsign, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
