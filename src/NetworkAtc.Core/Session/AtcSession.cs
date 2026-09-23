using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Radar;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Session;

public sealed record AtcConnectInfo(
    string Host, int Port, int Cid, string Password, string RealName, int Rating,
    string Callsign, int FrequencyKhz, Facility Facility, int VisualRange, GeoPoint Center);

public sealed record AtcMessage(MessageChannel Channel, string From, string Text, DateTime Time,
    string? Peer = null, int? FrequencyKhz = null, bool Outgoing = false, bool IsError = false);

public sealed record ControllerInfo(string Callsign, int FrequencyKhz, Facility Facility, GeoPoint Position, DateTime LastSeen);

/// <summary>
/// A controller's session on SkyNetwork. Keeps the traffic picture, flight plans and messages.
/// Events are raised on background threads.
/// </summary>
public sealed partial class AtcSession : IAsyncDisposable
{
    public static readonly TimeSpan PositionInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ControllerInfo> _controllers = new(StringComparer.OrdinalIgnoreCase);
    private FsdClient? _fsd;
    private AtcConnectInfo? _info;
    private Timer? _positionTimer;
    private Timer? _sweepTimer;

    public AtcSession(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public event EventHandler<Track>? TrackUpdated;
    public event EventHandler<string>? TrackRemoved;
    public event EventHandler<Track>? FlightPlanUpdated;
    public event EventHandler<AtcMessage>? MessageReceived;
    public event EventHandler? ControllersChanged;
    public event EventHandler<bool>? ConnectionChanged;

    public bool IsConnected => _fsd?.IsConnected == true;
    public string Callsign => _info?.Callsign ?? "";
    public AtcConnectInfo? Info => _info;
    public IReadOnlyList<Track> Tracks => _tracks.Values.ToList();
    public IReadOnlyList<ControllerInfo> Controllers => _controllers.Values.OrderBy(c => c.Callsign).ToList();

    public Track? Find(string callsign) => _tracks.TryGetValue(callsign, out var t) ? t : null;

    [GeneratedRegex("^[A-Z0-9_-]{2,12}$")]
    private static partial Regex CallsignRegex();

    public static bool IsValidCallsign(string callsign) => CallsignRegex().IsMatch(callsign);

    /// <summary>Facility guessed from a callsign suffix: UUEE_TWR -> Tower.</summary>
    public static Facility FacilityFromCallsign(string callsign) => callsign.ToUpperInvariant() switch
    {
        var c when c.EndsWith("_DEL") => Facility.Delivery,
        var c when c.EndsWith("_GND") => Facility.Ground,
        var c when c.EndsWith("_TWR") => Facility.Tower,
        var c when c.EndsWith("_APP") || c.EndsWith("_DEP") => Facility.Approach,
        var c when c.EndsWith("_CTR") => Facility.Centre,
        var c when c.EndsWith("_FSS") => Facility.FlightService,
        _ => Facility.Observer,
    };

    public async Task ConnectAsync(AtcConnectInfo info, CancellationToken ct = default)
    {
        if (IsConnected) throw new InvalidOperationException("Уже подключено");
        info = info with { Callsign = info.Callsign.Trim().ToUpperInvariant() };
        if (!IsValidCallsign(info.Callsign)) throw new FsdLoginException("Неверный позывной");

        var fsd = new FsdClient();
        fsd.PacketReceived += OnPacket;
        fsd.Disconnected += OnDisconnected;
        _fsd = fsd;
        _info = info;
        try
        {
            await fsd.ConnectAsync(info.Host, info.Port,
                AtcPackets.Login(info.Callsign, info.RealName, info.Cid, info.Password, info.Rating), ct).ConfigureAwait(false);
        }
        catch
        {
            _fsd = null;
            _info = null;
            await fsd.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _positionTimer = new Timer(_ => _ = SendPositionAsync(), null, TimeSpan.Zero, PositionInterval);
        _sweepTimer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        ConnectionChanged?.Invoke(this, true);
        Raise(new AtcMessage(MessageChannel.Server, "Network-ATC", $"Подключено как {info.Callsign}", _clock()));
    }

    public async Task DisconnectAsync()
    {
        var fsd = _fsd;
        if (fsd == null || _info == null) return;
        await fsd.DisconnectAsync(AtcPackets.Logoff(_info.Callsign, _info.Cid)).ConfigureAwait(false);
    }

    /// <summary>Change the primary frequency / visibility range / center while connected.</summary>
    public Task UpdateStationAsync(int frequencyKhz, int visualRange, GeoPoint center)
    {
        if (_info == null) return Task.CompletedTask;
        _info = _info with { FrequencyKhz = frequencyKhz, VisualRange = visualRange, Center = center };
        return SendPositionAsync();
    }

    private void OnDisconnected(object? sender, string reason)
    {
        if (!ReferenceEquals(sender, _fsd)) return;
        _positionTimer?.Dispose();
        _sweepTimer?.Dispose();
        _positionTimer = _sweepTimer = null;
        _fsd = null;
        foreach (var cs in _tracks.Keys.ToList())
            if (_tracks.TryRemove(cs, out _)) TrackRemoved?.Invoke(this, cs);
        _controllers.Clear();
        ControllersChanged?.Invoke(this, EventArgs.Empty);
        Raise(new AtcMessage(MessageChannel.Server, "Network-ATC", reason, _clock()));
        ConnectionChanged?.Invoke(this, false);
    }

    internal async Task SendPositionAsync()
    {
        var fsd = _fsd;
        var info = _info;
        if (fsd == null || info == null) return;
        await fsd.SendAsync(AtcPackets.Position(info.Callsign, info.FrequencyKhz, info.Facility, info.VisualRange, info.Rating, info.Center))
            .ConfigureAwait(false);
    }

    public async Task SendRadioAsync(string text)
    {
        var fsd = _fsd ?? throw new InvalidOperationException("Нет подключения к сети");
        var info = _info!;
        await fsd.SendAsync(AtcPackets.TextMessage(info.Callsign, Frequency.ToFsdAddress(info.FrequencyKhz), text)).ConfigureAwait(false);
        Raise(new AtcMessage(MessageChannel.Radio, info.Callsign, text, _clock(), FrequencyKhz: info.FrequencyKhz, Outgoing: true));
    }

    public async Task SendPrivateAsync(string to, string text)
    {
        var fsd = _fsd ?? throw new InvalidOperationException("Нет подключения к сети");
        to = to.Trim().ToUpperInvariant();
        if (!IsValidCallsign(to)) throw new InvalidOperationException("Неверный позывной получателя");
        await fsd.SendAsync(AtcPackets.TextMessage(Callsign, to, text)).ConfigureAwait(false);
        Raise(new AtcMessage(MessageChannel.Private, Callsign, text, _clock(), Peer: to, Outgoing: true));
    }

    public Task RequestFlightPlanAsync(string callsign) =>
        _fsd?.SendAsync(AtcPackets.RequestFlightPlan(Callsign, callsign.ToUpperInvariant())) ?? Task.CompletedTask;

    // ---- incoming ------------------------------------------------------------------------

    internal void OnPacket(object? sender, FsdPacket p)
    {
        switch (p.Command)
        {
            case "@":
                if (AtcPackets.ParsePilot(p) is { } r) OnPilot(r);
                break;
            case "%":
                if (AtcPackets.ParseAtc(p) is { } a && !a.Callsign.Equals(Callsign, StringComparison.OrdinalIgnoreCase))
                {
                    bool added = !_controllers.ContainsKey(a.Callsign);
                    _controllers[a.Callsign] = new ControllerInfo(a.Callsign, a.FrequencyKhz, a.Facility, a.Position, _clock());
                    if (added) ControllersChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
            case "$FP":
                if (AtcPackets.ParseFlightPlan(p) is { } fp)
                {
                    var t = _tracks.GetOrAdd(fp.Callsign, cs => new Track(cs));
                    t.ApplyFlightPlan(fp);
                    FlightPlanUpdated?.Invoke(this, t);
                }
                break;
            case "#SB":
                if (IsToMe(p[1]) && p[2] == "PI" && p[3] == "GEN" && _tracks.TryGetValue(p[0], out var tr))
                {
                    var eq = p.Fields.Skip(4).FirstOrDefault(f => f.StartsWith("EQUIPMENT=", StringComparison.Ordinal));
                    if (eq != null && tr.AircraftType.Length == 0) tr.AircraftType = eq[10..];
                    TrackUpdated?.Invoke(this, tr);
                }
                break;
            case "#DP":
                if (_tracks.TryRemove(p[0], out _)) TrackRemoved?.Invoke(this, p[0]);
                break;
            case "#DA":
                if (_controllers.TryRemove(p[0], out _)) ControllersChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "#TM":
                OnText(p[0], p[1], string.Join(':', p.Fields.Skip(2)));
                break;
            case "$PI":
                if (IsToMe(p[1])) _ = _fsd?.SendAsync($"$PO{Callsign}:{p[0]}:{p[2]}");
                break;
            case "$CQ":
                if (IsToMe(p[1]) && p[2] == "RN" && _info != null)
                    _ = _fsd?.SendAsync($"$CR{Callsign}:{p[0]}:RN:{AtcPackets.Clean(_info.RealName)}::{_info.Rating}");
                else if (IsToMe(p[1]) && p[2] == "ATC" && _info != null)
                    _ = _fsd?.SendAsync($"$CR{Callsign}:{p[0]}:ATC:Y:{Callsign}");
                break;
            case "$ER":
                Raise(new AtcMessage(MessageChannel.Server, "Сервер", $"{p[4]} {p[3]}".Trim(), _clock(), IsError: true));
                break;
        }
    }

    private bool IsToMe(string to) => to.Equals(Callsign, StringComparison.OrdinalIgnoreCase);

    private void OnPilot(PilotReport r)
    {
        bool added = false;
        var t = _tracks.GetOrAdd(r.Callsign, cs =>
        {
            added = true;
            return new Track(cs);
        });
        bool firstPosition = t.LastUpdate == default;
        t.Update(r, _clock());
        if (added || firstPosition)
        {
            if (!t.HasFlightPlan) _ = RequestFlightPlanAsync(r.Callsign);
            _ = _fsd?.SendAsync(AtcPackets.PlaneInfoRequest(Callsign, r.Callsign));
        }
        TrackUpdated?.Invoke(this, t);
    }

    private void OnText(string from, string to, string text)
    {
        var now = _clock();
        if (from.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
            Raise(new AtcMessage(MessageChannel.Server, from, text, now));
        else if (IsToMe(to))
            Raise(new AtcMessage(MessageChannel.Private, from, text, now, Peer: from.ToUpperInvariant()));
        else if (to is "*" or "*S")
            Raise(new AtcMessage(MessageChannel.Broadcast, from, text, now));
        else if (Frequency.TryParseFsdAddress(to, out var khz))
            Raise(new AtcMessage(MessageChannel.Radio, from, text, now, FrequencyKhz: khz));
    }

    internal void Sweep()
    {
        var now = _clock();
        foreach (var t in _tracks.Values)
            if (t.LastUpdate != default && now - t.LastUpdate > StaleAfter && _tracks.TryRemove(t.Callsign, out _))
                TrackRemoved?.Invoke(this, t.Callsign);
        bool changed = false;
        foreach (var c in _controllers.Values)
            if (now - c.LastSeen > TimeSpan.FromSeconds(90)) changed |= _controllers.TryRemove(c.Callsign, out _);
        if (changed) ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Raise(AtcMessage m) => MessageReceived?.Invoke(this, m);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _positionTimer?.Dispose();
        _sweepTimer?.Dispose();
    }
}
