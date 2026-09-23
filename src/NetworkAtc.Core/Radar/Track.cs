using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Geo;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Radar;

/// <summary>An aircraft on the radar: last reports, history dots, flight plan and controller annotations.</summary>
public sealed class Track(string callsign) : IAircraft
{
    public const int HistoryLength = 12;

    private readonly LinkedList<(DateTime Time, GeoPoint Position)> _history = new();
    private DateTime _lastAltitudeTime;
    private int _lastAltitude;

    public string Callsign { get; } = callsign;
    public GeoPoint Position { get; private set; }
    public int Altitude { get; private set; }
    public int PressureAltitude { get; private set; }
    public int GroundSpeed { get; private set; }
    public double Heading { get; private set; }
    public int VerticalSpeed { get; private set; }
    public int Squawk { get; private set; }
    public bool ModeC { get; private set; }
    public bool Ident { get; private set; }
    public bool OnGround { get; private set; }
    public DateTime LastUpdate { get; private set; }

    public string AircraftType { get; set; } = "";
    public string Departure { get; private set; } = "";
    public string Destination { get; private set; } = "";
    public string Alternate { get; private set; } = "";
    public string Route { get; private set; } = "";
    public string FiledAltitude { get; private set; } = "";
    public string Rules { get; private set; } = "";
    public int FiledSpeed { get; private set; }
    public string Remarks { get; private set; } = "";
    public bool HasFlightPlan { get; private set; }

    public int? ClearedAltitude { get; set; }
    public int? AssignedHeading { get; set; }
    public int? AssignedSpeed { get; set; }
    public int? AssignedSquawk { get; set; }
    public string Scratchpad { get; set; } = "";
    public bool IsTracked { get; set; }

    /// <summary>Color set by a plugin, or null.</summary>
    public string? Highlight { get; set; }

    /// <summary>Oldest first.</summary>
    public IReadOnlyCollection<(DateTime Time, GeoPoint Position)> History => _history;

    public void Update(PilotReport r, DateTime now)
    {
        if (LastUpdate != default && GeoMath.DistanceNm(Position, r.Position) > 0.01)
        {
            _history.AddLast((LastUpdate, Position));
            while (_history.Count > HistoryLength) _history.RemoveFirst();
        }
        if (_lastAltitudeTime != default)
        {
            double minutes = (now - _lastAltitudeTime).TotalMinutes;
            if (minutes > 0.05) VerticalSpeed = (int)Math.Round((r.Altitude - _lastAltitude) / minutes / 100) * 100;
        }
        _lastAltitude = r.Altitude;
        _lastAltitudeTime = now;

        Position = r.Position;
        Altitude = r.Altitude;
        PressureAltitude = r.PressureAltitude;
        GroundSpeed = r.GroundSpeed;
        Heading = r.Heading;
        Squawk = r.Squawk;
        ModeC = r.ModeC;
        Ident = r.Ident;
        OnGround = r.OnGround;
        LastUpdate = now;
    }

    public void ApplyFlightPlan(FiledPlan fp)
    {
        HasFlightPlan = true;
        Rules = fp.Rules;
        if (fp.AircraftType.Length > 0) AircraftType = fp.AircraftType;
        Departure = fp.Departure;
        Destination = fp.Destination;
        Alternate = fp.Alternate;
        Route = fp.Route;
        FiledAltitude = fp.Altitude;
        FiledSpeed = fp.TrueAirspeed;
        Remarks = fp.Remarks;
    }

    /// <summary>Where the aircraft will be after <paramref name="minutes"/> on its current track and speed.</summary>
    public GeoPoint Predict(double minutes) =>
        GroundSpeed < 30 ? Position : GeoMath.Offset(Position, Heading, GroundSpeed * minutes / 60);

    /// <summary>ICAO wake category guessed from the type code (H, M, L, J).</summary>
    public char WakeCategory => AircraftType.ToUpperInvariant() switch
    {
        "A388" => 'J',
        var t when t.StartsWith("B74") || t.StartsWith("B77") || t.StartsWith("B78") || t.StartsWith("A33") ||
                   t.StartsWith("A34") || t.StartsWith("A35") || t.StartsWith("IL96") || t.StartsWith("AN12") => 'H',
        var t when t.StartsWith("C1") || t.StartsWith("PA") || t.StartsWith("DA4") || t.StartsWith("SR2") => 'L',
        "" => '-',
        _ => 'M',
    };
}
