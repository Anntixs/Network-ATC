using NetworkAtc.Core.Atis;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Session;
using NetworkAtc.Plugins;
using SkyNetwork.Voice;

namespace NetworkAtc.App.Services;

/// <summary>
/// The ATIS stations on the air: each is its own network connection (UUEE_ATIS) answering pilots' text
/// requests and, with a voice mode, its own voice connection playing the ATIS in a loop on the frequency.
/// The spoken ATIS is made again whenever the letter or the weather changes.
/// </summary>
public sealed class AtisManager(
    AtisService service,
    Func<Profile> profile,
    Func<AtcConnectInfo?> controller,
    Func<string, GeoPoint?> airportPosition,
    Action<string, bool> message)
{
    private sealed class Running(AtisSettings settings, AtisStation fsd)
    {
        public AtisSettings Settings { get; } = settings;
        public AtisStation Fsd { get; } = fsd;
        public AtisVoiceStation? Voice { get; set; }
        public int Version;
    }

    private readonly Dictionary<string, Running> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A station connected, disconnected or went on the air; raised on any thread.</summary>
    public event Action? Changed;

    public AtisService Service => service;

    public bool IsConnected(AtisSettings s)
    {
        lock (_running) return _running.ContainsKey(s.Callsign);
    }

    public int ConnectedCount
    {
        get { lock (_running) return _running.Count; }
    }

    public string Status(AtisSettings s)
    {
        Running? r;
        lock (_running) _running.TryGetValue(s.Callsign, out r);
        if (r == null) return "not connected";
        if (r.Voice == null) return "online, text";
        return r.Voice.OnAir ? "online, on air" : r.Voice.IsConnected ? "online, voice starting" : "online, voice off";
    }

    public async Task ConnectAsync(AtisSettings s)
    {
        if (controller() is not { } ctl) throw new InvalidOperationException("Connect to the network first");
        if (s.Airport.Trim().Length != 4) throw new InvalidOperationException("Enter the airport (4-letter ICAO)");
        if (!Frequency.TryParse(s.Frequency, out var khz)) throw new InvalidOperationException("Enter the ATIS frequency, e.g. 128.050");
        if (IsConnected(s)) return;
        var at = airportPosition(s.Airport.ToUpperInvariant()) ?? ctl.Center;
        var station = new AtisStation(s.Callsign, () => service.Text(s));
        station.Disconnected += (_, reason) =>
        {
            Remove(s.Callsign);
            if (reason.Length > 0) message($"{s.Callsign}: {reason}", true);
        };
        station.Requested += (_, pilot) => message($"{s.Callsign}: ATIS requested by {pilot}", false);
        await station.ConnectAsync(ctl, khz, at);
        var running = new Running(s, station);
        lock (_running) _running[s.Callsign] = running;
        message($"{s.Callsign} connected on {Frequency.Format(khz)}", false);
        Changed?.Invoke();

        if (s.Voice == AtisVoiceMode.None) return;
        var voice = new AtisVoiceStation();
        voice.Closed += reason =>
        {
            running.Voice = null;
            if (reason.Length > 0) message($"{s.Callsign}: voice off: {reason}", true);
            Changed?.Invoke();
        };
        try
        {
            await voice.ConnectAsync(ctl.Host, profile().Voice.Port, (uint)ctl.Cid, s.Callsign, ctl.Password, Radio.ParseMhz(s.Frequency),
                new AntennaSite(at.Latitude, at.Longitude, 100));
            running.Voice = voice;
            UpdateVoice(s);
        }
        catch (Exception ex) when (ex is VoiceException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            voice.Dispose();
            message($"{s.Callsign}: voice server unavailable ({ex.Message}), ATIS is text-only", true);
        }
        Changed?.Invoke();
    }

    /// <summary>Makes the voice again (new letter, weather, text or recording) for a station on the air.</summary>
    public void UpdateVoice(AtisSettings s)
    {
        Running? r;
        lock (_running) _running.TryGetValue(s.Callsign, out r);
        if (r?.Voice is not { } voice) return;
        int version = Interlocked.Increment(ref r.Version);
        if (s.Voice == AtisVoiceMode.Recording)
        {
            var samples = AtisAudio.LoadWav(s.RecordingFile);
            if (samples.Length == 0) message($"{s.Callsign}: no ATIS recording; record one in the ATIS window", true);
            voice.SetRecording(samples);
            return;
        }
        string text = service.Speech(s);
        _ = Task.Run(() =>
        {
            float[] samples;
            try
            {
                samples = AtisAudio.Speak(text, s.Language, s.SpeechVoice, s.SpeechRate);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                message($"{s.Callsign}: speech synthesis failed: {ex.Message}", true);
                return;
            }
            if (samples.Length == 0) message($"{s.Callsign}: Windows has no voice for language \"{s.Language}\"", true);
            // Only the newest version goes on the air.
            if (version == Volatile.Read(ref r.Version)) voice.SetRecording(samples);
            Changed?.Invoke();
        });
    }

    /// <summary>The letter or the weather of an airport changed: the ATIS of that airport speaks again.</summary>
    public void Refresh(string airport)
    {
        List<AtisSettings> list;
        lock (_running) list = _running.Values.Select(r => r.Settings).Where(s => s.Airport.Equals(airport, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var s in list) if (s.Voice == AtisVoiceMode.Speech) UpdateVoice(s);
    }

    public async Task DisconnectAsync(AtisSettings s)
    {
        Running? r;
        lock (_running) _running.TryGetValue(s.Callsign, out r);
        if (r == null) return;
        r.Voice?.Dispose();
        r.Voice = null;
        await r.Fsd.DisconnectAsync();
        Remove(s.Callsign);
        message($"{s.Callsign} disconnected", false);
    }

    public async Task DisconnectAllAsync()
    {
        List<Running> all;
        lock (_running) all = [.. _running.Values];
        foreach (var r in all) await DisconnectAsync(r.Settings);
    }

    private void Remove(string callsign)
    {
        Running? r;
        lock (_running)
        {
            if (!_running.Remove(callsign, out r)) return;
        }
        r.Voice?.Dispose();
        _ = r.Fsd.DisposeAsync();
        Changed?.Invoke();
    }
}
