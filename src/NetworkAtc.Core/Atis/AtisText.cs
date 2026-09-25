using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Weather;

namespace NetworkAtc.Core.Atis;

/// <summary>ATIS letters, callsigns, and the ATIS as it is spoken (English, ICAO phraseology) from the METAR.</summary>
public static partial class AtisText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Callsign(string airport, string suffix) =>
        airport.Trim().ToUpperInvariant() + (suffix.Trim().Length > 0 ? "_" + suffix.Trim().ToUpperInvariant() : "") + "_ATIS";

    /// <summary>The letter after this one (Z is followed by A); A when there is none yet.</summary>
    public static string Next(string? letter) =>
        letter is { Length: 1 } l && char.ToUpperInvariant(l[0]) is >= 'A' and < 'Z' and var c ? ((char)(c + 1)).ToString() : "A";

    private static readonly string[] PhoneticEn =
    [
        "Alfa", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliett", "Kilo", "Lima", "Mike",
        "November", "Oscar", "Papa", "Quebec", "Romeo", "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu",
    ];

    public static string Phonetic(string letter) =>
        letter is { Length: 1 } && char.ToUpperInvariant(letter[0]) is >= 'A' and <= 'Z' and var c
            ? PhoneticEn[c - 'A'] : letter;

    /// <summary>"24R" → "24 right".</summary>
    public static string Runway(string id)
    {
        id = id.Trim().ToUpperInvariant();
        if (id.Length == 0) return "";
        string side = id[^1] switch
        {
            'L' => " left",
            'R' => " right",
            'C' => " center",
            _ => "",
        };
        string number = side.Length > 0 ? id[..^1] : id;
        return number.TrimStart('0').PadLeft(1, '0') + side;
    }

    /// <summary>Replaces $airport with the ATIS airport, so the alias variables of the profile work for each ATIS.</summary>
    public static string ForAirport(string line, string airport) =>
        AirportVariable().Replace(line, airport.ToUpperInvariant());

    [GeneratedRegex(@"\$airport\b", RegexOptions.IgnoreCase)]
    private static partial Regex AirportVariable();

    [GeneratedRegex(@"^(?<t>M?\d{2})/(?<d>M?\d{2})?$")]
    private static partial Regex TemperatureGroup();

    [GeneratedRegex(@"^(?<cover>FEW|SCT|BKN|OVC|VV)(?<h>\d{3})(?<type>CB|TCU)?$")]
    private static partial Regex CloudGroup();

    [GeneratedRegex(@"^(?<int>[-+]|VC)?(?<desc>MI|BC|PR|DR|BL|SH|TS|FZ)?(?<ph>(DZ|RA|SN|SG|IC|PL|GR|GS|UP|BR|FG|FU|VA|DU|SA|HZ|PO|SQ|FC|SS|DS)+)?$")]
    private static partial Regex WeatherGroup();

    /// <summary>
    /// The spoken ATIS: airport, letter, time, runways, wind, visibility, weather, clouds, temperature, QNH,
    /// the remark and "advise on first contact". Numbers are written in digits; the voice reads them.
    /// </summary>
    public static string Speech(string airportName, string letter, DateTime utc, RunwayUse? runways, Metar? metar, string remark)
    {
        var sb = new StringBuilder();
        void Say(string s)
        {
            if (s.Length == 0) return;
            sb.Append(s.TrimEnd('.', ' ')).Append(". ");
        }
        string phonetic = Phonetic(letter);
        string time = utc.ToString("HHmm", Inv);
        Say($"{airportName} information {phonetic}, time {time}");

        if (runways != null)
        {
            string dep = string.Join(" and ", runways.Departure.Select(Runway));
            string arr = string.Join(" and ", runways.Arrival.Select(Runway));
            if (dep.Length > 0 && dep == arr) Say($"Runway in use {dep}");
            else
            {
                if (arr.Length > 0) Say($"Landing runway {arr}");
                if (dep.Length > 0) Say($"Departure runway {dep}");
            }
        }

        if (metar != null)
        {
            if (metar.VariableWind || metar.WindDirection == null && metar.WindSpeed > 0)
                Say($"Wind variable {metar.WindSpeed} knots");
            else if (metar.WindSpeed == 0)
                Say("Wind calm");
            else
            {
                string gust = metar.Gust is { } g ? $", gusting {g}" : "";
                Say($"Wind {metar.WindDirection:000} degrees {metar.WindSpeed} knots{gust}");
            }
            DescribeBody(metar.Raw, Say);
            if (metar.Qnh is { } q) Say($"QNH {q}");
        }

        Say(remark.Trim());
        Say($"Advise on initial contact you have information {phonetic}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Visibility, weather, clouds and temperature groups of the METAR, in words.</summary>
    private static void DescribeBody(string raw, Action<string> say)
    {
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var clouds = new List<string>();
        foreach (var t in tokens.Skip(1))
        {
            if (t is "RMK" or "TEMPO" or "BECMG" or "NOSIG") break;
            if (t == "CAVOK")
            {
                say("CAVOK");
                continue;
            }
            if (t.Length == 4 && t.All(char.IsDigit))
            {
                int m = int.Parse(t, Inv);
                say(m >= 9999 ? "Visibility 10 kilometers or more"
                    : m >= 5000 ? $"Visibility {m / 1000} kilometers"
                    : $"Visibility {m} meters");
                continue;
            }
            if (t.EndsWith("SM", StringComparison.Ordinal))
            {
                say($"Visibility {t[..^2]} miles");
                continue;
            }
            if (t is "NSC" or "NCD" or "SKC" or "CLR")
            {
                say("No significant cloud");
                continue;
            }
            if (CloudGroup().Match(t) is { Success: true } c)
            {
                int feet = int.Parse(c.Groups["h"].Value, Inv) * 100;
                string cover = c.Groups["cover"].Value switch
                {
                    "FEW" => "few",
                    "SCT" => "scattered",
                    "BKN" => "broken",
                    "OVC" => "overcast",
                    _ => "vertical visibility",
                };
                string type = c.Groups["type"].Value switch
                {
                    "CB" => " cumulonimbus",
                    "TCU" => " towering cumulus",
                    _ => "",
                };
                clouds.Add($"{cover} {feet} feet{type}");
                continue;
            }
            if (TemperatureGroup().Match(t) is { Success: true } temp)
            {
                if (clouds.Count > 0)
                {
                    say("Clouds " + string.Join(", ", clouds));
                    clouds.Clear();
                }
                string T(string v) => v.StartsWith('M') ? "minus " + int.Parse(v[1..], Inv) : int.Parse(v, Inv).ToString(Inv);
                string dew = temp.Groups["d"].Success ? temp.Groups["d"].Value : "";
                say($"Temperature {T(temp.Groups["t"].Value)}" + (dew.Length > 0 ? $", dew point {T(dew)}" : ""));
                continue;
            }
            if (t.Length >= 2 && t != "AUTO" && !t.Contains('/') && WeatherGroup().Match(t) is { Success: true } w && w.Groups["ph"].Success)
                say(Weather(w));
        }
        if (clouds.Count > 0) say("Clouds " + string.Join(", ", clouds));
    }

    private static string Weather(Match w)
    {
        var parts = new List<string>();
        string intensity = w.Groups["int"].Value;
        if (intensity == "-") parts.Add("light");
        else if (intensity == "+") parts.Add("heavy");
        else if (intensity == "VC") parts.Add("in the vicinity");
        string desc = w.Groups["desc"].Value;
        if (desc.Length > 0)
            parts.Add(desc switch
            {
                "SH" => "showers of",
                "TS" => "thunderstorm",
                "FZ" => "freezing",
                "BL" => "blowing",
                "DR" => "drifting",
                _ => desc,
            });
        string ph = w.Groups["ph"].Value;
        for (int i = 0; i + 1 < ph.Length; i += 2)
            parts.Add(ph.Substring(i, 2) switch
            {
                "RA" => "rain",
                "SN" => "snow",
                "DZ" => "drizzle",
                "BR" => "mist",
                "FG" => "fog",
                "HZ" => "haze",
                "GR" => "hail",
                "GS" => "small hail",
                "PL" => "ice pellets",
                "SG" => "snow grains",
                "FU" => "smoke",
                "SQ" => "squalls",
                var other => other,
            });
        return string.Join(' ', parts);
    }
}
