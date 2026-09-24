using System.Collections.Concurrent;
using System.Globalization;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Geo;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Session;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.EsPlugins;

public sealed record EsPluginInfo(int Id, string Path, string Name, string Version, string Author, string Copyright);

public sealed record EsTagItem(int PluginId, string PluginName, string Name, int Code)
{
    /// <summary>The tag field key, e.g. "es:TopSky:12".</summary>
    public string FieldKey => EsBridge.FieldKey(PluginName, Code);
}

public sealed record EsDisplayType(int PluginId, string PluginName, string Name, bool NeedRadarContent, bool GeoReferenced, bool CanBeSaved, bool CanBeCreated);

/// <summary>A plugin's value for a tag item: text, EuroScope color code (TAG_COLOR_*) and RGB when the code is 1.</summary>
public sealed record EsTagValue(string Text, int ColorCode, uint Rgb, double FontSize);

public sealed record EsPopupElement(string Text, string Text2, int FunctionId, bool Selected, int Checked, bool Disabled, bool Fixed);

/// <summary>A popup list or edit box a plugin opened; the answer goes back with <see cref="EsBridge.SelectPopup"/>.</summary>
public sealed record EsPopup(int Id, int PluginId, bool IsEdit, string Title, int Columns, EsRect Area, int FunctionId, string Initial,
    IReadOnlyList<EsPopupElement> Elements);

public sealed record EsScreenObject(int ScreenIndex, int ObjectType, string ObjectId, EsRect Area, bool Moveable, string Message);

public sealed record EsViewDrawn(int ViewId, string BackMapping, string FrontMapping, int Width, int Height, IReadOnlyList<EsScreenObject> Objects);

public sealed record EsFpListColumn(string Title, int Width, bool Centered, string ItemPlugin, int ItemCode, string LeftPlugin, int LeftFunction,
    string RightPlugin, int RightFunction);

public sealed record EsFpList(int Id, int PluginId, string Name, bool Visible, IReadOnlyList<EsFpListColumn> Columns, IReadOnlyList<string> Callsigns);

public sealed record EsUserMessage(string Handler, string Sender, string Text, bool ShowHandler, bool Unread, bool Flash, bool NeedConfirmation);

public sealed record EsTagFunctionRequest(string Callsign, string ItemPlugin, int ItemCode, string ItemString, string FunctionPlugin, int FunctionId,
    int X, int Y, EsRect Area);

/// <summary>
/// EuroScope plugins inside Network-ATC: keeps the plugin host fed with the traffic, controllers, the sector
/// and our own station, and turns what the plugins do (assume, CFL, handoff, messages, popups, drawing) into
/// Network-ATC actions and events. Events come on a background thread.
/// </summary>
public sealed class EsBridge : IAsyncDisposable
{
    public const string StandardDisplay = "Standard ES radar screen";
    public const int MainView = 1;

    private readonly AtcSession _session;
    private readonly Workspace _workspace;
    private readonly Func<Profile> _profile;
    private EsHost? _host;
    private readonly ConcurrentDictionary<int, EsPluginInfo> _plugins = new();
    private readonly ConcurrentDictionary<(int, int), EsTagItem> _items = new();
    private readonly ConcurrentDictionary<(int, int), EsTagItem> _functions = new();
    private readonly ConcurrentDictionary<string, EsDisplayType> _displayTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, int, int), EsTagValue> _values = new();
    private readonly ConcurrentDictionary<int, EsFpList> _lists = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _commands = new();
    private readonly Dictionary<string, string> _sentAircraft = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sentControllers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _pendingFp = new(StringComparer.OrdinalIgnoreCase);
    private string _sentMyself = "";
    private int _nextRequest = 1;

    public EsBridge(AtcSession session, Workspace workspace, Func<Profile> profile)
    {
        _session = session;
        _workspace = workspace;
        _profile = profile;
    }

    public bool IsRunning => _host?.IsRunning == true;
    public IReadOnlyCollection<EsPluginInfo> Plugins => _plugins.Values.OrderBy(p => p.Id).ToList();
    public IReadOnlyCollection<EsTagItem> TagItems => _items.Values.OrderBy(i => i.PluginName).ThenBy(i => i.Code).ToList();
    public IReadOnlyCollection<EsTagItem> TagFunctions => _functions.Values.OrderBy(i => i.PluginName).ThenBy(i => i.Code).ToList();
    public IReadOnlyCollection<EsDisplayType> DisplayTypes => _displayTypes.Values.OrderBy(d => d.Name).ToList();
    public IReadOnlyCollection<EsFpList> Lists => _lists.Values.OrderBy(l => l.Id).ToList();

    public event Action? Changed;
    public event Action<string, bool>? Log;
    public event Action<EsUserMessage>? UserMessage;
    public event Action<EsPopup>? Popup;
    public event Action<EsViewDrawn>? ViewDrawn;
    public event Action<int>? RefreshRequested;
    public event Action<EsTagFunctionRequest>? BuiltInTagFunction;
    public event Action<int, GeoPoint, GeoPoint>? DisplayAreaRequested;
    public event Action<string, string>? AliasAdded;
    public event Action<EsFpList>? ListChanged;
    public event Action<int, string, string, string>? ViewData;
    public event Action? TagValuesChanged;
    /// <summary>A plugin selected an aircraft (ASEL).</summary>
    public event Action<string>? AselRequested;

    public static string FieldKey(string pluginName, int code) => $"es:{pluginName}:{code}";
    public static string FunctionKey(string pluginName, int code) => $"esfn:{pluginName}:{code}";

    public EsTagValue? Value(string callsign, int pluginId, int code) => _values.TryGetValue((callsign.ToUpperInvariant(), pluginId, code), out var v) ? v : null;

    public EsTagValue? Value(string callsign, string pluginName, int code) =>
        PluginByName(pluginName) is { } p ? Value(callsign, p.Id, code) : null;

    public EsPluginInfo? PluginByName(string name) =>
        _plugins.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ---- lifecycle --------------------------------------------------------------------------------------

    /// <summary>Starts the host (once) and loads the plugins of the profile.</summary>
    public async Task StartAsync(string hostExe, string settingsFile)
    {
        if (IsRunning) return;
        var host = new EsHost();
        host.Received += OnReceived;
        host.Closed += OnClosed;
        await host.StartAsync(hostExe).ConfigureAwait(false);
        _host = host;
        Hello(settingsFile);
    }

    /// <summary>Uses a host that is already connected (tests).</summary>
    public Task AttachAsync(EsHost host, string settingsFile)
    {
        host.Received += OnReceived;
        host.Closed += OnClosed;
        _host = host;
        Hello(settingsFile);
        return Task.CompletedTask;
    }

    private void Hello(string settingsFile)
    {
        Send(new EsWriter(EsMsg.Hello).Str(settingsFile).I32(_profile().TransitionAltitude));
        _sentAircraft.Clear();
        _sentControllers.Clear();
        _sentMyself = "";
    }

    private void OnClosed(string reason)
    {
        _plugins.Clear();
        _items.Clear();
        _functions.Clear();
        _displayTypes.Clear();
        _lists.Clear();
        if (reason.Length > 0) Log?.Invoke("Плагины EuroScope: " + reason, true);
        Changed?.Invoke();
    }

    public void LoadPlugin(string path) => Send(new EsWriter(EsMsg.LoadPlugin).Str(path));
    public void UnloadPlugin(int id) => Send(new EsWriter(EsMsg.UnloadPlugin).I32(id));

    private void Send(EsWriter w) => _host?.Send(w);

    public async ValueTask DisposeAsync()
    {
        if (_host is { } h) await h.DisposeAsync().ConfigureAwait(false);
        _host = null;
    }

    // ---- the world ----------------------------------------------------------------------------------------

    /// <summary>Sends what changed since the last call: our station, controllers and aircraft.</summary>
    public void SyncWorld()
    {
        if (!IsRunning) return;
        SyncMyself();
        var controllers = _session.Controllers;
        var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in controllers)
        {
            online.Add(c.Callsign);
            var w = new EsWriter(EsMsg.Controller);
            string id = _workspace.ShortName(c.Callsign);
            WriteController(w, c.Callsign, id == c.Callsign ? "" : id, id != c.Callsign, Frequency.Format(c.FrequencyKhz), "", 0,
                (int)c.Facility, "", c.Facility != Facility.Observer, c.Position, 0);
            string sig = $"{c.FrequencyKhz}|{c.Facility}|{id}|{c.Position.Latitude:0.###}|{c.Position.Longitude:0.###}";
            if (_sentControllers.TryGetValue(c.Callsign, out var old) && old == sig) continue;
            _sentControllers[c.Callsign] = sig;
            Send(w);
        }
        foreach (var gone in _sentControllers.Keys.Where(k => !online.Contains(k)).ToList())
        {
            _sentControllers.Remove(gone);
            Send(new EsWriter(EsMsg.ControllerGone).Str(gone));
        }

        var tracks = _session.Tracks;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            if (t.LastUpdate == default && !t.HasFlightPlan) continue;
            present.Add(t.Callsign);
            var w = new EsWriter(EsMsg.Aircraft);
            string sig = WriteAircraft(w, t);
            if (_sentAircraft.TryGetValue(t.Callsign, out var old) && old == sig) continue;
            _sentAircraft[t.Callsign] = sig;
            Send(w);
        }
        foreach (var gone in _sentAircraft.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _sentAircraft.Remove(gone);
            Send(new EsWriter(EsMsg.AircraftGone).Str(gone));
        }
    }

    private void SyncMyself()
    {
        var p = _profile();
        var info = _session.Info;
        string callsign = info?.Callsign ?? _session.Me;
        string freq = info != null ? Frequency.Format(info.FrequencyKhz) : p.Station.Frequency;
        var center = info?.Center ?? default;
        var facility = info?.Facility ?? p.Station.Facility;
        string id = _workspace.ShortName(callsign);
        var w = new EsWriter(EsMsg.Myself);
        WriteController(w, callsign, id == callsign ? "" : id, id != callsign, freq, info?.RealName ?? p.Connection.RealName,
            info?.Rating ?? p.Station.Rating, (int)facility, "", facility != Facility.Observer, center, info?.VisualRange ?? p.Station.VisualRange);
        w.I32(_session.IsConnected ? 1 : 0);
        string sig = $"{callsign}|{freq}|{id}|{facility}|{_session.IsConnected}|{center}";
        if (sig == _sentMyself) return;
        _sentMyself = sig;
        Send(w);
    }

    private static void WriteController(EsWriter w, string callsign, string positionId, bool identified, string frequency, string name, int rating,
        int facility, string sectorFile, bool isController, GeoPoint at, int range)
    {
        double.TryParse(frequency, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz);
        w.Str(callsign).Str(positionId).Bool(identified).F64(mhz > 0 ? mhz : 199.998).Str(name).I32(rating).I32(facility).Str(sectorFile)
            .Bool(isController).F64(at.Latitude).F64(at.Longitude).I32(range).Bool(false).Bool(true);
    }

    /// <summary>Feet from "FL350", "F350", "35000", "350" (hundreds when below 1000).</summary>
    public static int ParseAltitude(string text)
    {
        text = text.Trim().ToUpperInvariant();
        bool level = text.StartsWith("FL") || text.StartsWith('F');
        text = text.TrimStart('F', 'L', 'A');
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return 0;
        return level || n < 1000 ? n * 100 : n;
    }

    /// <summary>EuroScope's flight plan state of a track from our tag state.</summary>
    public static int StateCode(TrackState s) => s switch
    {
        TrackState.Assumed => 5,
        TrackState.TransferToMe => 3,
        TrackState.TransferFromMe => 4,
        TrackState.Redundant => 7,
        TrackState.Concerned => 1,
        _ => 0,
    };

    /// <summary>
    /// The aircraft record in the order Engine::ReadAircraft reads it; returns a signature of the content
    /// (without the timestamps) so unchanged aircraft are not sent again.
    /// </summary>
    internal string WriteAircraft(EsWriter w, Track t)
    {
        var inv = CultureInfo.InvariantCulture;
        bool radar = t.LastUpdate != default;
        var plan = t.Plan;
        long received = radar ? new DateTimeOffset(DateTime.SpecifyKind(t.LastUpdate, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;
        var state = _workspace.StateOf(t);
        string sid = _workspace.Procedures.Sid(t) ?? "", star = _workspace.Procedures.Star(t) ?? "";
        string depRwy = _workspace.Procedures.DepartureRunway(t) ?? "", arrRwy = _workspace.Procedures.ArrivalRunway(t) ?? "";
        var route = _workspace.Procedures.ResolveRoute(t);
        int finalAltitude = ParseAltitude(t.FiledAltitude);
        string squawk = t.Squawk.ToString("0000", inv);
        char comm = t.CommType switch { "T" => 't', "R" => 'r', _ => 'v' };

        w.Str(t.Callsign).Str("").Str(SystemId(t.Callsign)).Bool(radar).Bool(t.HasFlightPlan);
        // Radar
        w.F64(t.Position.Latitude).F64(t.Position.Longitude).I32(t.PressureAltitude != 0 ? t.PressureAltitude : t.Altitude).I32(t.Altitude)
            .I32(t.GroundSpeed).I32((int)Math.Round(t.Heading) % 360).Str(squawk).Bool(t.ModeC).Bool(t.Ident).I32(t.VerticalSpeed).F64(t.Heading)
            .I32((int)received).I32(t.ModeC ? 3 : 1);
        // Flight plan
        string type = t.AircraftType;
        w.Bool(t.HasFlightPlan).Bool(false).Str(t.Rules.Length > 0 ? t.Rules : "I").Str(plan?.AircraftType ?? type).Str(StripType(type)).Str("")
            .Char(t.WakeCategory == '-' ? '?' : t.WakeCategory).Char('L').I32(2).Char('J').Char('?').Bool(true).I32(t.FiledSpeed)
            .Str(t.Departure).I32(finalAltitude).Str(t.Destination).Str(t.Alternate).Str(t.Remarks).Char(comm).Str(t.Route)
            .Str(sid).Str(star).Str(depRwy).Str(arrRwy).Str(plan?.DepartureTime ?? "").Str("")
            .Str(plan?.EnrouteHours ?? "").Str(plan?.EnrouteMinutes ?? "").Str(plan?.FuelHours ?? "").Str(plan?.FuelMinutes ?? "");
        // Controller assigned data
        w.Str(t.AssignedSquawk?.ToString("0000", inv) ?? "").I32(0).I32(t.ClearedAltitude ?? 0).Char(' ').Str(t.Scratchpad)
            .I32(t.AssignedSpeed ?? 0).I32(0).I32(0).I32(t.AssignedHeading ?? 0).Str("").I32(0);
        // States
        string next = _workspace.NextController(t) ?? "";
        double toDestination = DistanceTo(t, t.Destination), fromOrigin = DistanceTo(t, t.Departure);
        int entry = _workspace.MinutesToEntry(t) is { } m ? (int)Math.Round(m) : -1;
        var warning = _workspace.WarningOf(t);
        w.I32(StateCode(state)).I32(0).Bool(false).Str(t.Owner).Str(t.Owner.Length > 0 ? _workspace.ShortName(t.Owner) : "").Bool(t.IsTracked)
            .Str(t.HandoffTo).Str(t.HandoffTo.Length > 0 ? _workspace.ShortName(t.HandoffTo) : "").F64(toDestination).F64(fromOrigin)
            .Str("").Str("").I32(entry).I32(-1).Bool(false).Bool(warning.HasFlag(TrackWarning.Clam)).Str(t.GroundState).Bool(t.ClearanceReceived)
            .Bool(t.CommType == "T").Str(next).I32(next.Length > 0 ? 4 : 1)
            .I32(1).Str("").I32(1).I32(0).I32(1).Str("").I32(1).I32(0);
        // Route and predictions
        w.I32(route.Count > 1 ? 0 : -1).I32(-1).I32(route.Count);
        foreach (var p in route)
        {
            double nm = GeoMath.DistanceNm(t.Position, p.Position);
            int minutes = t.GroundSpeed > 30 ? (int)Math.Round(nm / t.GroundSpeed * 60) : -1;
            w.Str(p.Name).F64(p.Position.Latitude).F64(p.Position.Longitude).Str("").I32(3).I32(minutes).I32(finalAltitude);
        }
        int count = t.GroundSpeed > 30 && radar ? 10 : 0;
        w.I32(count);
        for (int i = 1; i <= count; i++)
        {
            var at = t.Predict(i);
            w.F64(at.Latitude).F64(at.Longitude).I32(t.Altitude).Str("");
        }

        return string.Join('|', t.Position.Latitude.ToString("0.#####", inv), t.Position.Longitude.ToString("0.#####", inv), t.Altitude,
            t.GroundSpeed, t.Squawk, t.ModeC, t.Ident, t.HasFlightPlan, t.Route, t.Departure, t.Destination, t.FiledAltitude, t.AircraftType,
            t.ClearedAltitude, t.AssignedHeading, t.AssignedSpeed, t.AssignedSquawk, t.Scratchpad, t.Owner, t.HandoffTo, t.IsTracked, (int)state,
            sid, star, depRwy, arrRwy, t.GroundState, t.ClearanceReceived, t.Remarks, next);
    }

    private double DistanceTo(Track t, string airport)
    {
        if (airport.Length == 0 || t.LastUpdate == default) return 0;
        var sector = _sector;
        var a = sector?.Airports.FirstOrDefault(x => x.Name.Equals(airport, StringComparison.OrdinalIgnoreCase));
        return a == null ? 0 : GeoMath.DistanceNm(t.Position, a.Position);
    }

    private static string StripType(string type)
    {
        type = type.Trim().ToUpperInvariant();
        int slash = type.IndexOf('/');
        if (slash == 1 && type.Length > 2) type = type[2..];     // "H/B744/L"
        slash = type.IndexOf('/');
        return slash > 0 ? type[..slash] : type;
    }

    /// <summary>A stable id from the callsign, like EuroScope's system id.</summary>
    private static string SystemId(string callsign)
    {
        uint h = 2166136261;
        foreach (char c in callsign.ToUpperInvariant()) h = (h ^ c) * 16777619;
        return (h % 100000).ToString("00000", CultureInfo.InvariantCulture);
    }

    private SectorFile? _sector;
    private string _sectorFileName = "";

    /// <summary>Sends the sector file elements (and the active airports and runways) to the plugins.</summary>
    public void SendSector(SectorFile? sector, string fileName)
    {
        _sector = sector;
        _sectorFileName = fileName;
        if (!IsRunning) return;
        Send(new EsWriter(EsMsg.SectorReset).Str(fileName));
        if (sector != null)
            foreach (var e in SectorElements(sector, _profile())) Send(e);
        Send(new EsWriter(EsMsg.SectorDone));
    }

    /// <summary>Only the runway activity changed.</summary>
    public void SendRunways() => SendSector(_sector, _sectorFileName);

    private static EsWriter Element(int type, string name, string airport, double frequency, IEnumerable<GeoPoint> points, IEnumerable<string> components,
        string rwy1 = "", string rwy2 = "", int hdg1 = 0, int hdg2 = 0, bool dep1 = false, bool arr1 = false, bool dep2 = false, bool arr2 = false)
    {
        var w = new EsWriter(EsMsg.SectorElement).I32(type).Str(name).Str(airport).F64(frequency);
        var list = points.ToList();
        w.I32(list.Count);
        foreach (var p in list) w.F64(p.Latitude).F64(p.Longitude);
        var comps = components.ToList();
        w.I32(comps.Count);
        foreach (var c in comps) w.Str(c);
        return w.Str(rwy1).Str(rwy2).I32(hdg1).I32(hdg2).Bool(dep1).Bool(arr1).Bool(dep2).Bool(arr2);
    }

    private static double Mhz(string frequency) =>
        double.TryParse(frequency, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>The sector as EuroScope's SECTOR_ELEMENT_* list (the numbers are EuroScope's element types).</summary>
    public static IEnumerable<EsWriter> SectorElements(SectorFile s, Profile profile)
    {
        foreach (var v in s.Vors) yield return Element(1, v.Name, "", Mhz(v.Frequency), [v.Position], ["symbol", "name", "frequency"]);
        foreach (var n in s.Ndbs) yield return Element(2, n.Name, "", Mhz(n.Frequency), [n.Position], ["symbol", "name", "frequency"]);
        foreach (var a in s.Airports)
        {
            bool active = profile.ActiveAirports.Contains(a.Name, StringComparer.OrdinalIgnoreCase);
            yield return Element(3, a.Name, a.Name, Mhz(a.Frequency), [a.Position], ["symbol", "name"], dep1: active, arr1: active);
        }
        foreach (var r in s.Runways)
        {
            profile.ActiveRunways.TryGetValue(r.Airport, out var use);
            bool Dep(string id) => use?.Departure.Contains(id, StringComparer.OrdinalIgnoreCase) == true;
            bool Arr(string id) => use?.Arrival.Contains(id, StringComparer.OrdinalIgnoreCase) == true;
            yield return Element(4, $"{r.Airport} {r.Id1}-{r.Id2}", r.Airport, 0, [r.End1, r.End2], ["centerline", "name"],
                r.Id1, r.Id2, r.Heading1, r.Heading2, Dep(r.Id1), Arr(r.Id1), Dep(r.Id2), Arr(r.Id2));
        }
        foreach (var f in s.Fixes) yield return Element(5, f.Name, "", 0, [f.Position], ["symbol", "name"]);
        foreach (var p in s.Procedures)
            yield return Element(p.Kind == ProcedureKind.Star ? 6 : 7, p.Name, p.Airport, 0, [], ["line", "name"], p.Runway, p.Runway);
        (string Layer, int Type)[] lines = [("LOW AIRWAY", 8), ("HIGH AIRWAY", 9), ("ARTCC HIGH", 10), ("ARTCC", 11), ("ARTCC LOW", 12), ("GEO", 13)];
        foreach (var (layer, type) in lines)
        {
            if (!s.Lines.TryGetValue(layer, out var list)) continue;
            foreach (var group in list.GroupBy(l => l.Name))
                yield return Element(type, group.Key, "", 0, group.SelectMany(l => new[] { l.From, l.To }), ["line", "name"]);
        }
        foreach (var t in s.FreeTexts) yield return Element(14, t.Group.Length > 0 ? $"{t.Group}\\{t.Text}" : t.Text, "", 0, [t.Position], ["freetext"]);
        foreach (var a in s.Sectors) yield return Element(15, a.Name, "", 0, [], ["sector"]);
        foreach (var p in s.Positions) yield return Element(16, p.Callsign, "", Mhz(p.Frequency), [], ["name"]);
        foreach (var r in s.Regions) yield return Element(19, r.Name, "", 0, r.Points, ["region"]);
    }

    // ---- to the plugins ---------------------------------------------------------------------------------

    public void SetAsel(string callsign) => Send(new EsWriter(EsMsg.Asel).Str(callsign));

    /// <summary>Offers a command line to the plugins; true when one of them took it.</summary>
    public async Task<bool> CommandAsync(string text)
    {
        if (!IsRunning || _plugins.IsEmpty) return false;
        int id = Interlocked.Increment(ref _nextRequest);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands[id] = tcs;
        Send(new EsWriter(EsMsg.Command).I32(id).Str(text));
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _commands.TryRemove(id, out _);
        }
    }

    public void Chat(bool isPrivate, string sender, string receiver, double frequency, string text) =>
        Send(new EsWriter(EsMsg.Chat).Bool(isPrivate).Str(sender).Str(receiver).F64(frequency).Str(text));

    public void Metar(string station, string metar) => Send(new EsWriter(EsMsg.Metar).Str(station).Str(metar));

    /// <summary>The plugin tag items shown in our tags (field keys "es:Plugin:code").</summary>
    public void SetTagItemsInUse(IEnumerable<string> fieldKeys)
    {
        var used = new List<(int, int)>();
        foreach (var key in fieldKeys)
            if (ParseKey(key, "es:") is { } k && PluginByName(k.Plugin) is { } p) used.Add((p.Id, k.Code));
        var w = new EsWriter(EsMsg.TagItemsInUse).I32(used.Count);
        foreach (var (pid, code) in used) w.I32(pid).I32(code);
        Send(w);
    }

    /// <summary>"es:Plugin:12" → (Plugin, 12).</summary>
    public static (string Plugin, int Code)? ParseKey(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        int colon = key.LastIndexOf(':');
        if (colon <= prefix.Length || !int.TryParse(key[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) return null;
        return (key[prefix.Length..colon], code);
    }

    /// <summary>A click on a plugin function (tag field or list column).</summary>
    public void CallFunction(string pluginName, int functionId, string callsign, string itemString, int x, int y, EsRect area)
    {
        if (PluginByName(pluginName) is not { } p) return;
        Send(new EsWriter(EsMsg.FunctionCall).I32(p.Id).I32(functionId).Str(callsign).Str(itemString).I32(x).I32(y).Rect(area));
    }

    public void SelectPopup(EsPopup popup, int functionId, string text, int x, int y) =>
        Send(new EsWriter(EsMsg.PopupSelect).I32(popup.Id).I32(functionId).Str(text).I32(x).I32(y).Rect(popup.Area));

    public void OpenView(int viewId, string displayType, IReadOnlyDictionary<string, string> data)
    {
        var w = new EsWriter(EsMsg.ViewOpen).I32(viewId).Str(displayType).I32(data.Count);
        foreach (var (k, v) in data) w.Str(k).Str(v);
        Send(w);
    }

    public void CloseView(int viewId) => Send(new EsWriter(EsMsg.ViewClose).I32(viewId));

    public void ViewGeometry(int viewId, int width, int height, GeoPoint projectionCenter, double cx, double cy, double nmPerPixel, uint keyColor,
        EsRect radarArea, EsRect toolbarArea, EsRect chatArea) =>
        Send(new EsWriter(EsMsg.ViewGeometry).I32(viewId).I32(width).I32(height).F64(projectionCenter.Latitude).F64(projectionCenter.Longitude)
            .F64(cx).F64(cy).F64(nmPerPixel).U32(keyColor).Rect(radarArea).Rect(toolbarArea).Rect(chatArea));

    public void RefreshView(int viewId) => Send(new EsWriter(EsMsg.ViewRefresh).I32(viewId));

    public void SaveView(int viewId) => Send(new EsWriter(EsMsg.ViewSave).I32(viewId));

    /// <summary>Mouse on a plugin's screen object: 0 over, 1 down, 2 up, 3 click, 4 double click, 5 move (extra = released).</summary>
    public void ScreenObjectEvent(int viewId, EsScreenObject o, int kind, int x, int y, int buttonOrReleased) =>
        Send(new EsWriter(EsMsg.ScreenObjectEvent).I32(viewId).I32(o.ScreenIndex).I32(kind).I32(o.ObjectType).Str(o.ObjectId).I32(x).I32(y)
            .Rect(o.Area).I32(buttonOrReleased));

    public void PlaneInfo(string callsign, string livery, string type) => Send(new EsWriter(EsMsg.PlaneInfo).Str(callsign).Str(livery).Str(type));

    // ---- from the plugins -------------------------------------------------------------------------------

    private void OnReceived(EsMsg type, EsReader r)
    {
        switch (type)
        {
            case EsMsg.Log:
            {
                bool error = r.Bool();
                Log?.Invoke(r.Str(), error);
                break;
            }
            case EsMsg.PluginLoaded:
            {
                var p = new EsPluginInfo(r.I32(), r.Str(), r.Str(), r.Str(), r.Str(), r.Str());
                _plugins[p.Id] = p;
                // Items registered in the constructor came before we knew the name.
                foreach (var (key, item) in _items.Where(kv => kv.Key.Item1 == p.Id).ToList()) _items[key] = item with { PluginName = p.Name };
                foreach (var (key, item) in _functions.Where(kv => kv.Key.Item1 == p.Id).ToList()) _functions[key] = item with { PluginName = p.Name };
                foreach (var (key, d) in _displayTypes.Where(kv => kv.Value.PluginId == p.Id).ToList()) _displayTypes[key] = d with { PluginName = p.Name };
                Log?.Invoke($"Плагин EuroScope {p.Name} {p.Version} загружен", false);
                Changed?.Invoke();
                break;
            }
            case EsMsg.PluginFailed:
            {
                string path = r.Str(), reason = r.Str();
                Log?.Invoke($"Плагин {Path.GetFileName(path)} не загружен: {reason}", true);
                break;
            }
            case EsMsg.PluginUnloaded:
            {
                int id = r.I32();
                if (_plugins.TryRemove(id, out var p)) Log?.Invoke($"Плагин {p.Name} выгружен", false);
                foreach (var k in _items.Keys.Where(k => k.Item1 == id).ToList()) _items.TryRemove(k, out _);
                foreach (var k in _functions.Keys.Where(k => k.Item1 == id).ToList()) _functions.TryRemove(k, out _);
                foreach (var k in _displayTypes.Where(kv => kv.Value.PluginId == id).Select(kv => kv.Key).ToList()) _displayTypes.TryRemove(k, out _);
                foreach (var k in _lists.Where(kv => kv.Value.PluginId == id).Select(kv => kv.Key).ToList()) _lists.TryRemove(k, out _);
                Changed?.Invoke();
                break;
            }
            case EsMsg.DisplayType:
            {
                int pid = r.I32();
                var d = new EsDisplayType(pid, NameOf(pid), r.Str(), r.Bool(), r.Bool(), r.Bool(), r.Bool());
                _displayTypes[d.Name] = d;
                Changed?.Invoke();
                break;
            }
            case EsMsg.TagItemType:
            case EsMsg.TagItemFunction:
            {
                int pid = r.I32();
                string name = r.Str();
                int code = r.I32();
                var item = new EsTagItem(pid, NameOf(pid), name, code);
                (type == EsMsg.TagItemType ? _items : _functions)[(pid, code)] = item;
                Changed?.Invoke();
                break;
            }
            case EsMsg.UserMessage:
            {
                string handler = r.Str(), sender = r.Str(), text = r.Str();
                bool show = r.Bool(), unread = r.Bool(), _ = r.Bool(), flash = r.Bool(), confirm = r.Bool();
                UserMessage?.Invoke(new EsUserMessage(handler, sender, text, show, unread, flash, confirm));
                break;
            }
            case EsMsg.Action:
                OnAction((EsActionKind)r.I32(), r.Str(), r.Str(), r.Str(), r.I32());
                break;
            case EsMsg.CommandResult:
            {
                int id = r.I32();
                bool handled = r.Bool();
                if (_commands.TryRemove(id, out var tcs)) tcs.TrySetResult(handled);
                break;
            }
            case EsMsg.TagValues:
            {
                int n = r.I32();
                for (int i = 0; i < n && r.Ok; i++)
                {
                    string cs = r.Str();
                    int pid = r.I32(), code = r.I32();
                    var v = new EsTagValue(r.Str(), r.I32(), r.U32(), r.F64());
                    _values[(cs.ToUpperInvariant(), pid, code)] = v;
                }
                TagValuesChanged?.Invoke();
                break;
            }
            case EsMsg.PopupList:
            {
                int id = r.I32(), pid = r.I32();
                string title = r.Str();
                int columns = r.I32();
                var area = r.Rect();
                int n = r.I32();
                var elements = new List<EsPopupElement>();
                for (int i = 0; i < n && r.Ok; i++)
                    elements.Add(new EsPopupElement(r.Str(), r.Str(), r.I32(), r.Bool(), r.I32(), r.Bool(), r.Bool()));
                Popup?.Invoke(new EsPopup(id, pid, false, title, columns, area, 0, "", elements));
                break;
            }
            case EsMsg.PopupEdit:
            {
                int id = r.I32(), pid = r.I32(), fid = r.I32();
                var area = r.Rect();
                Popup?.Invoke(new EsPopup(id, pid, true, "", 1, area, fid, r.Str(), []));
                break;
            }
            case EsMsg.ViewDrawn:
            {
                int view = r.I32();
                string back = r.Str(), front = r.Str();
                int width = r.I32(), height = r.I32();
                r.Bool();
                int n = r.I32();
                var objects = new List<EsScreenObject>();
                for (int i = 0; i < n && r.Ok; i++)
                    objects.Add(new EsScreenObject(r.I32(), r.I32(), r.Str(), r.Rect(), r.Bool(), r.Str()));
                ViewDrawn?.Invoke(new EsViewDrawn(view, back, front, width, height, objects));
                break;
            }
            case EsMsg.ViewData:
            {
                int view = r.I32();
                string name = r.Str(), description = r.Str(), value = r.Str();
                ViewData?.Invoke(view, name, description, value);
                break;
            }
            case EsMsg.RequestRefresh:
                RefreshRequested?.Invoke(r.I32());
                break;
            case EsMsg.StartTagFunction:
                BuiltInTagFunction?.Invoke(new EsTagFunctionRequest(r.Str(), r.Str(), r.I32(), r.Str(), r.Str(), r.I32(), r.I32(), r.I32(), r.Rect()));
                break;
            case EsMsg.SetDisplayArea:
            {
                int view = r.I32();
                var a = new GeoPoint(r.F64(), r.F64());
                var b = new GeoPoint(r.F64(), r.F64());
                DisplayAreaRequested?.Invoke(view, a, b);
                break;
            }
            case EsMsg.FpList:
            {
                int id = r.I32(), pid = r.I32();
                string name = r.Str();
                bool visible = r.Bool();
                int n = r.I32();
                var columns = new List<EsFpListColumn>();
                for (int i = 0; i < n && r.Ok; i++)
                    columns.Add(new EsFpListColumn(r.Str(), r.I32(), r.Bool(), r.Str(), r.I32(), r.Str(), r.I32(), r.Str(), r.I32()));
                n = r.I32();
                var callsigns = new List<string>();
                for (int i = 0; i < n && r.Ok; i++) callsigns.Add(r.Str());
                var list = new EsFpList(id, pid, name, visible, columns, callsigns);
                _lists[id] = list;
                ListChanged?.Invoke(list);
                break;
            }
            case EsMsg.Alias:
            {
                string name = r.Str(), value = r.Str();
                AliasAdded?.Invoke(name, value);
                break;
            }
            case EsMsg.RefreshMap:
            case EsMsg.ShowSectorElement:
                // The map of Network-ATC is drawn from its own layers; element visibility is not taken over.
                break;
        }
    }

    private string NameOf(int pluginId) => _plugins.TryGetValue(pluginId, out var p) ? p.Name : "";

    private Track? Find(string callsign) =>
        _session.Tracks.FirstOrDefault(t => t.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));

    private void Run(Func<Task<string?>> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (await action().ConfigureAwait(false) is { } error) Log?.Invoke(error, true);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                Log?.Invoke(e.Message, true);
            }
        });
    }

    private void OnAction(EsActionKind kind, string callsign, string a, string b, int n)
    {
        var t = Find(callsign);
        switch (kind)
        {
            case EsActionKind.StartTracking when t != null:
                Run(() => _session.AssumeAsync(t));
                break;
            case EsActionKind.EndTracking when t != null:
                Run(() => _session.ReleaseAsync(t));
                break;
            case EsActionKind.InitiateHandoff when t != null:
                Run(() => _session.HandoffAsync(t, a));
                break;
            case EsActionKind.AcceptHandoff when t != null:
                Run(() => _session.AcceptHandoffAsync(t));
                break;
            case EsActionKind.RefuseHandoff when t != null:
                Run(() => _session.RefuseHandoffAsync(t));
                break;
            case EsActionKind.SetAssigned when t != null:
                if (AssignedAnnotation(n, a) is { } set)
                    Run(async () =>
                    {
                        await _session.AnnotateAsync(t, set.Annotation, set.Value).ConfigureAwait(false);
                        return null;
                    });
                break;
            case EsActionKind.SetFlightPlan when t != null:
                lock (_pendingFp)
                {
                    if (!_pendingFp.TryGetValue(t.Callsign, out var fields)) _pendingFp[t.Callsign] = fields = [];
                    fields[a] = b;
                }
                break;
            case EsActionKind.AmendFlightPlan when t != null:
                Run(async () =>
                {
                    Dictionary<string, string>? fields;
                    lock (_pendingFp) _pendingFp.Remove(t.Callsign, out fields);
                    await _session.AmendFlightPlanAsync(t, Amended(t, fields ?? [])).ConfigureAwait(false);
                    return null;
                });
                break;
            case EsActionKind.SetAsel when t != null:
                AselRequested?.Invoke(t.Callsign);
                break;
            case EsActionKind.PushStrip:
                Log?.Invoke($"{callsign}: передача стрипа {a} не поддерживается", false);
                break;
            case EsActionKind.InitiateCoordination:
            case EsActionKind.AcceptCoordination:
            case EsActionKind.RefuseCoordination:
                Log?.Invoke($"{callsign}: координация точки/высоты по запросу плагина пока не поддерживается", false);
                break;
        }
        Changed?.Invoke();
    }

    /// <summary>EuroScope CTR_DATA_TYPE_* to our shared annotations.</summary>
    public static (Annotation Annotation, string Value)? AssignedAnnotation(int dataType, string value) => dataType switch
    {
        1 => (Annotation.Squawk, value),
        3 => (Annotation.ClearedAltitude, int.TryParse(value, out var cfl) && cfl > 2 ? cfl.ToString(CultureInfo.InvariantCulture) : ""),
        5 => (Annotation.Scratchpad, value),
        6 => (Annotation.GroundState, value),
        7 => (Annotation.Clearance, value is "1" or "true" ? "1" : ""),
        9 => (Annotation.Speed, value == "0" ? "" : value),
        12 => (Annotation.Heading, value == "0" ? "" : value),
        _ => null,
    };

    private static FiledPlan Amended(Track t, Dictionary<string, string> f)
    {
        var p = t.Plan ?? new FiledPlan(t.Callsign, t.Rules, t.AircraftType, t.FiledSpeed, t.Departure, "", t.FiledAltitude, t.Destination,
            t.Alternate, t.Remarks, t.Route);
        string Get(string key, string current) => f.TryGetValue(key, out var v) ? v : current;
        int speed = f.TryGetValue("TrueAirspeed", out var tas) && int.TryParse(tas, out var s) ? s : p.TrueAirspeed;
        string altitude = f.TryGetValue("FinalAltitude", out var fa) && int.TryParse(fa, out var feet) ? feet.ToString(CultureInfo.InvariantCulture) : p.Altitude;
        return p with
        {
            Rules = Get("PlanType", p.Rules),
            AircraftType = Get("AircraftInfo", p.AircraftType),
            TrueAirspeed = speed,
            Departure = Get("Origin", p.Departure),
            DepartureTime = Get("EstimatedDeparture", p.DepartureTime),
            Altitude = altitude,
            Destination = Get("Destination", p.Destination),
            Alternate = Get("Alternate", p.Alternate),
            Remarks = Get("Remarks", p.Remarks),
            Route = Get("Route", p.Route),
            EnrouteHours = Get("EnrouteHours", p.EnrouteHours),
            EnrouteMinutes = Get("EnrouteMinutes", p.EnrouteMinutes),
            FuelHours = Get("FuelHours", p.FuelHours),
            FuelMinutes = Get("FuelMinutes", p.FuelMinutes),
        };
    }
}
