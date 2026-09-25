using System.Collections.Concurrent;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Weather;

namespace NetworkAtc.Core.Atis;

/// <summary>
/// The controller's ATIS stations: their text and spoken ATIS of the moment, and the letter that moves on
/// by itself when a new METAR comes in (for stations with <see cref="AtisSettings.AutoLetter"/>).
/// </summary>
public sealed class AtisService
{
    private readonly Func<Profile> _profile;
    private readonly Func<string, string> _expand;
    private readonly Func<string, Metar?> _metar;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, string> _lastMetar = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="expand">Alias variable expansion ($metar(UUEE), $atiscode(UUEE)…).</param>
    public AtisService(Func<Profile> profile, Func<string, string> expand, Func<string, Metar?> metar, Func<DateTime>? clock = null)
    {
        _profile = profile;
        _expand = expand;
        _metar = metar;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>The letter of an airport changed (the airport).</summary>
    public event EventHandler<string>? LetterChanged;

    public IReadOnlyList<AtisSettings> Stations => _profile().Atis;

    public string Letter(string airport) => _profile().AtisLetters.GetValueOrDefault(airport.ToUpperInvariant(), "");

    public void SetLetter(string airport, string letter)
    {
        airport = airport.ToUpperInvariant();
        letter = letter == "+" ? AtisText.Next(Letter(airport)) : letter.Trim().ToUpperInvariant();
        if (letter.Length != 1 || letter[0] is < 'A' or > 'Z' || Letter(airport) == letter) return;
        _profile().AtisLetters[airport] = letter;
        LetterChanged?.Invoke(this, airport);
    }

    /// <summary>A METAR came in: the next letter for airports whose ATIS changes it by itself (not for the first METAR seen).</summary>
    public void OnMetar(Metar metar)
    {
        bool seen = _lastMetar.TryGetValue(metar.Station, out var last);
        _lastMetar[metar.Station] = metar.Raw;
        if (!seen || last == metar.Raw) return;
        if (Stations.Any(s => s.AutoLetter && s.Airport.Equals(metar.Station, StringComparison.OrdinalIgnoreCase)))
            SetLetter(metar.Station, "+");
    }

    /// <summary>The text lines sent to pilots: the station's lines with variables filled in, then the remark.</summary>
    public IReadOnlyList<string> Text(AtisSettings s)
    {
        var lines = s.Text.Select(l => _expand(AtisText.ForAirport(l, s.Airport)).Trim()).Where(l => l.Length > 0).ToList();
        if (s.Remark.Trim().Length > 0) lines.Add(s.Remark.Trim());
        return lines;
    }

    /// <summary>The spoken ATIS for the station at this moment.</summary>
    public string Speech(AtisSettings s)
    {
        string airport = s.Airport.ToUpperInvariant();
        _profile().ActiveRunways.TryGetValue(airport, out var runways);
        return AtisText.Speech(s.SpokenName.Trim().Length > 0 ? s.SpokenName.Trim() : airport, Letter(airport), _clock(), runways,
            _metar(airport), s.Remark);
    }
}
