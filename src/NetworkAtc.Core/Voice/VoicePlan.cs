using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Session;
using NetworkAtc.Plugins;
using SkyNetwork.Voice;

namespace NetworkAtc.Core.Voice;

/// <summary>
/// What the voice server is told about the controller: radios from the primary and extra frequencies,
/// antenna sites from the visibility centres, audio settings from the profile. The UI hands the
/// results to <see cref="VoiceClient"/>.
/// </summary>
public static class VoicePlan
{
    /// <summary>A controller has up to four visibility centres, each is an antenna site.</summary>
    public const int MaxSites = 4;
    /// <summary>Ground station antennas: a mast of about 100 ft.</summary>
    public const double SiteAltitudeFeet = 100;

    /// <summary>A real position may transmit; observers (OBS facility or rating, no frequency) only listen.</summary>
    public static bool CanTransmit(AtcConnectInfo info) =>
        info.Facility != Facility.Observer && info.Rating > 1 && IsAirband(info.FrequencyKhz);

    private static bool IsAirband(int khz) => khz is >= 118000 and <= 136990;

    /// <summary>
    /// The primary frequency first, then the extra ones (duplicates, invalid and switched-off entries
    /// are skipped). Transmit is dropped when the controller may not transmit. The server takes eight
    /// transceivers, one per radio and site, so the list is cut to fit <paramref name="siteCount"/>.
    /// </summary>
    public static IReadOnlyList<Radio> Radios(int primaryKhz, bool canTransmit, VoiceOptions options, int siteCount = 1)
    {
        var radios = new List<Radio>();
        var seen = new HashSet<uint>();
        if (IsAirband(primaryKhz) && (options.PrimaryReceive || options.PrimaryTransmit))
        {
            uint hz = (uint)primaryKhz * 1000;
            seen.Add(hz);
            radios.Add(new Radio(hz, options.PrimaryReceive, canTransmit && options.PrimaryTransmit, Volume(options.PrimaryVolume)));
        }
        foreach (var f in options.Frequencies)
        {
            if (!f.Receive && !f.Transmit || !Frequency.TryParse(f.Frequency, out var khz)) continue;
            uint hz = (uint)khz * 1000;
            if (seen.Add(hz)) radios.Add(new Radio(hz, f.Receive, canTransmit && f.Transmit, Volume(f.Volume)));
        }
        int max = Protocol.MaxTransceivers / Math.Clamp(siteCount, 1, Protocol.MaxTransceivers);
        return radios.Count > max ? radios.Take(max).ToList() : radios;
    }

    /// <summary>Antenna sites at the visibility centres (up to four, near-duplicates merged); the fallback if there are none.</summary>
    public static IReadOnlyList<AntennaSite> Sites(IEnumerable<GeoPoint> visibilityCenters, GeoPoint? fallback)
    {
        var points = new List<GeoPoint>();
        foreach (var p in visibilityCenters)
        {
            if (!IsKnown(p) || points.Any(q => Math.Abs(q.Latitude - p.Latitude) < 0.01 && Math.Abs(q.Longitude - p.Longitude) < 0.01)) continue;
            points.Add(p);
            if (points.Count == MaxSites) break;
        }
        if (points.Count == 0 && fallback is { } f && IsKnown(f)) points.Add(f);
        return points.Select(p => new AntennaSite(p.Latitude, p.Longitude, SiteAltitudeFeet)).ToList();
    }

    /// <summary>Where to put the antenna without a visibility centre: the first active airport, else the sector centre.</summary>
    public static GeoPoint? FallbackSite(SectorFile? sector, IReadOnlyList<string> activeAirports)
    {
        if (sector == null) return null;
        foreach (var icao in activeAirports)
            if (sector.Airports.FirstOrDefault(a => a.Name.Equals(icao, StringComparison.OrdinalIgnoreCase)) is { } airport)
                return airport.Position;
        return IsKnown(sector.Center) ? sector.Center : null;
    }

    private static bool IsKnown(GeoPoint p) =>
        p != default && p.Latitude is >= -90 and <= 90 && p.Longitude is >= -180 and <= 180;

    private static float Volume(double v) => (float)Math.Clamp(v, 0, 1);

    /// <summary>Device index for the voice library: its position in the list, -1 (Windows default) if empty or gone.</summary>
    public static int DeviceIndex(IReadOnlyList<string> devices, string name) =>
        name.Length == 0 ? -1 : devices.ToList().FindIndex(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static VoiceSettings Settings(VoiceOptions options, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs) => new()
    {
        InputDevice = DeviceIndex(inputs, options.InputDevice),
        OutputDevice = DeviceIndex(outputs, options.OutputDevice),
        MicGain = (float)Math.Clamp(options.MicGain, 0, 4),
        OutputVolume = (float)Math.Clamp(options.OutputVolume, 0, 2),
        Ptt = PttBinding.Parse(options.PushToTalk),
    };
}

/// <summary>Stations heard right now, one frequency each: the RX line in the status bar and the radar highlight.</summary>
public sealed class HeardStations
{
    private readonly List<(string Callsign, uint FrequencyHz)> _items = [];

    public IReadOnlyList<(string Callsign, uint FrequencyHz)> Items => _items;

    /// <summary>A station started or stopped being heard. False if nothing changed.</summary>
    public bool Set(string callsign, uint frequencyHz, bool active)
    {
        int i = _items.FindIndex(x => x.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
        if (!active)
        {
            if (i < 0) return false;
            _items.RemoveAt(i);
            return true;
        }
        if (i >= 0 && _items[i].FrequencyHz == frequencyHz) return false;
        if (i >= 0) _items[i] = (callsign, frequencyHz);
        else _items.Add((callsign, frequencyHz));
        return true;
    }

    public bool Contains(string callsign) => _items.Any(x => x.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));

    public void Clear() => _items.Clear();

    /// <summary>"RX 118.100 AFL123  RX 124.300 SBI22".</summary>
    public string Describe() => string.Join("  ", _items.Select(x => $"RX {Radio.FormatMhz(x.FrequencyHz)} {x.Callsign}"));
}
