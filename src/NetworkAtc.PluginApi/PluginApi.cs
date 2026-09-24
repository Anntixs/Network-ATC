namespace NetworkAtc.Plugins;

/// <summary>Version of the plugin API. Plugins built for a higher major version are not loaded.</summary>
public static class PluginApiVersion
{
    public const int Major = 1;
    public const int Minor = 2;
}

/// <summary>
/// Entry point of a Network-ATC plugin. Put a public class implementing this interface into a
/// .NET 8 class library, copy the DLL to the plugins folder and it is loaded at start-up.
/// </summary>
public interface IAtcPlugin
{
    string Name { get; }
    string Version { get; }
    string Author => "";

    /// <summary>Called once after loading. Register tag fields, commands, overlays and events here.</summary>
    void Initialize(IPluginHost host);

    /// <summary>Called when the application closes or the plugin is unloaded.</summary>
    void Shutdown() { }
}

public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>An aircraft on the radar, read-only for plugins.</summary>
public interface IAircraft
{
    string Callsign { get; }
    GeoPoint Position { get; }
    /// <summary>True altitude, feet.</summary>
    int Altitude { get; }
    int GroundSpeed { get; }
    double Heading { get; }
    /// <summary>Vertical speed, feet per minute (computed from position reports).</summary>
    int VerticalSpeed { get; }
    int Squawk { get; }
    bool ModeC { get; }
    bool Ident { get; }
    bool OnGround { get; }

    string AircraftType { get; }
    string Departure { get; }
    string Destination { get; }
    string Route { get; }
    string FiledAltitude { get; }
    bool HasFlightPlan { get; }

    /// <summary>Controller annotations (local).</summary>
    int? ClearedAltitude { get; }
    int? AssignedHeading { get; }
    int? AssignedSpeed { get; }
    int? AssignedSquawk { get; }
    string Scratchpad { get; }
    bool IsTracked { get; }

    /// <summary>Callsign of the controller that has the aircraft assumed, or "" (API 1.2).</summary>
    string Owner { get; }
    /// <summary>Pending transfer of control, "" when none (API 1.2).</summary>
    string HandoffFrom { get; }
    string HandoffTo { get; }
    /// <summary>Manually assigned SID/STAR and runways, "" when automatic (API 1.2).</summary>
    string Sid { get; }
    string Star { get; }
    string DepartureRunway { get; }
    string ArrivalRunway { get; }
    bool ClearanceReceived { get; }
}

public enum MessageChannel
{
    Radio,
    Private,
    Server,
    Broadcast,
}

public sealed record PluginMessage(MessageChannel Channel, string From, string Text, int? FrequencyKhz);

/// <summary>Drawing surface for overlays. Coordinates are geographic; colors are "#RRGGBB" or "#AARRGGBB".</summary>
public interface IRadarCanvas
{
    /// <summary>Current scale, nautical miles per screen pixel.</summary>
    double NmPerPixel { get; }
    void Line(GeoPoint from, GeoPoint to, string color, double width = 1);
    void Polyline(IReadOnlyList<GeoPoint> points, string color, double width = 1, bool closed = false);
    void Polygon(IReadOnlyList<GeoPoint> points, string fill, string? stroke = null);
    void Circle(GeoPoint center, double radiusNm, string color, double width = 1);
    void Text(GeoPoint at, string text, string color, double size = 11);
}

public interface IRadarOverlay
{
    /// <summary>Shown in the layer list; the user can switch the overlay on and off.</summary>
    string Name { get; }
    void Draw(IRadarCanvas canvas);
}

/// <summary>What the application offers to plugins. All members are thread-safe.</summary>
public interface IPluginHost
{
    /// <summary>Callsign of the controller, empty when offline.</summary>
    string ControllerCallsign { get; }
    bool IsConnected { get; }
    IReadOnlyCollection<IAircraft> Aircraft { get; }
    IAircraft? SelectedAircraft { get; }

    /// <summary>Folder where the plugin may keep its own files.</summary>
    string DataDirectory { get; }

    event EventHandler<IAircraft>? AircraftUpdated;
    event EventHandler<string>? AircraftRemoved;
    event EventHandler<IAircraft>? FlightPlanUpdated;
    event EventHandler<PluginMessage>? MessageReceived;
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>A value that can be used in tag templates as {key}.</summary>
    void RegisterTagField(string key, string description, Func<IAircraft, string> value);

    /// <summary>
    /// Make a tag field clickable (API 1.1). The handler gets the aircraft and true for a right click.
    /// Users can still rebind the field to another action in the settings.
    /// </summary>
    void RegisterTagFieldClick(string key, Action<IAircraft, bool> onClick);

    /// <summary>A command typed as ".name args". Return feedback text or null.</summary>
    void RegisterCommand(string name, string description, Func<IReadOnlyList<string>, string?> handler);

    void RegisterOverlay(IRadarOverlay overlay);

    /// <summary>An entry in the aircraft context menu.</summary>
    void RegisterAircraftAction(string title, Action<IAircraft> action);

    /// <summary>Highlight an aircraft with a color (null clears).</summary>
    void SetHighlight(string callsign, string? color);

    Task SendRadioMessageAsync(string text);
    Task SendPrivateMessageAsync(string to, string text);

    /// <summary>Write to the message area (visible only to the controller).</summary>
    void Log(string text);
}
