using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NetworkAtc.Core.Weather;

/// <summary>The parts of a METAR a controller needs at a glance.</summary>
public sealed record Metar(string Station, string Raw, int? WindDirection, int WindSpeed, int? Gust, bool VariableWind,
    int? QnhHpa, double? AltimeterInHg)
{
    /// <summary>"240/12G22", "VRB03", "00000".</summary>
    public string Wind => VariableWind
        ? $"VRB{WindSpeed:00}"
        : $"{WindDirection ?? 0:000}/{WindSpeed:00}" + (Gust is { } g ? $"G{g}" : "");

    /// <summary>QNH in hPa, converted from inches of mercury when the METAR gives an A group.</summary>
    public int? Qnh => QnhHpa ?? (AltimeterInHg is { } a ? (int)Math.Round(a * 33.8639) : null);

    /// <summary>Altimeter setting in inches (A2992), converted from hPa when needed.</summary>
    public string Altimeter => AltimeterInHg is { } a ? a.ToString("0.00", CultureInfo.InvariantCulture)
        : QnhHpa is { } q ? (q / 33.8639).ToString("0.00", CultureInfo.InvariantCulture) : "";
}

public static partial class MetarParser
{
    [GeneratedRegex(@"^(?<dir>\d{3}|VRB)(?<spd>\d{2,3})(G(?<gust>\d{2,3}))?(?<unit>KT|MPS)$")]
    private static partial Regex WindGroup();

    [GeneratedRegex(@"^Q(?<q>\d{4})$")]
    private static partial Regex QnhGroup();

    [GeneratedRegex(@"^A(?<a>\d{4})$")]
    private static partial Regex AltimeterGroup();

    public static Metar? Parse(string raw)
    {
        var tokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count > 0 && tokens[0] is "METAR" or "SPECI") tokens.RemoveAt(0);
        if (tokens.Count < 2 || tokens[0].Length != 4) return null;
        int? dir = null, gust = null, qnh = null;
        int speed = 0;
        bool vrb = false, windFound = false;
        double? inHg = null;
        foreach (var t in tokens.Skip(1))
        {
            if (t == "RMK") break;
            if (!windFound && WindGroup().Match(t) is { Success: true } w)
            {
                windFound = true;
                double factor = w.Groups["unit"].Value == "MPS" ? 1.94384 : 1;
                vrb = w.Groups["dir"].Value == "VRB";
                if (!vrb) dir = int.Parse(w.Groups["dir"].Value, CultureInfo.InvariantCulture);
                speed = (int)Math.Round(int.Parse(w.Groups["spd"].Value, CultureInfo.InvariantCulture) * factor);
                if (w.Groups["gust"].Success) gust = (int)Math.Round(int.Parse(w.Groups["gust"].Value, CultureInfo.InvariantCulture) * factor);
            }
            else if (qnh == null && QnhGroup().Match(t) is { Success: true } q)
                qnh = int.Parse(q.Groups["q"].Value, CultureInfo.InvariantCulture);
            else if (inHg == null && AltimeterGroup().Match(t) is { Success: true } a)
                inHg = int.Parse(a.Groups["a"].Value, CultureInfo.InvariantCulture) / 100.0;
        }
        return new Metar(tokens[0].ToUpperInvariant(), raw.Trim(), dir, speed, gust, vrb, qnh, inHg);
    }
}

/// <summary>Keeps METARs of the airports the controller cares about, refreshed every 10 minutes.</summary>
public sealed class WeatherService : IDisposable
{
    public const string Source = "https://aviationweather.gov/api/data/metar?format=raw&ids=";

    private readonly ConcurrentDictionary<string, Metar> _metars = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly Func<IEnumerable<string>> _stations;
    private readonly Timer _timer;

    public WeatherService(Func<IEnumerable<string>> stations, HttpClient? http = null)
    {
        _stations = stations;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _timer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on a background thread when a METAR changes.</summary>
    public event EventHandler<Metar>? Updated;

    public bool Enabled { get; set; } = true;

    public void Start() => _timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(10));

    public Metar? Get(string station) => _metars.TryGetValue(station, out var m) ? m : null;

    /// <summary>Stores a METAR from any source (network, manual input, tests).</summary>
    public void Set(Metar m)
    {
        if (_metars.TryGetValue(m.Station, out var old) && old.Raw == m.Raw) return;
        _metars[m.Station] = m;
        Updated?.Invoke(this, m);
    }

    public async Task RefreshAsync(IEnumerable<string>? stations = null)
    {
        if (!Enabled) return;
        var ids = (stations ?? _stations()).Where(s => s.Length == 4).Select(s => s.ToUpperInvariant()).Distinct().ToList();
        if (ids.Count == 0) return;
        try
        {
            string text = await _http.GetStringAsync(Source + string.Join(',', ids)).ConfigureAwait(false);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (MetarParser.Parse(line) is { } m) Set(m);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Offline or the service is down: keep the last known METARs.
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
