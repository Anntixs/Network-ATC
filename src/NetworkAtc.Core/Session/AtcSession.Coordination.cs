using System.Globalization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Radar;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Session;

/// <summary>Data a controller sets on a track and shares with the other controllers.</summary>
public enum Annotation
{
    ClearedAltitude,
    Heading,
    Speed,
    Squawk,
    Scratchpad,
    Sid,
    Star,
    DepartureRunway,
    ArrivalRunway,
    Clearance,
    GroundState,
}

public enum CoordinationKind
{
    /// <summary>Another controller offers us the aircraft.</summary>
    HandoffRequested,
    /// <summary>Our offer was accepted.</summary>
    HandoffAccepted,
    /// <summary>Our offer was refused.</summary>
    HandoffRefused,
    /// <summary>An offer to us was withdrawn.</summary>
    HandoffCancelled,
    /// <summary>Another controller points the aircraft out to us.</summary>
    PointOut,
    /// <summary>Another controller assumed or released the aircraft.</summary>
    OwnerChanged,
}

public sealed record CoordinationEvent(CoordinationKind Kind, Track Track, string Peer);

/// <summary>
/// EuroScope-style coordination: assume/release (IT/DR), transfer of control ($HO/$HA, #PC CCP HC),
/// point-outs (#PC CCP PT) and shared annotations ($CQ … @94835 SC/TA/BC…).
/// </summary>
public sealed partial class AtcSession
{
    private static readonly Dictionary<Annotation, string> AnnotationCodes = new()
    {
        [Annotation.ClearedAltitude] = "TA",
        [Annotation.Heading] = "HD",
        [Annotation.Speed] = "SP",
        [Annotation.Squawk] = "BC",
        [Annotation.Scratchpad] = "SC",
        [Annotation.Sid] = "SID",
        [Annotation.Star] = "STAR",
        [Annotation.DepartureRunway] = "DRWY",
        [Annotation.ArrivalRunway] = "ARWY",
        [Annotation.Clearance] = "CLR",
        [Annotation.GroundState] = "GS",
    };

    public event EventHandler<CoordinationEvent>? Coordination;

    /// <summary>Callsign used for tracks assumed while offline (demo traffic); normally the profile station callsign.</summary>
    public string LocalCallsign { get; set; } = "";

    /// <summary>Our callsign when connected, otherwise <see cref="LocalCallsign"/>.</summary>
    public string Me => _info?.Callsign ?? (LocalCallsign.Length > 0 ? LocalCallsign.ToUpperInvariant() : "LOCAL");

    /// <summary>Lines sent to pilots who request our ATIS / controller info.</summary>
    public Func<IReadOnlyList<string>>? ControllerInfo { get; set; }

    private bool IsMe(string callsign) => callsign.Equals(Me, StringComparison.OrdinalIgnoreCase);

    private void SetOwner(Track t, string owner)
    {
        t.Owner = owner.ToUpperInvariant();
        t.IsTracked = t.Owner.Length > 0 && IsMe(t.Owner);
    }

    private static void ClearHandoff(Track t) => t.HandoffFrom = t.HandoffTo = "";

    private Task Send(string packet) => _fsd?.SendAsync(packet) ?? Task.CompletedTask;

    private void Changed(Track t) => TrackUpdated?.Invoke(this, t);

    // ---- actions --------------------------------------------------------------------------------

    /// <summary>Start tracking. Fails if another controller has it and is not handing it to us.</summary>
    public async Task<string?> AssumeAsync(Track t)
    {
        if (t.HandoffPending && IsMe(t.HandoffTo)) return await AcceptHandoffAsync(t).ConfigureAwait(false);
        if (t.Owner.Length > 0 && !IsMe(t.Owner)) return $"{t.Callsign} на сопровождении у {t.Owner}";
        SetOwner(t, Me);
        Changed(t);
        await Send(AtcPackets.Shared(Me, "IT", t.Callsign)).ConfigureAwait(false);
        return null;
    }

    public async Task<string?> ReleaseAsync(Track t)
    {
        if (!t.IsTracked) return $"{t.Callsign} не на вашем сопровождении";
        if (t.HandoffPending && IsMe(t.HandoffFrom)) await CancelHandoffAsync(t).ConfigureAwait(false);
        SetOwner(t, "");
        Changed(t);
        await Send(AtcPackets.Shared(Me, "DR", t.Callsign)).ConfigureAwait(false);
        return null;
    }

    /// <summary>Offer the aircraft to <paramref name="to"/> (transfer of control).</summary>
    public async Task<string?> HandoffAsync(Track t, string to)
    {
        to = to.Trim().ToUpperInvariant();
        if (!t.IsTracked) return $"{t.Callsign} не на вашем сопровождении";
        if (IsMe(to)) return "Нельзя передать борт самому себе";
        if (IsConnected && !_controllers.ContainsKey(to)) return $"{to} не в сети";
        t.HandoffFrom = Me;
        t.HandoffTo = to;
        Changed(t);
        await Send(AtcPackets.Handoff(Me, to, t.Callsign)).ConfigureAwait(false);
        return null;
    }

    public async Task<string?> AcceptHandoffAsync(Track t)
    {
        if (!t.HandoffPending || !IsMe(t.HandoffTo)) return $"{t.Callsign} вам не передают";
        string from = t.HandoffFrom;
        ClearHandoff(t);
        SetOwner(t, Me);
        Changed(t);
        await Send(AtcPackets.HandoffAccept(Me, from, t.Callsign)).ConfigureAwait(false);
        await Send(AtcPackets.Shared(Me, "IT", t.Callsign)).ConfigureAwait(false);
        return null;
    }

    public async Task<string?> RefuseHandoffAsync(Track t)
    {
        if (!t.HandoffPending || !IsMe(t.HandoffTo)) return $"{t.Callsign} вам не передают";
        string from = t.HandoffFrom;
        ClearHandoff(t);
        Changed(t);
        await Send(AtcPackets.Coordination(Me, from, "HC", t.Callsign)).ConfigureAwait(false);
        return null;
    }

    /// <summary>Withdraw our own pending offer.</summary>
    public async Task<string?> CancelHandoffAsync(Track t)
    {
        if (!t.HandoffPending || !IsMe(t.HandoffFrom)) return $"{t.Callsign}: передачи нет";
        string to = t.HandoffTo;
        ClearHandoff(t);
        Changed(t);
        await Send(AtcPackets.Coordination(Me, to, "HC", t.Callsign)).ConfigureAwait(false);
        return null;
    }

    public async Task<string?> PointOutAsync(Track t, string to)
    {
        to = to.Trim().ToUpperInvariant();
        if (!IsConnected) return "Точка передачи работает только в сети";
        if (!_controllers.ContainsKey(to)) return $"{to} не в сети";
        await Send(AtcPackets.Coordination(Me, to, "PT", t.Callsign)).ConfigureAwait(false);
        return null;
    }

    /// <summary>Set an annotation locally and share it with the other controllers. "" clears it.</summary>
    public async Task AnnotateAsync(Track t, Annotation a, string value)
    {
        Apply(t, a, value);
        Changed(t);
        await Send(AtcPackets.Shared(Me, AnnotationCodes[a], t.Callsign, value)).ConfigureAwait(false);
    }

    /// <summary>".wallop": a text request to every supervisor online.</summary>
    public async Task SendSupervisorRequestAsync(string text)
    {
        var fsd = _fsd ?? throw new InvalidOperationException("Нет подключения к сети");
        await fsd.SendAsync(AtcPackets.TextMessage(Me, "*S", text)).ConfigureAwait(false);
        Raise(new AtcMessage(MessageChannel.Broadcast, Me, "[супервайзеру] " + text, _clock(), Outgoing: true));
    }

    /// <summary>Sends a flight plan amendment to the server and applies it locally.</summary>
    public async Task AmendFlightPlanAsync(Track t, FiledPlan plan)
    {
        t.ApplyFlightPlan(plan);
        FlightPlanUpdated?.Invoke(this, t);
        await Send(AtcPackets.Amend(Me, plan)).ConfigureAwait(false);
    }

    internal static void Apply(Track t, Annotation a, string value)
    {
        value = value.Trim();
        int? number = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        switch (a)
        {
            case Annotation.ClearedAltitude: t.ClearedAltitude = number is > 0 ? number : null; break;
            case Annotation.Heading: t.AssignedHeading = number is > 0 ? number : null; break;
            case Annotation.Speed: t.AssignedSpeed = number is > 0 ? number : null; break;
            case Annotation.Squawk: t.AssignedSquawk = number is >= 0 && value.Length == 4 ? number : null; break;
            case Annotation.Scratchpad: t.Scratchpad = value; break;
            case Annotation.Sid: t.Sid = value.ToUpperInvariant(); break;
            case Annotation.Star: t.Star = value.ToUpperInvariant(); break;
            case Annotation.DepartureRunway: t.DepartureRunway = value.ToUpperInvariant(); break;
            case Annotation.ArrivalRunway: t.ArrivalRunway = value.ToUpperInvariant(); break;
            case Annotation.Clearance: t.ClearanceReceived = value is "1" or "Y"; break;
            case Annotation.GroundState: t.GroundState = value.ToUpperInvariant(); break;
        }
    }

    // ---- incoming ---------------------------------------------------------------------------------

    /// <summary>Handles coordination packets; returns false for everything else.</summary>
    private bool OnCoordinationPacket(FsdPacket p)
    {
        switch (p.Command)
        {
            case "$CQ" when p.Fields.Length >= 4 && p[1] == AtcPackets.AllControllers && !IsMe(p[0]):
                OnShared(p[0], p[2], p[3], p.Fields.Length > 4 ? string.Join(':', p.Fields.Skip(4)) : "");
                return true;
            case "$CQ" when p.Fields.Length >= 3 && IsMe(p[1]) && p[2] == "ATIS":
                var lines = ControllerInfo?.Invoke() ?? [];
                foreach (var line in AtcPackets.AtisReply(Me, p[0], lines)) _ = Send(line);
                return true;
            case "$HO" when p.Fields.Length >= 3 && IsMe(p[1]):
            {
                var t = _tracks.GetOrAdd(p[2], cs => new Track(cs));
                t.HandoffFrom = p[0].ToUpperInvariant();
                t.HandoffTo = Me;
                Changed(t);
                Coordination?.Invoke(this, new CoordinationEvent(CoordinationKind.HandoffRequested, t, t.HandoffFrom));
                return true;
            }
            case "$HA" when p.Fields.Length >= 3 && IsMe(p[1]):
                if (_tracks.TryGetValue(p[2], out var accepted))
                {
                    ClearHandoff(accepted);
                    SetOwner(accepted, p[0]);
                    Changed(accepted);
                    Coordination?.Invoke(this, new CoordinationEvent(CoordinationKind.HandoffAccepted, accepted, p[0].ToUpperInvariant()));
                }
                return true;
            case "#PC" when p.Fields.Length >= 5 && IsMe(p[1]) && p[2] == "CCP":
                if (!_tracks.TryGetValue(p[4], out var tr)) return true;
                string peer = p[0].ToUpperInvariant();
                if (p[3] == "HC" && tr.HandoffPending)
                {
                    bool wasMine = IsMe(tr.HandoffFrom);
                    ClearHandoff(tr);
                    Changed(tr);
                    Coordination?.Invoke(this, new CoordinationEvent(wasMine ? CoordinationKind.HandoffRefused : CoordinationKind.HandoffCancelled, tr, peer));
                }
                else if (p[3] == "PT")
                {
                    Coordination?.Invoke(this, new CoordinationEvent(CoordinationKind.PointOut, tr, peer));
                }
                return true;
        }
        return false;
    }

    private void OnShared(string from, string kind, string callsign, string value)
    {
        from = from.ToUpperInvariant();
        if (kind == "WH")
        {
            // Someone asks who has the aircraft: the owner answers with everything it knows.
            if (_tracks.TryGetValue(callsign, out var mine) && mine.IsTracked) _ = AnnounceAsync(mine);
            return;
        }
        var t = _tracks.GetOrAdd(callsign, cs => new Track(cs));
        switch (kind)
        {
            case "IT":
                if (t.HandoffPending && !IsMe(t.HandoffTo) && !IsMe(t.HandoffFrom)) ClearHandoff(t);
                SetOwner(t, from);
                Coordination?.Invoke(this, new CoordinationEvent(CoordinationKind.OwnerChanged, t, from));
                break;
            case "DR":
                if (t.Owner.Equals(from, StringComparison.OrdinalIgnoreCase))
                {
                    SetOwner(t, "");
                    ClearHandoff(t);
                    Coordination?.Invoke(this, new CoordinationEvent(CoordinationKind.OwnerChanged, t, from));
                }
                break;
            default:
                var a = AnnotationCodes.FirstOrDefault(kv => kv.Value == kind);
                if (a.Value == null) return;
                Apply(t, a.Key, value);
                break;
        }
        Changed(t);
    }

    /// <summary>Tell everyone we own the aircraft and share our annotations.</summary>
    private async Task AnnounceAsync(Track t)
    {
        await Send(AtcPackets.Shared(Me, "IT", t.Callsign)).ConfigureAwait(false);
        foreach (var (a, value) in Annotations(t))
            await Send(AtcPackets.Shared(Me, AnnotationCodes[a], t.Callsign, value)).ConfigureAwait(false);
    }

    private static IEnumerable<(Annotation, string)> Annotations(Track t)
    {
        var inv = CultureInfo.InvariantCulture;
        if (t.ClearedAltitude is { } c) yield return (Annotation.ClearedAltitude, c.ToString(inv));
        if (t.AssignedHeading is { } h) yield return (Annotation.Heading, h.ToString(inv));
        if (t.AssignedSpeed is { } s) yield return (Annotation.Speed, s.ToString(inv));
        if (t.AssignedSquawk is { } q) yield return (Annotation.Squawk, q.ToString("0000", inv));
        if (t.Scratchpad.Length > 0) yield return (Annotation.Scratchpad, t.Scratchpad);
        if (t.Sid.Length > 0) yield return (Annotation.Sid, t.Sid);
        if (t.Star.Length > 0) yield return (Annotation.Star, t.Star);
        if (t.DepartureRunway.Length > 0) yield return (Annotation.DepartureRunway, t.DepartureRunway);
        if (t.ArrivalRunway.Length > 0) yield return (Annotation.ArrivalRunway, t.ArrivalRunway);
        if (t.ClearanceReceived) yield return (Annotation.Clearance, "1");
        if (t.GroundState.Length > 0) yield return (Annotation.GroundState, t.GroundState);
    }

    /// <summary>A controller left: whatever it owned becomes free and its pending handoffs lapse.</summary>
    private void OnControllerGone(string callsign)
    {
        foreach (var t in _tracks.Values)
        {
            bool changed = false;
            if (t.Owner.Equals(callsign, StringComparison.OrdinalIgnoreCase)) { SetOwner(t, ""); changed = true; }
            if (t.HandoffPending && (t.HandoffFrom.Equals(callsign, StringComparison.OrdinalIgnoreCase) ||
                                     t.HandoffTo.Equals(callsign, StringComparison.OrdinalIgnoreCase)))
            {
                ClearHandoff(t);
                changed = true;
            }
            if (changed) Changed(t);
        }
    }
}
