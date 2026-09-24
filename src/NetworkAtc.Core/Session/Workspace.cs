using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Geo;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Weather;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Session;

/// <summary>A row of the sector inbound (SIL) or exit (SEL) list.</summary>
public sealed record SectorListRow(Track Track, string Callsign, string Other, string Level, string Info);

/// <summary>
/// The controller's working context: loaded sector, sector ownership, runway configuration, SID/STAR
/// logic and weather. Tag fields, commands and lists ask it the EuroScope questions: is this aircraft
/// mine, who is next, which SID, what state is the tag in.
/// </summary>
public sealed class Workspace : IDisposable
{
    private readonly AtcSession _session;
    private readonly Func<Profile> _profile;
    private SectorFile? _sector;
    private SectorOwnership? _ownership;

    public Workspace(AtcSession session, Func<Profile> profile)
    {
        _session = session;
        _profile = profile;
        Procedures = new ProcedureAssigner(() => _sector, () => _profile().ActiveRunways);
        Weather = new WeatherService(() => _profile().ActiveAirports);
        _session.ControllersChanged += (_, _) => UpdateOwnership();
        _session.ConnectionChanged += (_, _) => UpdateOwnership();
    }

    public AtcSession Session => _session;
    public Profile Profile => _profile();
    public SectorFile? Sector => _sector;
    public SectorOwnership? Ownership => _ownership;
    public ProcedureAssigner Procedures { get; }
    public WeatherService Weather { get; }

    public void SetSector(SectorFile? sector)
    {
        _sector = sector;
        _ownership = sector == null ? null : new SectorOwnership(sector);
        UpdateOwnership();
    }

    /// <summary>Recomputes sector owners from the controllers online and ourselves.</summary>
    public void UpdateOwnership()
    {
        if (_ownership == null) return;
        var online = _session.Controllers.Select(c => new OnlineStation(c.Callsign, Frequency.Format(c.FrequencyKhz))).ToList();
        online.Add(new OnlineStation(_session.Me, _session.Info is { } i ? Frequency.Format(i.FrequencyKhz) : _profile().Station.Frequency));
        _ownership.Update(online);
    }

    /// <summary>Short name of a controller: its .ese identifier ("EA") or the callsign.</summary>
    public string ShortName(string callsign)
    {
        if (callsign.Length == 0) return "";
        var freq = _session.Controllers.FirstOrDefault(c => c.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase)) is { } c
            ? Frequency.Format(c.FrequencyKhz) : "";
        return _ownership?.PositionOf(new OnlineStation(callsign, freq))?.Identifier is { Length: > 0 } id ? id : callsign;
    }

    /// <summary>The controller the aircraft should go to next, or null.</summary>
    public string? NextController(Track t) => _ownership?.NextController(t, t.Owner.Length > 0 ? t.Owner : _session.Me);

    /// <summary>In or entering our airspace, or departing/arriving at one of our active airports.</summary>
    public bool IsConcerned(Track t)
    {
        var airports = _profile().ActiveAirports;
        if (t.HasFlightPlan)
        {
            if (airports.Contains(t.Departure, StringComparer.OrdinalIgnoreCase) && NearAirport(t, t.Departure, 40)) return true;
            if (airports.Contains(t.Destination, StringComparer.OrdinalIgnoreCase) && NearAirport(t, t.Destination, 60)) return true;
        }
        return _ownership?.IsInOrEntering(t, _session.Me) == true;
    }

    private bool NearAirport(Track t, string icao, double nm) =>
        AirportPosition(icao) is { } p && GeoMath.DistanceNm(p, t.Position) <= nm;

    /// <summary>Airport reference point from the sector's [AIRPORT] list (checked every frame, so no fix/navaid search).</summary>
    private GeoPoint? AirportPosition(string icao) =>
        icao.Length == 0 ? null : _sector?.Airports.FirstOrDefault(a => a.Name.Equals(icao, StringComparison.OrdinalIgnoreCase))?.Position;

    public TrackState StateOf(Track t) => TrackStates.Of(t, _session.Me, IsConcerned(t));

    public TrackWarning WarningOf(Track t) => Warnings.Of(t, _session.Tracks, _profile().ClamFeet);

    /// <summary>SID for departures from our airports, STAR for arrivals, otherwise whichever exists.</summary>
    public string? ProcedureOf(Track t)
    {
        var airports = _profile().ActiveAirports;
        bool departing = airports.Contains(t.Departure, StringComparer.OrdinalIgnoreCase);
        bool arriving = airports.Contains(t.Destination, StringComparer.OrdinalIgnoreCase);
        if (departing && (!arriving || DistanceTo(t, t.Departure) < DistanceTo(t, t.Destination))) return Procedures.Sid(t);
        if (arriving) return Procedures.Star(t);
        return Procedures.Sid(t) ?? Procedures.Star(t);
    }

    /// <summary>Departure runway for departures from our airports, otherwise the arrival runway.</summary>
    public string? RunwayOf(Track t)
    {
        var airports = _profile().ActiveAirports;
        bool departing = airports.Contains(t.Departure, StringComparer.OrdinalIgnoreCase);
        bool arriving = airports.Contains(t.Destination, StringComparer.OrdinalIgnoreCase);
        if (departing && (!arriving || DistanceTo(t, t.Departure) < DistanceTo(t, t.Destination))) return Procedures.DepartureRunway(t);
        return arriving ? Procedures.ArrivalRunway(t) : Procedures.DepartureRunway(t) ?? Procedures.ArrivalRunway(t);
    }

    private double DistanceTo(Track t, string icao) =>
        AirportPosition(icao) is { } p ? GeoMath.DistanceNm(p, t.Position) : double.MaxValue;

    /// <summary>Squawk range: the position's own range from the .ese, else the profile's.</summary>
    public (int From, int To)? SquawkRange()
    {
        if (_ownership?.PositionOf(new OnlineStation(_session.Me, _profile().Station.Frequency)) is { SquawkStart: { } a, SquawkEnd: { } b })
            return (a, b);
        var parts = _profile().SquawkRange.Split('-');
        return parts.Length == 2 && CommandProcessor.IsSquawk(parts[0].Trim()) && CommandProcessor.IsSquawk(parts[1].Trim())
            ? (int.Parse(parts[0].Trim()), int.Parse(parts[1].Trim()))
            : null;
    }

    private string Level(Track t, Func<int, string> format) =>
        format(t.Altitude) + (t.ClearedAltitude is { } c ? " → " + format(c) : "");

    /// <summary>
    /// Sector inbound list: aircraft offered to us, and aircraft not ours that are entering our
    /// airspace within 10 minutes (with the time to entry).
    /// </summary>
    public IReadOnlyList<SectorListRow> InboundList(Func<int, string> formatLevel)
    {
        var rows = new List<(SectorListRow Row, double Order)>();
        foreach (var t in _session.Tracks)
        {
            if (t.LastUpdate == default || t.OnGround || t.IsTracked) continue;
            var state = StateOf(t);
            double? entry = state == TrackState.TransferToMe ? 0 : MinutesToEntry(t);
            if (entry == null) continue;
            string other = t.Owner.Length > 0 ? ShortName(t.Owner) : "—";
            string info = state == TrackState.TransferToMe ? "ПЕРЕДАЧА" : entry < 0.5 ? "в секторе" : $"через {entry:0} мин";
            rows.Add((new SectorListRow(t, t.Callsign, other, Level(t, formatLevel), info), state == TrackState.TransferToMe ? -1 : entry.Value));
        }
        return rows.OrderBy(r => r.Order).ThenBy(r => r.Row.Callsign).Select(r => r.Row).ToList();
    }

    /// <summary>Sector exit list: our aircraft with the controller they go to next and the transfer state.</summary>
    public IReadOnlyList<SectorListRow> OutboundList(Func<int, string> formatLevel)
    {
        var rows = new List<SectorListRow>();
        foreach (var t in _session.Tracks)
        {
            if (t.LastUpdate == default || !t.IsTracked) continue;
            string other, info;
            if (t.HandoffPending)
            {
                other = "→" + ShortName(t.HandoffTo);
                info = "ПЕРЕДАН";
            }
            else
            {
                other = NextController(t) is { } n ? "»" + ShortName(n) : "—";
                info = t.ClearanceReceived ? "CLR" : "";
            }
            rows.Add(new SectorListRow(t, t.Callsign, other, Level(t, formatLevel), info));
        }
        return rows.OrderByDescending(r => r.Info == "ПЕРЕДАН").ThenBy(r => r.Callsign).ToList();
    }

    /// <summary>Minutes until the aircraft enters airspace we own (0 when already inside), or null.</summary>
    public double? MinutesToEntry(Track t, int lookahead = 10)
    {
        if (_ownership == null) return null;
        int alt = t.ClearedAltitude ?? t.Altitude;
        for (int m = 0; m <= lookahead; m++)
            if (string.Equals(_ownership.OwnerAt(t.Predict(m), m == 0 ? t.Altitude : alt), _session.Me, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    public void Dispose() => Weather.Dispose();
}
