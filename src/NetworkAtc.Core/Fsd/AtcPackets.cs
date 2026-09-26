using System.Globalization;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Fsd;

public enum Facility
{
    Observer = 0,
    FlightService = 1,
    Delivery = 2,
    Ground = 3,
    Tower = 4,
    Approach = 5,
    Centre = 6,
}

public sealed record PilotReport(
    string Callsign, bool ModeC, bool Ident, int Squawk, GeoPoint Position, int Altitude, int GroundSpeed,
    double Heading, bool OnGround, int PressureAltitude);

public sealed record AtcReport(string Callsign, int FrequencyKhz, Facility Facility, int VisualRange, GeoPoint Position);

public sealed record FiledPlan(
    string Callsign, string Rules, string AircraftType, int TrueAirspeed, string Departure, string DepartureTime,
    string Altitude, string Destination, string Alternate, string Remarks, string Route,
    string EnrouteHours = "0", string EnrouteMinutes = "0", string FuelHours = "0", string FuelMinutes = "0");

/// <summary>Builds and parses the FSD packets a controller client uses.</summary>
public static class AtcPackets
{
    public const int ProtocolRevision = 100;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Clean(string text) => text.Replace(':', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public static string Login(string callsign, string realName, int cid, string password, int rating) =>
        $"#AA{callsign}:SERVER:{Clean(realName)}:{cid}:{Clean(password)}:{rating}:{ProtocolRevision}";

    public static string Logoff(string callsign, int cid) => $"#DA{callsign}:{cid}";

    /// <summary>%&lt;cs&gt;:&lt;freq&gt;:&lt;facility&gt;:&lt;range&gt;:&lt;rating&gt;:&lt;lat&gt;:&lt;lon&gt;:0 — frequency 118.100 is sent as 18100.</summary>
    public static string Position(string callsign, int frequencyKhz, Facility facility, int visualRange, int rating, GeoPoint at) =>
        string.Create(Inv, $"%{callsign}:{frequencyKhz - 100000}:{(int)facility}:{visualRange}:{rating}:{at.Latitude:0.000000}:{at.Longitude:0.000000}:0");

    public static string TextMessage(string from, string to, string text) => $"#TM{from}:{to}:{Clean(text)}";

    public static string RequestFlightPlan(string from, string callsign) => $"$CQ{from}:SERVER:FP:{callsign}";

    /// <summary>A supervisor command to the server: KILL, WARN, FIND, WHOIS, STAFF, ONLINE.</summary>
    public static string StaffCommand(string from, string command, params string[] args) =>
        $"$CQ{from}:SERVER:{command}" + string.Concat(args.Select(a => ":" + Clean(a)));

    public static string PlaneInfoRequest(string from, string to) => $"#SB{from}:{to}:PIR";

    public static PilotReport? ParsePilot(FsdPacket p)
    {
        if (p.Command != "@" || p.Fields.Length < 9) return null;
        if (!double.TryParse(p[4], NumberStyles.Float, Inv, out var lat) ||
            !double.TryParse(p[5], NumberStyles.Float, Inv, out var lon) ||
            !int.TryParse(p[6], NumberStyles.Integer, Inv, out var alt) ||
            !int.TryParse(p[7], NumberStyles.Integer, Inv, out var gs) ||
            !uint.TryParse(p[8], NumberStyles.Integer, Inv, out var pbhWord))
            return null;
        int.TryParse(p[2], NumberStyles.Integer, Inv, out var squawk);
        int.TryParse(p[9], NumberStyles.Integer, Inv, out var pressureDelta);
        var pbh = Pbh.Decode(pbhWord);
        return new PilotReport(p[1], p[0] is "N" or "Y", p[0] == "Y", squawk, new GeoPoint(lat, lon), alt, gs,
            pbh.Heading, pbh.OnGround, alt + pressureDelta);
    }

    public static AtcReport? ParseAtc(FsdPacket p)
    {
        if (p.Command != "%" || p.Fields.Length < 7) return null;
        if (!int.TryParse(p[1], NumberStyles.Integer, Inv, out var freq) ||
            !int.TryParse(p[2], NumberStyles.Integer, Inv, out var facility) ||
            !int.TryParse(p[3], NumberStyles.Integer, Inv, out var range) ||
            !double.TryParse(p[5], NumberStyles.Float, Inv, out var lat) ||
            !double.TryParse(p[6], NumberStyles.Float, Inv, out var lon))
            return null;
        return new AtcReport(p[0], freq + 100000, (Facility)Math.Clamp(facility, 0, 6), range, new GeoPoint(lat, lon));
    }

    /// <summary>$FP&lt;cs&gt;:&lt;to&gt;:&lt;rules&gt;:&lt;type&gt;:&lt;tas&gt;:&lt;dep&gt;:&lt;deptime&gt;:&lt;actdep&gt;:&lt;alt&gt;:&lt;dest&gt;:&lt;h&gt;:&lt;m&gt;:&lt;fh&gt;:&lt;fm&gt;:&lt;altn&gt;:&lt;remarks&gt;:&lt;route&gt;</summary>
    public static FiledPlan? ParseFlightPlan(FsdPacket p)
    {
        if (p.Command != "$FP" || p.Fields.Length < 17) return null;
        int.TryParse(p[4], NumberStyles.Integer, Inv, out var tas);
        return new FiledPlan(p[0], p[2], p[3], tas, p[5], p[6], p[8], p[9], p[14], p[15], string.Join(':', p.Fields.Skip(16)),
            p[10], p[11], p[12], p[13]);
    }

    /// <summary>The address EuroScope uses for data meant for every controller.</summary>
    public const string AllControllers = "@94835";

    /// <summary>$CQ&lt;me&gt;:@94835:&lt;kind&gt;:&lt;callsign&gt;[:value] — shared coordination data (IT, DR, SC, TA, BC, WH...).</summary>
    public static string Shared(string from, string kind, string callsign, string? value = null) =>
        $"$CQ{from}:{AllControllers}:{kind}:{callsign}" + (value == null ? "" : ":" + Clean(value));

    public static string Handoff(string from, string to, string callsign) => $"$HO{from}:{to}:{callsign}";

    public static string HandoffAccept(string from, string to, string callsign) => $"$HA{from}:{to}:{callsign}";

    /// <summary>#PC&lt;me&gt;:&lt;to&gt;:CCP:&lt;kind&gt;:&lt;callsign&gt; — controller-to-controller coordination (HC cancel/refuse, PT point-out).</summary>
    public static string Coordination(string from, string to, string kind, string callsign) => $"#PC{from}:{to}:CCP:{kind}:{callsign}";

    /// <summary>$AM — a controller amends a pilot's flight plan; the server passes it to every controller.</summary>
    public static string Amend(string from, FiledPlan fp) => string.Join(':',
        $"$AM{from}", "SERVER", fp.Callsign, Clean(fp.Rules), Clean(fp.AircraftType), fp.TrueAirspeed.ToString(Inv), Clean(fp.Departure),
        Clean(fp.DepartureTime), "0", Clean(fp.Altitude), Clean(fp.Destination), Clean(fp.EnrouteHours), Clean(fp.EnrouteMinutes),
        Clean(fp.FuelHours), Clean(fp.FuelMinutes), Clean(fp.Alternate), Clean(fp.Remarks), Clean(fp.Route));

    /// <summary>Answer to an ATIS query: one $CR ... ATIS:T line per text line, then E with the count.</summary>
    public static IEnumerable<string> AtisReply(string from, string to, IReadOnlyList<string> lines)
    {
        foreach (var l in lines) yield return $"$CR{from}:{to}:ATIS:T:{Clean(l)}";
        yield return $"$CR{from}:{to}:ATIS:E:{lines.Count + 1}";
    }
}
