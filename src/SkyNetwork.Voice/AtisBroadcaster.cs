using System.Diagnostics;

namespace SkyNetwork.Voice;

/// <summary>
/// Plays a recorded ATIS on the air in a loop, in real time: 20 ms frames, the transmitter keyed for the
/// length of the recording, then a pause, then again. A new recording starts with the next repeat.
/// </summary>
public sealed class AtisBroadcaster : IDisposable
{
    private static readonly TimeSpan FrameTime = TimeSpan.FromMilliseconds(20);
    private readonly Transmitter _transmitter;
    private readonly float[] _frame = new float[AudioFormat.FrameSamples];
    private readonly object _lock = new();
    private float[] _next = [];
    private float[] _current = [];
    private int _position;
    private int _pauseFrames;
    private TimeSpan _due;
    private CancellationTokenSource? _loop;

    public AtisBroadcaster(IAudioSender sender, IReadOnlyList<byte> transmitters)
    {
        _transmitter = new Transmitter(sender);
        _transmitter.SetTransmitters(transmitters);
    }

    /// <summary>Silence between two repeats.</summary>
    public TimeSpan Pause { get; set; } = TimeSpan.FromSeconds(2);

    public bool OnAir => _transmitter.Transmitting;

    /// <summary>The recording (48 kHz mono); empty stops the broadcast after the current repeat.</summary>
    public void SetRecording(float[] samples)
    {
        lock (_lock) _next = samples;
    }

    /// <summary>Sends every frame due by <paramref name="now"/> (time since the broadcast started).</summary>
    public void Pump(TimeSpan now)
    {
        lock (_lock)
        {
            // After a long stall (sleep, debugger) do not rush out the missed audio.
            if (now - _due > TimeSpan.FromSeconds(1)) _due = now;
            while (_due <= now)
            {
                Step();
                _due += FrameTime;
            }
        }
    }

    private void Step()
    {
        if (_pauseFrames > 0)
        {
            _pauseFrames--;
            return;
        }
        if (_position == 0)
        {
            _current = _next;
            if (_current.Length == 0) return;
            _transmitter.Key(true);
        }
        int n = Math.Min(_frame.Length, _current.Length - _position);
        _current.AsSpan(_position, n).CopyTo(_frame);
        _frame.AsSpan(n).Clear();
        _transmitter.AddSamples(_frame);
        _position += n;
        if (_position < _current.Length) return;
        _transmitter.Key(false);
        _position = 0;
        _pauseFrames = (int)(Pause / FrameTime);
    }

    /// <summary>Plays in the background until <see cref="Stop"/>.</summary>
    public void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        _loop = cts;
        var clock = Stopwatch.StartNew();
        lock (_lock) _due = TimeSpan.Zero;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                Pump(clock.Elapsed);
                try { await Task.Delay(10, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop = null;
        lock (_lock)
        {
            if (_transmitter.Transmitting) _transmitter.Key(false);
            _position = 0;
            _pauseFrames = 0;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>A voice ATIS on the network: its own voice connection (UUEE_ATIS) with one transmitter at the airport.</summary>
public sealed class AtisVoiceStation : IDisposable
{
    private VoiceConnection? _connection;
    private AtisBroadcaster? _broadcaster;
    private float[] _recording = [];

    public string Callsign { get; private set; } = "";
    public bool IsConnected => _connection?.IsConnected == true;
    public bool OnAir => _broadcaster?.OnAir == true;

    /// <summary>The voice connection closed (the reason; empty when we closed it).</summary>
    public event Action<string>? Closed;

    public async Task ConnectAsync(string host, int port, uint cid, string callsign, string password, uint frequencyHz, AntennaSite site,
        CancellationToken ct = default)
    {
        Disconnect();
        var connection = new VoiceConnection(host, port);
        try
        {
            await connection.ConnectAsync(cid, callsign, password, ct).ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        Callsign = callsign;
        connection.SetTransceivers([new Transceiver(0, frequencyHz, site.Latitude, site.Longitude, site.AltitudeFeet)]);
        connection.Closed += reason =>
        {
            if (!ReferenceEquals(_connection, connection)) return;
            _broadcaster?.Dispose();
            _broadcaster = null;
            _connection = null;
            Closed?.Invoke(reason);
        };
        _connection = connection;
        _broadcaster = new AtisBroadcaster(connection, [0]);
        _broadcaster.SetRecording(_recording);
        _broadcaster.Start();
    }

    /// <summary>What is played (48 kHz mono); takes effect from the next repeat.</summary>
    public void SetRecording(float[] samples)
    {
        _recording = samples;
        _broadcaster?.SetRecording(samples);
    }

    public void SetFrequency(uint frequencyHz, AntennaSite site) =>
        _connection?.SetTransceivers([new Transceiver(0, frequencyHz, site.Latitude, site.Longitude, site.AltitudeFeet)]);

    public void Disconnect()
    {
        var c = _connection;
        _connection = null;
        _broadcaster?.Dispose();
        _broadcaster = null;
        c?.Dispose();
    }

    public void Dispose() => Disconnect();
}
