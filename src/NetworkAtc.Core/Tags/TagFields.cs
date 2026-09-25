using System.Globalization;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Tags;

public sealed record TagField(string Key, string Description, Func<IAircraft, string> Value);

/// <summary>All fields usable in tag templates: built-in ones plus those registered by plugins.</summary>
public sealed class TagFields
{
    private readonly Dictionary<string, TagField> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Altitudes at or above this are shown as flight levels.</summary>
    public int TransitionAltitude { get; set; } = 10000;

    /// <summary>Source of the coordination fields (owner, next, SID/STAR, warnings); set by the application.</summary>
    public Session.Workspace? Workspace { get; set; }

    private string WithTrack(IAircraft a, Func<Radar.Track, Session.Workspace, string?> f) =>
        a is Radar.Track t && Workspace is { } w ? f(t, w) ?? "" : "";

    public TagFields()
    {
        Add("callsign", "Callsign", a => a.Callsign);
        Add("type", "Aircraft type", a => a.AircraftType);
        Add("wtc", "Wake turbulence category", a => a is Radar.Track t ? t.WakeCategory.ToString() : "");
        Add("alt", "Altitude in hundreds of feet (350)", a => Hundreds(a.Altitude));
        Add("fl", "Flight level or altitude (F350 / A045)", a => FlightLevel(a.Altitude));
        Add("vs", "Climb/descent arrow", a => a.VerticalSpeed > 300 ? "↑" : a.VerticalSpeed < -300 ? "↓" : "");
        Add("vsfpm", "Vertical speed, ft/min", a => a.VerticalSpeed == 0 ? "" : a.VerticalSpeed.ToString("+0;-0", CultureInfo.InvariantCulture));
        Add("gs", "Ground speed, knots", a => a.GroundSpeed.ToString(CultureInfo.InvariantCulture));
        Add("gs10", "Ground speed / 10", a => (a.GroundSpeed / 10).ToString("00", CultureInfo.InvariantCulture));
        Add("hdg", "Heading", a => ((int)Math.Round(a.Heading) % 360).ToString("000", CultureInfo.InvariantCulture));
        Add("squawk", "Squawk", a => a.Squawk.ToString("0000", CultureInfo.InvariantCulture));
        Add("dep", "Departure airport", a => a.Departure);
        Add("dest", "Destination airport", a => a.Destination);
        Add("rfl", "Requested flight level", a => a.FiledAltitude);
        Add("cfl", "Cleared flight level", a => a.ClearedAltitude is { } c ? FlightLevel(c) : "");
        Add("ahdg", "Assigned heading", a => a.AssignedHeading is { } h ? "H" + h.ToString("000", CultureInfo.InvariantCulture) : "");
        Add("aspd", "Assigned speed", a => a.AssignedSpeed is { } s ? "S" + s : "");
        Add("asq", "Assigned squawk", a => a.AssignedSquawk is { } q && q != a.Squawk ? q.ToString("0000", CultureInfo.InvariantCulture) : "");
        Add("scratch", "Scratchpad", a => a.Scratchpad);
        Add("ident", "IDENT", a => a.Ident ? "ID" : "");
        Add("modec", "Transponder mode (empty if Mode C)", a => a.ModeC ? "" : "STBY");
        Add("owner", "Tracking controller (position ID)", a => WithTrack(a, (t, w) => t.Owner.Length == 0 ? "" : w.ShortName(t.Owner)));
        Add("ho", "Handoff: to / from (→EA, ←DC)", a => WithTrack(a, (t, w) =>
            !t.HandoffPending ? ""
            : t.HandoffFrom.Equals(w.Session.Me, StringComparison.OrdinalIgnoreCase) ? "→" + w.ShortName(t.HandoffTo)
            : "←" + w.ShortName(t.HandoffFrom)));
        Add("next", "Next controller by sector", a => WithTrack(a, (t, w) => w.NextController(t) is { } n ? "»" + w.ShortName(n) : ""));
        Add("proc", "SID for departures / STAR for arrivals", a => WithTrack(a, (t, w) => w.ProcedureOf(t)));
        Add("sid", "SID", a => WithTrack(a, (t, w) => w.Procedures.Sid(t)));
        Add("star", "STAR", a => WithTrack(a, (t, w) => w.Procedures.Star(t)));
        Add("rwy", "Runway (departure runway for departures, arrival runway for arrivals)", a => WithTrack(a, (t, w) => w.RunwayOf(t)));
        Add("drwy", "Departure runway", a => WithTrack(a, (t, w) => w.Procedures.DepartureRunway(t)));
        Add("arwy", "Arrival runway", a => WithTrack(a, (t, w) => w.Procedures.ArrivalRunway(t)));
        Add("clr", "Clearance received flag", a => a is Radar.Track { ClearanceReceived: true } ? "✓" : "");
        Add("gstate", "Ground state (PUSH/TAXI/DEPA)", a => a is Radar.Track t ? t.GroundState : "");
        Add("comm", "Voice capability: /r receive only, /t text only", a => a is Radar.Track t && t.CommType.Length > 0 ? "/" + t.CommType.ToLowerInvariant() : "");
        Add("warn", "Warnings: EMERG, DUPE, SQ, CLAM", a => WithTrack(a, (t, w) => Radar.Warnings.Text(t, w.WarningOf(t))));
        Add("rules", "Flight rules (I/V)", a => a is Radar.Track t ? t.Rules : "");
    }

    public IEnumerable<TagField> All => _fields.Values.OrderBy(f => f.Key);

    public void Add(string key, string description, Func<IAircraft, string> value) =>
        _fields[key] = new TagField(key, description, value);

    public void Remove(string key) => _fields.Remove(key);

    public string? Resolve(string key, IAircraft aircraft)
    {
        if (!_fields.TryGetValue(key, out var f)) return null;
        try { return f.Value(aircraft) ?? ""; }
        catch (Exception) { return "?"; } // a broken plugin field must not break the radar
    }

    public string FlightLevel(int feet) => feet >= TransitionAltitude ? "F" + Hundreds(feet) : "A" + Hundreds(feet);

    private static string Hundreds(int feet) => Math.Max(0, (int)Math.Round(feet / 100.0)).ToString("000", CultureInfo.InvariantCulture);
}
