using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Geo;
using NetworkAtc.Core.Plugins;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Session;

/// <summary>
/// The command line, EuroScope style. Plain text goes out on the primary frequency; dot-commands act on
/// the selected aircraft (ASEL) or the station. Plugin commands, profile aliases and alias variables
/// ($aircraft, $metar(UUEE)...) are resolved here too.
/// </summary>
public sealed partial class CommandProcessor(AtcSession session, Func<Profile> profile, PluginRegistry plugins, Func<Track?> selected)
{
    private DemoTraffic? _demo;

    /// <summary>Sector, ownership, procedures and weather; optional (commands that need it say so).</summary>
    public Workspace? Workspace { get; set; }

    /// <summary>Center used by ".demo"; set by the UI to the sector center.</summary>
    public GeoPoint DemoCenter { get; set; } = new(55.97, 37.41);

    /// <summary>The UI should center the map on this point.</summary>
    public event EventHandler<GeoPoint>? CenterRequested;
    /// <summary>The UI should show this aircraft (select it and open its flight plan).</summary>
    public event EventHandler<Track>? SelectRequested;
    /// <summary>Annotations or settings changed; the radar should redraw.</summary>
    public event EventHandler? Changed;

    public static readonly IReadOnlyList<(string Command, string Help)> BuiltIn =
    [
        (".cfl 350 | A045", "cleared flight level/altitude of the selected aircraft"),
        (".hdg 270", "assigned heading (.hdg alone clears it)"),
        (".spd 250", "assigned speed (.spd alone clears it)"),
        (".sq [4521]", "assign a squawk (no code: a free one from the position's range)"),
        (".scratch text", "scratchpad of the aircraft"),
        (".assume / .track", "assume the aircraft (or accept a handoff)"),
        (".release / .drop", "release the aircraft"),
        (".ho [POSITION]", "hand off the aircraft (no position: to the next controller by sector)"),
        (".accept / .refuse", "accept / refuse a handoff"),
        (".hc", "cancel your handoff"),
        (".po POSITION", "point out the aircraft to another controller"),
        (".sid [NAME] / .star [NAME]", "assign SID / STAR (no name: automatic)"),
        (".drwy 24R / .arwy 24L", "departure / arrival runway of the selected aircraft"),
        (".rwy UUEE 24R [24L]", "active runways of the airport: departure [arrival]"),
        (".clr", "\"clearance received\" flag of the selected aircraft"),
        (".state PUSH|TAXI|DEPA", "ground state of the aircraft (no value clears it)"),
        (".route", "show / hide the route of the selected aircraft"),
        (".halo [3]", "ring of N NM radius around the aircraft (0 removes it)"),
        (".sep AFL1 SBI2", "distance, bearing and closest approach of two aircraft"),
        (".find AFL123", "find and select an aircraft"),
        (".fp [AFL123]", "request the flight plan from the server"),
        (".msg CALLSIGN text", "private message"),
        (".contactme", "ask the selected aircraft to contact you on your frequency"),
        (".wallop text", "call a supervisor"),
        (".atis UUEE [B|+]", "ATIS letter of the airport (+ for the next one)"),
        (".metar [UUEE]", "METAR of the airport"),
        (".info", "controller info text (sent to pilots on request)"),
        (".freq 118.100", "primary frequency"),
        (".range 150", "visibility range, NM"),
        (".airport UUEE UUDD", "active airports for the departure and arrival lists (no codes: show them)"),
        (".plugins", "plugins and their commands"),
        (".demo", "demo traffic without a server (again to turn it off)"),
        (".help", "this help"),
    ];

    public async Task<string?> ExecuteAsync(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return null;
        line = ExpandAlias(line);
        if (line[0] != '.')
        {
            await session.SendRadioAsync(line).ConfigureAwait(false);
            return null;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0][1..].ToLowerInvariant();
        var args = parts.Skip(1).ToList();
        string rest = args.Count > 0 ? line[(line.IndexOf(' ') + 1)..].Trim() : "";

        switch (cmd)
        {
            case "cfl":
                return await WithSelected(async t =>
                {
                    if (args.Count == 0)
                    {
                        await session.AnnotateAsync(t, Annotation.ClearedAltitude, "").ConfigureAwait(false);
                        return $"{t.Callsign}: CFL cleared";
                    }
                    if (!TryParseAltitude(args[0], out var feet)) return "Example: .cfl 350 or .cfl A045";
                    await session.AnnotateAsync(t, Annotation.ClearedAltitude, Inv(feet)).ConfigureAwait(false);
                    return $"{t.Callsign}: CFL {FormatAltitude(feet)}";
                });
            case "hdg":
                return await WithSelected(async t =>
                {
                    if (args.Count == 0)
                    {
                        await session.AnnotateAsync(t, Annotation.Heading, "").ConfigureAwait(false);
                        return $"{t.Callsign}: heading cleared";
                    }
                    if (!int.TryParse(args[0], out var h) || h is < 1 or > 360) return "Heading 1–360";
                    await session.AnnotateAsync(t, Annotation.Heading, Inv(h)).ConfigureAwait(false);
                    return $"{t.Callsign}: heading {h:000}";
                });
            case "spd":
                return await WithSelected(async t =>
                {
                    if (args.Count == 0)
                    {
                        await session.AnnotateAsync(t, Annotation.Speed, "").ConfigureAwait(false);
                        return $"{t.Callsign}: speed cleared";
                    }
                    if (!int.TryParse(args[0], out var s) || s is < 60 or > 999) return "Speed 60–999";
                    await session.AnnotateAsync(t, Annotation.Speed, Inv(s)).ConfigureAwait(false);
                    return $"{t.Callsign}: speed {s}";
                });
            case "sq":
            case "squawk":
                return await WithSelected(async t =>
                {
                    int code;
                    if (args.Count > 0)
                    {
                        if (!IsSquawk(args[0])) return "Squawk: 4 digits from 0 to 7";
                        code = int.Parse(args[0], CultureInfo.InvariantCulture);
                    }
                    else if (FreeSquawk() is { } free) code = free;
                    else return $"No free squawks in the range {SquawkRangeText()}";
                    await session.AnnotateAsync(t, Annotation.Squawk, code.ToString("0000", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    return $"{t.Callsign}: squawk {code:0000}";
                });
            case "scratch":
                return await WithSelected(async t =>
                {
                    await session.AnnotateAsync(t, Annotation.Scratchpad, rest).ConfigureAwait(false);
                    return rest.Length == 0 ? $"{t.Callsign}: scratchpad cleared" : null;
                });
            case "track":
            case "assume":
                return await WithSelected(async t =>
                    await session.AssumeAsync(t).ConfigureAwait(false) ?? $"{t.Callsign}: assumed");
            case "drop":
            case "release":
                return await WithSelected(async t =>
                    await session.ReleaseAsync(t).ConfigureAwait(false) ?? $"{t.Callsign}: released");
            case "ho":
            case "handoff":
                return await WithSelected(async t =>
                {
                    string? target = args.Count > 0 ? ResolveController(args[0]) : Workspace?.NextController(t);
                    if (target == null)
                        return args.Count > 0 ? $"Controller {args[0].ToUpperInvariant()} not found" : "Next controller unknown. Example: .ho UUEE_APP";
                    return await session.HandoffAsync(t, target).ConfigureAwait(false) ?? $"{t.Callsign}: handoff to {target}";
                });
            case "accept":
            case "ha":
                return await WithSelected(async t =>
                    await session.AcceptHandoffAsync(t).ConfigureAwait(false) ?? $"{t.Callsign}: accepted");
            case "refuse":
            case "hr":
                return await WithSelected(async t =>
                    await session.RefuseHandoffAsync(t).ConfigureAwait(false) ?? $"{t.Callsign}: handoff refused");
            case "hc":
                return await WithSelected(async t =>
                    await session.CancelHandoffAsync(t).ConfigureAwait(false) ?? $"{t.Callsign}: handoff cancelled");
            case "po":
            case "pointout":
                return await WithSelected(async t =>
                {
                    if (args.Count == 0) return "Example: .po UUWV_CTR";
                    string target = ResolveController(args[0]) ?? args[0].ToUpperInvariant();
                    return await session.PointOutAsync(t, target).ConfigureAwait(false) ?? $"{t.Callsign} pointed out to {target}";
                });
            case "sid":
            case "star":
                return await WithSelected(async t =>
                {
                    var kind = cmd == "sid" ? Annotation.Sid : Annotation.Star;
                    var procKind = cmd == "sid" ? ProcedureKind.Sid : ProcedureKind.Star;
                    string name = args.Count > 0 ? args[0].ToUpperInvariant() : "";
                    if (name.Length > 0 && Workspace?.Sector is { Procedures.Count: > 0 } s &&
                        !s.Procedures.Any(p => p.Name == name && p.Kind == procKind))
                        return $"{cmd.ToUpperInvariant()} {name} is not in the sector";
                    await session.AnnotateAsync(t, kind, name).ConfigureAwait(false);
                    string? effective = cmd == "sid" ? Workspace?.Procedures.Sid(t) : Workspace?.Procedures.Star(t);
                    return name.Length > 0 ? $"{t.Callsign}: {cmd.ToUpperInvariant()} {name}"
                        : $"{t.Callsign}: {cmd.ToUpperInvariant()} automatic ({effective ?? "none"})";
                });
            case "drwy":
            case "arwy":
                return await WithSelected(async t =>
                {
                    string rwy = args.Count > 0 ? args[0].ToUpperInvariant() : "";
                    await session.AnnotateAsync(t, cmd == "drwy" ? Annotation.DepartureRunway : Annotation.ArrivalRunway, rwy).ConfigureAwait(false);
                    return rwy.Length == 0 ? $"{t.Callsign}: default runway" : $"{t.Callsign}: runway {rwy}";
                });
            case "rwy":
                return SetRunways(args);
            case "clr":
                return await WithSelected(async t =>
                {
                    bool value = !t.ClearanceReceived;
                    await session.AnnotateAsync(t, Annotation.Clearance, value ? "1" : "0").ConfigureAwait(false);
                    return $"{t.Callsign}: {(value ? "clearance received" : "clearance flag cleared")}";
                });
            case "state":
                return await WithSelected(async t =>
                {
                    string state = args.Count > 0 ? args[0].ToUpperInvariant() : "";
                    if (state is not ("" or "PUSH" or "TAXI" or "DEPA" or "STUP")) return "States: STUP, PUSH, TAXI, DEPA";
                    await session.AnnotateAsync(t, Annotation.GroundState, state).ConfigureAwait(false);
                    return state.Length == 0 ? $"{t.Callsign}: state cleared" : $"{t.Callsign}: {state}";
                });
            case "route":
                return await WithSelected(t =>
                {
                    t.ShowRoute = !t.ShowRoute;
                    return Task.FromResult<string?>(t.ShowRoute ? $"{t.Callsign}: route shown" : $"{t.Callsign}: route hidden");
                });
            case "halo":
                return await WithSelected(t =>
                {
                    double nm = 3;
                    if (args.Count > 0 && !double.TryParse(args[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out nm))
                        return Task.FromResult<string?>("Example: .halo 5");
                    t.HaloNm = nm <= 0 || (args.Count == 0 && t.HaloNm != null) ? null : Math.Min(nm, 50);
                    return Task.FromResult<string?>(t.HaloNm is { } r ? $"{t.Callsign}: halo {r:0.#} NM" : $"{t.Callsign}: halo removed");
                });
            case "sep":
                return Separation(args);
            case "find":
            {
                if (args.Count == 0) return "Example: .find AFL123";
                var t = session.Find(args[0]);
                if (t == null) return $"{args[0].ToUpperInvariant()} not found";
                CenterRequested?.Invoke(this, t.Position);
                SelectRequested?.Invoke(this, t);
                return null;
            }
            case "fp":
            {
                string cs = args.Count > 0 ? args[0] : selected()?.Callsign ?? "";
                if (cs.Length == 0) return "Select an aircraft or enter a callsign";
                await session.RequestFlightPlanAsync(cs).ConfigureAwait(false);
                return $"Flight plan of {cs.ToUpperInvariant()} requested";
            }
            case "msg":
            case "chat":
                if (args.Count < 2) return "Example: .msg AFL123 text";
                await session.SendPrivateAsync(args[0], rest[(rest.IndexOf(' ') + 1)..]).ConfigureAwait(false);
                return null;
            case "contactme":
            {
                var t = args.Count > 0 ? session.Find(args[0]) : selected();
                if (t == null) return "Select an aircraft or enter a callsign";
                string freq = session.Info is { } i ? Frequency.Format(i.FrequencyKhz) : profile().Station.Frequency;
                await session.SendPrivateAsync(t.Callsign, $"Please contact me on {freq}").ConfigureAwait(false);
                return null;
            }
            case "wallop":
                if (rest.Length == 0) return "Example: .wallop need help with AFL123";
                await session.SendSupervisorRequestAsync(rest).ConfigureAwait(false);
                return "Request sent to supervisors";
            case "atis":
                return Atis(args);
            case "metar":
            {
                string icao = args.Count > 0 ? args[0].ToUpperInvariant() : profile().ActiveAirports.FirstOrDefault() ?? "";
                if (icao.Length != 4) return "Example: .metar UUEE";
                if (Workspace == null) return "Weather unavailable";
                if (Workspace.Weather.Get(icao) == null) await Workspace.Weather.RefreshAsync([icao]).ConfigureAwait(false);
                return Workspace.Weather.Get(icao)?.Raw ?? $"No METAR received for {icao}";
            }
            case "info":
            {
                var lines = ControllerInfoLines();
                return lines.Count == 0 ? "Controller info is not set (Settings → Station)" : string.Join('\n', lines);
            }
            case "freq":
            {
                if (args.Count == 0 || !Frequency.TryParse(args[0], out var khz)) return "Example: .freq 118.100";
                var p = profile();
                p.Station.Frequency = Frequency.Format(khz);
                if (session.Info is { } info) await session.UpdateStationAsync(khz, info.VisualRange, info.Center).ConfigureAwait(false);
                Workspace?.UpdateOwnership();
                return $"Primary frequency {p.Station.Frequency}";
            }
            case "range":
            {
                if (args.Count == 0 || !int.TryParse(args[0], out var nm) || nm is < 1 or > 600) return "Range 1–600 NM";
                profile().Station.VisualRange = nm;
                if (session.Info is { } info) await session.UpdateStationAsync(info.FrequencyKhz, nm, info.Center).ConfigureAwait(false);
                return $"Visibility range {nm} NM";
            }
            case "demo":
                if (_demo != null)
                {
                    _demo.Dispose();
                    _demo = null;
                    return "Demo traffic off";
                }
                if (session.IsConnected) return "Demo traffic is only available when not connected to the network";
                _demo = new DemoTraffic(session, DemoCenter);
                return "Demo traffic on: 10 aircraft around the sector. .demo to turn it off";
            case "airport":
            case "airports":
            {
                var p = profile();
                if (args.Count > 0)
                {
                    var codes = args.Select(a => a.ToUpperInvariant()).Where(IsIcao).Distinct().ToList();
                    if (codes.Count != args.Count) return "Airport codes are 4-letter ICAO codes, e.g. .airport UUEE UUDD";
                    p.ActiveAirports = codes;
                    Changed?.Invoke(this, EventArgs.Empty);
                    _ = Workspace?.Weather.RefreshAsync();
                }
                return p.ActiveAirports.Count == 0 ? "No active airports. Example: .airport UUEE" : "Active airports: " + string.Join(' ', p.ActiveAirports);
            }
            case "plugins":
            {
                var sb = new StringBuilder();
                foreach (var c in plugins.Commands.Values.OrderBy(c => c.Name))
                    sb.AppendLine($".{c.Name} — {c.Description} ({c.Owner})");
                return sb.Length == 0 ? "No plugin commands" : sb.ToString().TrimEnd();
            }
            case "help":
            case "?":
                return string.Join('\n', BuiltIn.Select(b => $"{b.Command} — {b.Help}")) +
                       (plugins.Commands.IsEmpty ? "" : "\nPlugin commands: .plugins");
        }

        if (plugins.Commands.TryGetValue(cmd, out var pc))
        {
            try { return pc.Handler(args); }
            catch (Exception e) { return $"Error in plugin {pc.Owner}: {e.Message}"; }
        }
        return $"Unknown command .{cmd}. Type .help";
    }

    private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool IsIcao(string code) => code.Length == 4 && code.All(char.IsLetterOrDigit);

    // ---- runways, ATIS, separation --------------------------------------------------------------

    private string SetRunways(List<string> args)
    {
        var p = profile();
        if (args.Count == 0)
        {
            if (p.ActiveRunways.Count == 0) return "No active runways set. Example: .rwy UUEE 24R 24L";
            return string.Join('\n', p.ActiveRunways.Select(kv =>
                $"{kv.Key}: departure {string.Join(',', kv.Value.Departure)} · arrival {string.Join(',', kv.Value.Arrival)}"));
        }
        string icao = args[0].ToUpperInvariant();
        if (!IsIcao(icao)) return "Example: .rwy UUEE 24R 24L";
        if (args.Count == 1)
        {
            p.ActiveRunways.Remove(icao);
            Changed?.Invoke(this, EventArgs.Empty);
            return $"{icao}: active runways reset";
        }
        string dep = args[1].ToUpperInvariant(), arr = args.Count > 2 ? args[2].ToUpperInvariant() : dep;
        var known = Workspace?.Sector?.Runways.Where(r => r.Airport.Equals(icao, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => new[] { r.Id1.ToUpperInvariant(), r.Id2.ToUpperInvariant() }).ToHashSet();
        if (known is { Count: > 0 } && (!known.Contains(dep) || !known.Contains(arr)))
            return $"{icao}: runways {string.Join(", ", known.Order())}";
        p.ActiveRunways[icao] = new RunwayUse { Departure = [dep], Arrival = [arr] };
        if (!p.ActiveAirports.Contains(icao, StringComparer.OrdinalIgnoreCase)) p.ActiveAirports.Add(icao);
        Changed?.Invoke(this, EventArgs.Empty);
        return $"{icao}: departure {dep}, arrival {arr}";
    }

    private string Atis(List<string> args)
    {
        var p = profile();
        if (args.Count == 0)
            return p.AtisLetters.Count == 0 ? "Example: .atis UUEE B" : string.Join(' ', p.AtisLetters.Select(kv => $"{kv.Key} {kv.Value}"));
        string icao = args[0].ToUpperInvariant();
        if (!IsIcao(icao)) return "Example: .atis UUEE B";
        if (args.Count == 1) return p.AtisLetters.TryGetValue(icao, out var l) ? $"{icao}: information {l}" : $"{icao}: ATIS letter not set";
        string letter = args[1].ToUpperInvariant();
        if (letter == "+")
        {
            char current = p.AtisLetters.TryGetValue(icao, out var cur) && cur.Length == 1 ? cur[0] : '@';
            letter = current is >= 'A' and < 'Z' ? ((char)(current + 1)).ToString() : "A";
        }
        if (letter.Length != 1 || letter[0] is < 'A' or > 'Z') return "ATIS letter: A…Z or +";
        p.AtisLetters[icao] = letter;
        Changed?.Invoke(this, EventArgs.Empty);
        return $"{icao}: information {letter}";
    }

    private string Separation(List<string> args)
    {
        Track? a, b;
        if (args.Count >= 2) (a, b) = (session.Find(args[0]), session.Find(args[1]));
        else if (args.Count == 1) (a, b) = (selected(), session.Find(args[0]));
        else return "Example: .sep AFL1 SBI2";
        if (a == null || b == null) return "Both aircraft must be on the radar";
        double now = GeoMath.DistanceNm(a.Position, b.Position);
        double bearing = GeoMath.BearingDeg(a.Position, b.Position);
        var (minNm, atMinutes) = ClosestApproach(a, b, 20);
        string cpa = atMinutes < 0.1 ? "diverging" : $"closest {minNm:0.0} NM in {atMinutes:0} min";
        return $"{a.Callsign} → {b.Callsign}: {now:0.0} NM, bearing {bearing:000}°, {cpa}, " +
               $"vertical {Math.Abs(a.Altitude - b.Altitude)} ft";
    }

    /// <summary>Minimum distance within the next <paramref name="minutes"/> on present tracks (1/4-minute steps).</summary>
    public static (double Nm, double Minutes) ClosestApproach(Track a, Track b, double minutes)
    {
        double best = GeoMath.DistanceNm(a.Position, b.Position), at = 0;
        for (double m = 0.25; m <= minutes; m += 0.25)
        {
            double d = GeoMath.DistanceNm(a.Predict(m), b.Predict(m));
            if (d < best) (best, at) = (d, m);
        }
        return (best, at);
    }

    /// <summary>Accepts a callsign or an .ese identifier ("EA") of an online controller.</summary>
    public string? ResolveController(string text)
    {
        text = text.Trim().ToUpperInvariant();
        var online = session.Controllers;
        var exact = online.FirstOrDefault(c => c.Callsign.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Callsign;
        if (Workspace != null)
        {
            var byId = online.FirstOrDefault(c => Workspace.ShortName(c.Callsign).Equals(text, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId.Callsign;
        }
        // Offline (demo) handoffs go to whatever callsign was typed.
        return session.IsConnected ? null : AtcSession.IsValidCallsign(text) ? text : null;
    }

    // ---- aliases ----------------------------------------------------------------------------------

    /// <summary>".ctc UUEE_TWR 118.1" with alias ".ctc" = "contact $1 on $2" → "contact UUEE_TWR on 118.1"; then variables.</summary>
    public string ExpandAlias(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return line;
        if (profile().Aliases.TryGetValue(parts[0], out var template))
        {
            var sb = new StringBuilder(template);
            for (int i = parts.Length - 1; i >= 1; i--) sb.Replace("$" + i, parts[i]);
            line = sb.ToString();
        }
        return line.Contains('$') ? ExpandVariables(line) : line;
    }

    [GeneratedRegex(@"\$(?<name>[a-z]+)(\((?<arg>[A-Za-z0-9$]*)\))?")]
    private static partial Regex Variable();

    /// <summary>EuroScope-style alias variables: $aircraft, $cfl, $sid, $myfreq, $metar(UUEE), $atiscode(UUEE)...</summary>
    public string ExpandVariables(string text) =>
        Variable().Replace(text, m => VariableValue(m.Groups["name"].Value, m.Groups["arg"].Success ? m.Groups["arg"].Value : null) ?? m.Value);

    private static readonly HashSet<string> AircraftVariables =
        ["aircraft", "cs", "type", "dep", "arr", "alt", "cfl", "temp", "squawk", "route", "sid", "star", "calt", "nextctr", "nextfreq"];

    private string? VariableValue(string name, string? arg)
    {
        var p = profile();
        var t = selected();
        var inv = CultureInfo.InvariantCulture;
        string airport = arg is { Length: > 0 } a
            ? (a == "$myairport" ? p.ActiveAirports.FirstOrDefault() ?? "" : a).ToUpperInvariant()
            : p.ActiveAirports.FirstOrDefault() ?? "";
        var metar = Workspace?.Weather.Get(airport);
        switch (name)
        {
            case "mycallsign": return session.Me;
            case "myfreq": return session.Info is { } i ? Frequency.Format(i.FrequencyKhz) : p.Station.Frequency;
            case "myrealname": return p.Connection.RealName;
            case "myairport": return p.ActiveAirports.FirstOrDefault() ?? "";
            case "time": return DateTime.UtcNow.ToString("HHmm", inv);
            case "atiscode": return p.AtisLetters.GetValueOrDefault(airport, "");
            case "metar": return metar?.Raw ?? "";
            case "wind": return metar?.Wind ?? "";
            case "qnh": return metar?.Qnh?.ToString(inv) ?? "";
            case "altim": return metar?.Altimeter ?? "";
            case "deprwy": return t != null && arg == null ? Workspace?.Procedures.DepartureRunway(t) ?? "" : RunwaysOf(airport, dep: true);
            case "arrrwy": return t != null && arg == null ? Workspace?.Procedures.ArrivalRunway(t) ?? "" : RunwaysOf(airport, dep: false);
        }
        if (t == null) return AircraftVariables.Contains(name) ? "" : null;
        switch (name)
        {
            case "aircraft":
            case "cs": return t.Callsign;
            case "type": return t.AircraftType;
            case "dep": return t.Departure;
            case "arr": return t.Destination;
            case "alt": return t.FiledAltitude;
            case "cfl":
            case "temp": return t.ClearedAltitude is { } c ? FormatAltitude(c) : "";
            case "squawk": return (t.AssignedSquawk ?? t.Squawk).ToString("0000", inv);
            case "route": return t.Route;
            case "sid": return Workspace?.Procedures.Sid(t) ?? "";
            case "star": return Workspace?.Procedures.Star(t) ?? "";
            case "calt": return FormatAltitude(t.Altitude);
            case "nextctr": return Workspace?.NextController(t) ?? "";
            case "nextfreq":
                return Workspace?.NextController(t) is { } n && session.Controllers.FirstOrDefault(x => x.Callsign == n) is { } ctl
                    ? Frequency.Format(ctl.FrequencyKhz) : "";
        }
        return null;
    }

    private string RunwaysOf(string airport, bool dep) =>
        profile().ActiveRunways.TryGetValue(airport, out var use) ? string.Join(',', dep ? use.Departure : use.Arrival) : "";

    /// <summary>The profile's controller info lines with variables expanded, empty lines dropped.</summary>
    public IReadOnlyList<string> ControllerInfoLines() =>
        profile().ControllerInfo.Select(l => ExpandVariables(l).Trim()).Where(l => l.Length > 0).ToList();

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<string?> WithSelected(Func<Track, Task<string?>> action)
    {
        var t = selected();
        if (t == null) return "Select an aircraft on the radar first";
        var result = await action(t).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public bool TryParseAltitude(string text, out int feet)
    {
        feet = 0;
        text = text.Trim().ToUpperInvariant();
        bool altitude = text.StartsWith('A');
        if (text.StartsWith('F') || altitude) text = text[1..];
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var hundreds) || hundreds is < 0 or > 999) return false;
        feet = hundreds * 100;
        return true;
    }

    private string FormatAltitude(int feet) =>
        (feet >= profile().TransitionAltitude ? "F" : "A") + (feet / 100).ToString("000", CultureInfo.InvariantCulture);

    public static bool IsSquawk(string text) => text.Length == 4 && text.All(c => c is >= '0' and <= '7');

    private (int From, int To)? SquawkRange()
    {
        if (Workspace?.SquawkRange() is { } r) return r;
        var range = profile().SquawkRange.Split('-');
        if (range.Length != 2 || !IsSquawk(range[0].Trim()) || !IsSquawk(range[1].Trim())) return null;
        return (int.Parse(range[0], CultureInfo.InvariantCulture), int.Parse(range[1], CultureInfo.InvariantCulture));
    }

    private string SquawkRangeText() =>
        SquawkRange() is { } r ? $"{r.From:0000}-{r.To:0000}" : profile().SquawkRange;

    /// <summary>First code of the position's (or profile's) squawk range not used or assigned by anyone.</summary>
    public int? FreeSquawk()
    {
        if (SquawkRange() is not { } range) return null;
        var used = new HashSet<int>(session.Tracks.SelectMany(t => new[] { t.Squawk, t.AssignedSquawk ?? -1 }));
        for (int code = range.From; code <= range.To; code++)
            if (IsSquawk(code.ToString("0000", CultureInfo.InvariantCulture)) && !used.Contains(code)) return code;
        return null;
    }
}
