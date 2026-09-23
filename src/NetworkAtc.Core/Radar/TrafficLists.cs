using NetworkAtc.Core.Geo;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Radar;

public sealed record DepartureRow(Track Track, string Callsign, string Type, string Destination, string Rfl, string Squawk, string Status);

public sealed record ArrivalRow(Track Track, string Callsign, string Type, string Departure, string Destination, double DistanceNm, int? EtaMinutes);

/// <summary>
/// EuroScope-style departure and arrival lists for the controller's active airports.
/// </summary>
public static class TrafficLists
{
    /// <summary>Departures stay in the list until they are this far from the airport.</summary>
    public const double DepartureRadiusNm = 40;

    public static IReadOnlyList<DepartureRow> Departures(IEnumerable<Track> tracks, IReadOnlyCollection<string> activeAirports, SectorFile? sector)
    {
        var rows = new List<DepartureRow>();
        foreach (var t in tracks)
        {
            if (!t.HasFlightPlan || !Contains(activeAirports, t.Departure)) continue;
            var airport = FindAirport(sector, t.Departure);
            if (!t.OnGround && airport != null && t.LastUpdate != default &&
                GeoMath.DistanceNm(airport.Value, t.Position) > DepartureRadiusNm)
                continue;
            string status = t.LastUpdate == default ? "нет связи" : t.OnGround ? (t.GroundSpeed > 30 ? "разбег" : t.GroundSpeed > 3 ? "руление" : "стоянка") : "взлёт";
            rows.Add(new DepartureRow(t, t.Callsign, t.AircraftType, t.Destination, t.FiledAltitude,
                (t.AssignedSquawk ?? t.Squawk).ToString("0000"), status));
        }
        return rows.OrderBy(r => r.Status == "стоянка").ThenBy(r => r.Callsign).ToList();
    }

    public static IReadOnlyList<ArrivalRow> Arrivals(IEnumerable<Track> tracks, IReadOnlyCollection<string> activeAirports, SectorFile? sector)
    {
        var rows = new List<ArrivalRow>();
        foreach (var t in tracks)
        {
            if (!t.HasFlightPlan || !Contains(activeAirports, t.Destination) || t.LastUpdate == default) continue;
            if (t.OnGround && t.GroundSpeed < 40 && Contains(activeAirports, t.Departure)) continue; // still waiting to depart
            var airport = FindAirport(sector, t.Destination);
            double dist = airport == null ? double.NaN : GeoMath.DistanceNm(t.Position, airport.Value);
            int? eta = !double.IsNaN(dist) && t.GroundSpeed > 50 && !t.OnGround ? (int)Math.Round(dist / t.GroundSpeed * 60) : null;
            rows.Add(new ArrivalRow(t, t.Callsign, t.AircraftType, t.Departure, t.Destination, dist, eta));
        }
        return rows.OrderBy(r => double.IsNaN(r.DistanceNm) ? double.MaxValue : r.DistanceNm).ToList();
    }

    private static bool Contains(IReadOnlyCollection<string> airports, string icao) =>
        icao.Length > 0 && airports.Any(a => a.Equals(icao, StringComparison.OrdinalIgnoreCase));

    private static GeoPoint? FindAirport(SectorFile? sector, string icao) =>
        sector?.Airports.FirstOrDefault(a => a.Name.Equals(icao, StringComparison.OrdinalIgnoreCase))?.Position;
}
