using System.Globalization;
using NetworkAtc.Plugins;

namespace SamplePlugin;

/// <summary>
/// Shows every extension point of the Network-ATC plugin API:
///  - tag field {dist}: distance from a reference airport;
///  - command ".dist AFL123";
///  - overlay "Кольцо 5 NM": a 5 NM ring around the selected aircraft;
///  - aircraft menu item "Подсветить" that toggles a highlight color.
/// </summary>
public sealed class DistancePlugin : IAtcPlugin
{
    // Sheremetyevo. A real plugin would read this from a file in host.DataDirectory.
    private static readonly GeoPoint Reference = new(55.9728, 37.4147);

    private readonly HashSet<string> _highlighted = [];
    private IPluginHost _host = null!;

    public string Name => "Sample: distance";
    public string Version => "1.0";
    public string Author => "SkyNetwork";

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.RegisterTagField("dist", "Расстояние до UUEE, NM", a => Distance(a.Position).ToString("0", CultureInfo.InvariantCulture));
        host.RegisterCommand("dist", "расстояние от борта до UUEE", args =>
        {
            var target = args.Count > 0
                ? host.Aircraft.FirstOrDefault(a => a.Callsign.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                : host.SelectedAircraft;
            return target == null ? "Борт не найден" : $"{target.Callsign}: {Distance(target.Position):0.0} NM до UUEE";
        });
        host.RegisterOverlay(new SelectedRing(host));
        host.RegisterAircraftAction("Подсветить", a =>
        {
            bool on = _highlighted.Add(a.Callsign);
            if (!on) _highlighted.Remove(a.Callsign);
            host.SetHighlight(a.Callsign, on ? "#F5A524" : null);
        });
        host.Log("загружен");
    }

    private static double Distance(GeoPoint p)
    {
        const double rad = Math.PI / 180;
        double dLat = (p.Latitude - Reference.Latitude) * rad, dLon = (p.Longitude - Reference.Longitude) * rad;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Reference.Latitude * rad) * Math.Cos(p.Latitude * rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * 3440.065 * Math.Asin(Math.Sqrt(h));
    }

    private sealed class SelectedRing(IPluginHost host) : IRadarOverlay
    {
        public string Name => "Кольцо 5 NM";

        public void Draw(IRadarCanvas canvas)
        {
            if (host.SelectedAircraft is { } a) canvas.Circle(a.Position, 5, "#804FB3FF", 1);
        }
    }
}
