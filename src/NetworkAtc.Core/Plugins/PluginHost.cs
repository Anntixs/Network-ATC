using System.Collections.Concurrent;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Tags;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Plugins;

public sealed record PluginCommand(string Name, string Description, string Owner, Func<IReadOnlyList<string>, string?> Handler);

public sealed record AircraftAction(string Title, string Owner, Action<IAircraft> Action);

public sealed record RegisteredOverlay(IRadarOverlay Overlay, string Owner);

/// <summary>Shared registry of everything plugins contributed.</summary>
public sealed class PluginRegistry
{
    public ConcurrentDictionary<string, PluginCommand> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AircraftAction> AircraftActions { get; } = [];
    public List<RegisteredOverlay> Overlays { get; } = [];
    public event EventHandler<string>? Log;
    internal void RaiseLog(string text) => Log?.Invoke(this, text);
    public event EventHandler? Changed;
    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>The <see cref="IPluginHost"/> given to one plugin.</summary>
public sealed class PluginHost : IPluginHost
{
    private readonly AtcSession _session;
    private readonly TagFields _fields;
    private readonly PluginRegistry _registry;
    private readonly Func<IAircraft?> _selected;
    private readonly string _owner;

    public PluginHost(string owner, AtcSession session, TagFields fields, PluginRegistry registry, Func<IAircraft?> selected, string dataDirectory)
    {
        _owner = owner;
        _session = session;
        _fields = fields;
        _registry = registry;
        _selected = selected;
        DataDirectory = dataDirectory;
        _session.TrackUpdated += (_, t) => Safe(() => AircraftUpdated?.Invoke(this, t));
        _session.TrackRemoved += (_, cs) => Safe(() => AircraftRemoved?.Invoke(this, cs));
        _session.FlightPlanUpdated += (_, t) => Safe(() => FlightPlanUpdated?.Invoke(this, t));
        _session.ConnectionChanged += (_, c) => Safe(() => ConnectionChanged?.Invoke(this, c));
        _session.MessageReceived += (_, m) =>
        {
            if (!m.Outgoing) Safe(() => MessageReceived?.Invoke(this, new PluginMessage(m.Channel, m.From, m.Text, m.FrequencyKhz)));
        };
    }

    public string ControllerCallsign => _session.Callsign;
    public bool IsConnected => _session.IsConnected;
    public IReadOnlyCollection<IAircraft> Aircraft => _session.Tracks;
    public IAircraft? SelectedAircraft => _selected();
    public string DataDirectory { get; }

    public event EventHandler<IAircraft>? AircraftUpdated;
    public event EventHandler<string>? AircraftRemoved;
    public event EventHandler<IAircraft>? FlightPlanUpdated;
    public event EventHandler<PluginMessage>? MessageReceived;
    public event EventHandler<bool>? ConnectionChanged;

    public void RegisterTagField(string key, string description, Func<IAircraft, string> value)
    {
        _fields.Add(key, $"{description} ({_owner})", value);
        _registry.RaiseChanged();
    }

    public void RegisterCommand(string name, string description, Func<IReadOnlyList<string>, string?> handler)
    {
        name = name.TrimStart('.');
        _registry.Commands[name] = new PluginCommand(name, description, _owner, handler);
        _registry.RaiseChanged();
    }

    public void RegisterOverlay(IRadarOverlay overlay)
    {
        lock (_registry.Overlays) _registry.Overlays.Add(new RegisteredOverlay(overlay, _owner));
        _registry.RaiseChanged();
    }

    public void RegisterAircraftAction(string title, Action<IAircraft> action)
    {
        lock (_registry.AircraftActions) _registry.AircraftActions.Add(new AircraftAction(title, _owner, action));
        _registry.RaiseChanged();
    }

    public void SetHighlight(string callsign, string? color)
    {
        if (_session.Find(callsign) is { } t) t.Highlight = color;
    }

    public Task SendRadioMessageAsync(string text) => _session.SendRadioAsync(text);
    public Task SendPrivateMessageAsync(string to, string text) => _session.SendPrivateAsync(to, text);
    public void Log(string text) => _registry.RaiseLog($"[{_owner}] {text}");

    /// <summary>Exceptions thrown by plugin event handlers must not break the session.</summary>
    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { _registry.RaiseLog($"[{_owner}] ошибка: {e.Message}"); }
    }
}
