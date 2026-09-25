using NetworkAtc.Core.Fsd;
using NetworkAtc.Plugins;
using NetworkAtc.Core.Session;

namespace NetworkAtc.Core.Atis;

/// <summary>
/// An ATIS on the network: its own FSD connection (UUEE_ATIS) with the controller's CID, on the ATIS
/// frequency at the airport. It answers pilots' ATIS requests with the text lines of the moment.
/// </summary>
public sealed class AtisStation : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<string>> _text;
    private FsdClient? _fsd;
    private Timer? _positionTimer;
    private string _realName = "";
    private int _cid, _rating, _frequencyKhz;
    private GeoPoint _position;

    public AtisStation(string callsign, Func<IReadOnlyList<string>> text)
    {
        Callsign = callsign.ToUpperInvariant();
        _text = text;
    }

    public string Callsign { get; }
    public bool IsConnected => _fsd?.IsConnected == true;

    /// <summary>Raised on a background thread when the connection is lost (the reason) or closed ("").</summary>
    public event EventHandler<string>? Disconnected;

    /// <summary>An ATIS request was answered (the pilot's callsign).</summary>
    public event EventHandler<string>? Requested;

    public async Task ConnectAsync(AtcConnectInfo controller, int frequencyKhz, GeoPoint position, CancellationToken ct = default)
    {
        if (_fsd != null) throw new InvalidOperationException("ATIS is already connected");
        if (!AtcSession.IsValidCallsign(Callsign)) throw new FsdLoginException($"Invalid ATIS callsign: {Callsign}");
        (_realName, _cid, _rating, _frequencyKhz, _position) = (controller.RealName, controller.Cid, controller.Rating, frequencyKhz, position);
        var fsd = new FsdClient();
        fsd.PacketReceived += OnPacket;
        fsd.Disconnected += (_, reason) =>
        {
            if (!ReferenceEquals(_fsd, fsd)) return;
            Stop();
            Disconnected?.Invoke(this, reason);
        };
        _fsd = fsd;
        try
        {
            await fsd.ConnectAsync(controller.Host, controller.Port,
                AtcPackets.Login(Callsign, controller.RealName, controller.Cid, controller.Password, controller.Rating), ct).ConfigureAwait(false);
        }
        catch
        {
            _fsd = null;
            await fsd.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _positionTimer = new Timer(_ => _ = SendPositionAsync(), null, TimeSpan.Zero, AtcSession.PositionInterval);
    }

    public async Task DisconnectAsync()
    {
        var fsd = _fsd;
        if (fsd == null) return;
        Stop();
        _fsd = null;
        await fsd.DisconnectAsync(AtcPackets.Logoff(Callsign, _cid)).ConfigureAwait(false);
        await fsd.DisposeAsync().ConfigureAwait(false);
        Disconnected?.Invoke(this, "");
    }

    /// <summary>A new frequency (while connected, it is announced with the next position report).</summary>
    public Task SetFrequencyAsync(int frequencyKhz)
    {
        _frequencyKhz = frequencyKhz;
        return SendPositionAsync();
    }

    private void Stop()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    private Task SendPositionAsync() =>
        _fsd is { } fsd ? fsd.SendAsync(AtcPackets.Position(Callsign, _frequencyKhz, Facility.Observer, 50, _rating, _position)) : Task.CompletedTask;

    internal void OnPacket(object? sender, FsdPacket p)
    {
        if (_fsd is not { } fsd || p.Fields.Length < 2 || !p[1].Equals(Callsign, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var packet in Reply(p)) _ = fsd.SendAsync(packet);
        if (p.Command == "$CQ" && p.Fields.Length >= 3 && p[2] == "ATIS") Requested?.Invoke(this, p[0]);
    }

    /// <summary>What the ATIS sends back to a packet addressed to it.</summary>
    internal IEnumerable<string> Reply(FsdPacket p)
    {
        switch (p.Command)
        {
            case "$PI" when p.Fields.Length >= 3:
                return [$"$PO{Callsign}:{p[0]}:{p[2]}"];
            case "$CQ" when p.Fields.Length >= 3 && p[2] == "ATIS":
                return AtcPackets.AtisReply(Callsign, p[0], _text()).ToList();
            case "$CQ" when p.Fields.Length >= 3 && p[2] == "RN":
                return [$"$CR{Callsign}:{p[0]}:RN:{AtcPackets.Clean(_realName)}::{_rating}"];
            case "$CQ" when p.Fields.Length >= 3 && p[2] == "ATC":
                return [$"$CR{Callsign}:{p[0]}:ATC:N:{Callsign}"];
            default:
                return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_fsd is { } fsd)
        {
            _fsd = null;
            await fsd.DisposeAsync().ConfigureAwait(false);
        }
    }
}
