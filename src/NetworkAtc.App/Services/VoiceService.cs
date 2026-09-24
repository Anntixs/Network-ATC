using System.Windows.Input;
using System.Windows.Threading;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Voice;
using SkyNetwork.Voice;

namespace NetworkAtc.App.Services;

/// <summary>
/// Radio voice next to the network session: signs in with the same CID, password and callsign when the
/// controller goes online, keeps radios and antennas in step with the station and retries every 30 s
/// when the voice server fails. A voice failure never touches the network session. Events are raised
/// on the UI thread.
/// </summary>
public sealed class VoiceService : IDisposable
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly VoiceClient _client = new();
    private readonly Dispatcher _dispatcher;
    private readonly Func<VoiceOptions> _options;
    private readonly DispatcherTimer _retry;
    private readonly HeardStations _heard = new();
    private AtcConnectInfo? _info;
    private CancellationTokenSource? _connecting;
    private string _lastError = "";

    public VoiceService(Dispatcher dispatcher, Func<VoiceOptions> options)
    {
        _dispatcher = dispatcher;
        _options = options;
        _retry = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = RetryInterval };
        _retry.Tick += (_, _) => _ = ConnectAsync();
        _client.StateChanged += (state, reason) => _dispatcher.BeginInvoke(() => OnState(state, reason));
        _client.ReceiveActivity += (callsign, hz, on) => _dispatcher.BeginInvoke(() =>
        {
            if (_heard.Set(callsign, hz, on)) Changed?.Invoke();
        });
        _client.TransmitChanged += _ => _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    /// <summary>State, transmit or the stations heard changed.</summary>
    public event Action? Changed;

    /// <summary>Text for the message window; true = error.</summary>
    public event Action<string, bool>? Message;

    /// <summary>Voice should be up (the controller is online and voice is on).</summary>
    public bool IsActive => _info != null;
    public VoiceState State => _connecting != null ? VoiceState.Connecting : _client.State == VoiceState.Connected ? VoiceState.Connected : VoiceState.Disconnected;
    public string LastError => _lastError;
    public string Server => _info == null ? "" : $"{_info.Host}:{_options().Port}";
    public bool Transmitting => _client.Transmitting;
    public float MicLevel => _client.MicLevel;
    public HeardStations Heard => _heard;

    /// <summary>The network connection is up: connect voice too (unless it is switched off).</summary>
    public void Start(AtcConnectInfo info)
    {
        if (!_options().Enabled) return;
        _info = info;
        _lastError = "";
        _connecting?.Cancel();
        _ = ConnectAsync();
    }

    /// <summary>The network connection went down, or voice was switched off.</summary>
    public void Stop()
    {
        _info = null;
        _connecting?.Cancel();
        _retry.Stop();
        _client.Disconnect();
        _heard.Clear();
        _lastError = "";
        Changed?.Invoke();
    }

    /// <summary>Try again now instead of waiting for the next retry.</summary>
    public void Reconnect()
    {
        if (_info == null || _connecting != null) return;
        _lastError = "";
        _ = ConnectAsync();
    }

    public void Update(IReadOnlyList<Radio> radios, IReadOnlyList<AntennaSite> sites)
    {
        _client.SetSites(sites);
        _client.SetRadios(radios);
    }

    /// <summary>Devices, gains and push-to-talk from the profile.</summary>
    public void ApplySettings()
    {
        var (inputs, outputs) = Devices();
        _client.ApplySettings(VoicePlan.Settings(_options(), inputs, outputs));
    }

    public void SetManualPtt(bool down) => _client.SetManualPtt(down);

    /// <summary>Microphones and speakers; empty if the audio system cannot list them.</summary>
    public static (IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs) Devices()
    {
        try
        {
            return VoiceClient.Devices();
        }
        catch (Exception)
        {
            return ([], []);
        }
    }

    private async Task ConnectAsync()
    {
        var info = _info;
        if (info == null || _connecting != null) return;
        var cts = _connecting = new CancellationTokenSource();
        int port = _options().Port;
        _retry.Stop();
        Changed?.Invoke();
        try
        {
            await _client.ConnectAsync(info.Host, port, (uint)info.Cid, info.Callsign, info.Password, cts.Token);
            if (ReferenceEquals(_info, info))
            {
                _lastError = "";
                Message?.Invoke($"Голосовая связь: подключено ({info.Host}:{port})", false);
            }
            else
            {
                _client.Disconnect(); // went offline (or reconnected elsewhere) while signing in
            }
        }
        catch (Exception e)
        {
            // Whatever goes wrong with voice stays here: the network session carries on without it.
            if (ReferenceEquals(_info, info) && !cts.IsCancellationRequested) Fail(e.Message);
        }
        finally
        {
            _connecting = null;
            cts.Dispose();
        }
        if (_info != null && !ReferenceEquals(_info, info))
        {
            // A new network connection started while this attempt was running.
            _ = ConnectAsync();
            return;
        }
        UpdateRetry();
        Changed?.Invoke();
    }

    private void OnState(VoiceState state, string reason)
    {
        if (state == VoiceState.Disconnected) _heard.Clear();
        if (reason.Length > 0 && _info != null)
        {
            if (state == VoiceState.Connected) Message?.Invoke("Голосовая связь: " + reason, true); // audio device problem
            else Fail(reason);
        }
        UpdateRetry();
        Changed?.Invoke();
    }

    private void Fail(string reason)
    {
        if (reason == _lastError) return;
        _lastError = reason;
        Message?.Invoke($"Голосовая связь недоступна: {reason}. Повтор каждые 30 с, «Переподключить» — в окне РАДИО", true);
    }

    private void UpdateRetry()
    {
        bool retry = _info != null && _connecting == null && _client.State != VoiceState.Connected;
        if (retry && !_retry.IsEnabled) _retry.Start();
        else if (!retry) _retry.Stop();
    }

    public void Dispose()
    {
        _info = null;
        _connecting?.Cancel();
        _retry.Stop();
        _client.Dispose();
    }
}

/// <summary>The push-to-talk key against the program's own keyboard shortcuts.</summary>
public static class PttKeys
{
    /// <summary>True if the WPF key is the keyboard push-to-talk control.</summary>
    public static bool Matches(PttBinding binding, Key key) =>
        binding.Kind == PttKind.Keyboard && key != Key.None && KeyInterop.VirtualKeyFromKey(key) == binding.Code;

    /// <summary>Keys that type into a text box: there they keep typing while also keying the radio.</summary>
    public static bool TypesText(Key key) => key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9
        or >= Key.NumPad0 and <= Key.Divide or >= Key.Oem1 and <= Key.Oem102 or Key.Space or Key.Back or Key.Delete;

    /// <summary>The shortcut (action name) bound to the same key without modifiers, if any.</summary>
    public static string? ConflictingAction(PttBinding binding, IReadOnlyDictionary<string, string> keyBindings)
    {
        foreach (var (action, gesture) in keyBindings)
            if (KeyBindingParser.TryParse(gesture, out var key, out var mods) && mods == ModifierKeys.None && Matches(binding, key))
                return action;
        return null;
    }
}
