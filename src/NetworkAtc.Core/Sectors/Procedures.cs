using System.Text.RegularExpressions;
using NetworkAtc.Core.Radar;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Sectors;

/// <summary>Runways in use at one airport, as set in the active runway dialog or with .rwy.</summary>
public sealed class RunwayUse
{
    public List<string> Departure { get; set; } = [];
    public List<string> Arrival { get; set; } = [];
}

/// <summary>A route point resolved against the sector's fixes, navaids and airports.</summary>
public sealed record RoutePoint(string Name, GeoPoint Position);

/// <summary>
/// Picks runways, SIDs and STARs the way EuroScope does: the first active runway of the airport, and
/// the procedure for that runway whose name is in the route or whose end (SID) / start (STAR) fix is.
/// </summary>
public sealed partial class ProcedureAssigner(Func<SectorFile?> sector, Func<IReadOnlyDictionary<string, RunwayUse>> runways)
{
    [GeneratedRegex(@"^[A-Z]{2,5}\d{0,2}$")]
    private static partial Regex WaypointToken();

    /// <summary>Route text split into points and airways: "N0450F350 SHR DCT DEMO5/N0440F360 UM100" → SHR DCT DEMO5 UM100.</summary>
    public static IReadOnlyList<string> Tokens(string route) =>
        route.ToUpperInvariant()
            .Split(new[] { ' ', '.', '+' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Split('/')[0])
            .Where(t => t.Length > 0 && !Regex.IsMatch(t, @"^[NKM]\d{3,4}[FASM]\d{3,4}$"))
            .ToList();

    public string? DepartureRunway(Track t) =>
        t.DepartureRunway.Length > 0 ? t.DepartureRunway
        : runways().TryGetValue(t.Departure, out var use) && use.Departure.Count > 0 ? use.Departure[0] : null;

    public string? ArrivalRunway(Track t) =>
        t.ArrivalRunway.Length > 0 ? t.ArrivalRunway
        : runways().TryGetValue(t.Destination, out var use) && use.Arrival.Count > 0 ? use.Arrival[0] : null;

    public IEnumerable<Procedure> Sids(string airport, string? runway) => Procedures(ProcedureKind.Sid, airport, runway);

    public IEnumerable<Procedure> Stars(string airport, string? runway) => Procedures(ProcedureKind.Star, airport, runway);

    private IEnumerable<Procedure> Procedures(ProcedureKind kind, string airport, string? runway) =>
        sector()?.Procedures.Where(p => p.Kind == kind && p.Airport.Equals(airport, StringComparison.OrdinalIgnoreCase) &&
                                        (runway == null || p.Runway.Equals(runway, StringComparison.OrdinalIgnoreCase)))
        ?? [];

    /// <summary>Assigned SID, or the one EuroScope would suggest; null when none fits.</summary>
    public string? Sid(Track t)
    {
        if (t.Sid.Length > 0) return t.Sid;
        if (!t.HasFlightPlan) return null;
        var tokens = Tokens(t.Route);
        var candidates = Sids(t.Departure, DepartureRunway(t)).ToList();
        var byName = candidates.FirstOrDefault(p => tokens.Contains(p.Name));
        if (byName != null) return byName.Name;
        // The SID whose last fix comes first in the route.
        return candidates
            .Select(p => (p.Name, Index: p.Route.Count == 0 ? -1 : IndexOf(tokens, p.Route[^1])))
            .Where(x => x.Index >= 0)
            .OrderBy(x => x.Index)
            .Select(x => x.Name)
            .FirstOrDefault();
    }

    /// <summary>Assigned STAR, or the one EuroScope would suggest; null when none fits.</summary>
    public string? Star(Track t)
    {
        if (t.Star.Length > 0) return t.Star;
        if (!t.HasFlightPlan) return null;
        var tokens = Tokens(t.Route);
        var candidates = Stars(t.Destination, ArrivalRunway(t)).ToList();
        var byName = candidates.FirstOrDefault(p => tokens.Contains(p.Name));
        if (byName != null) return byName.Name;
        // The STAR whose first fix comes last in the route.
        return candidates
            .Select(p => (p.Name, Index: p.Route.Count == 0 ? -1 : LastIndexOf(tokens, p.Route[0])))
            .Where(x => x.Index >= 0)
            .OrderByDescending(x => x.Index)
            .Select(x => x.Name)
            .FirstOrDefault();
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    private static int LastIndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i] == value) return i;
        return -1;
    }

    /// <summary>
    /// The flight plan route as points known to the sector file: departure airport, SID fixes, route
    /// waypoints, STAR fixes, destination. Airways and unknown names are skipped.
    /// </summary>
    public IReadOnlyList<RoutePoint> ResolveRoute(Track t)
    {
        var s = sector();
        if (s == null) return [];
        var names = new List<string>();
        if (t.Departure.Length > 0) names.Add(t.Departure);
        if (Sid(t) is { } sid && Sids(t.Departure, null).FirstOrDefault(p => p.Name == sid) is { } sp) names.AddRange(sp.Route);
        names.AddRange(Tokens(t.Route).Where(x => WaypointToken().IsMatch(x) && x != "DCT"));
        if (Star(t) is { } star && Stars(t.Destination, null).FirstOrDefault(p => p.Name == star) is { } st) names.AddRange(st.Route);
        if (t.Destination.Length > 0) names.Add(t.Destination);

        var result = new List<RoutePoint>();
        GeoPoint? last = null;
        foreach (var n in names)
        {
            if (result.Count > 0 && result[^1].Name == n) continue;
            if (Find(s, n, last ?? t.Position) is { } p)
            {
                result.Add(new RoutePoint(n, p));
                last = p;
            }
        }
        return result;
    }

    /// <summary>A named point; when several share the name, the one nearest to <paramref name="near"/>.</summary>
    public static GeoPoint? Find(SectorFile s, string name, GeoPoint near)
    {
        GeoPoint? best = null;
        double bestDistance = double.MaxValue;
        foreach (var list in new[] { s.Airports, s.Fixes, s.Vors, s.Ndbs })
            foreach (var p in list)
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    double d = Geo.GeoMath.DistanceNm(near, p.Position);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = p.Position;
                    }
                }
        return best;
    }
}
