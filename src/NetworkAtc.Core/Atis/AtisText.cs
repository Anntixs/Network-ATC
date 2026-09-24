using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Weather;

namespace NetworkAtc.Core.Atis;

/// <summary>ATIS letters, callsigns, and the ATIS as it is spoken (Russian or English) from the METAR.</summary>
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

    private static readonly string[] PhoneticRu =
    [
        "Альфа", "Браво", "Чарли", "Дельта", "Эхо", "Фокстрот", "Гольф", "Отель", "Индия", "Джульетт", "Кило", "Лима", "Майк",
        "Ноябрь", "Оскар", "Папа", "Квебек", "Ромео", "Сьерра", "Танго", "Юниформ", "Виктор", "Виски", "Экс-рей", "Янки", "Зулу",
    ];

    public static string Phonetic(string letter, bool russian) =>
        letter is { Length: 1 } && char.ToUpperInvariant(letter[0]) is >= 'A' and <= 'Z' and var c
            ? (russian ? PhoneticRu : PhoneticEn)[c - 'A'] : letter;

    /// <summary>"24R" → "24 правая" / "24 right".</summary>
    public static string Runway(string id, bool russian)
    {
        id = id.Trim().ToUpperInvariant();
        if (id.Length == 0) return "";
        string side = id[^1] switch
        {
            'L' => russian ? " левая" : " left",
            'R' => russian ? " правая" : " right",
            'C' => russian ? " центральная" : " center",
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
    public static string Speech(string airportName, string letter, DateTime utc, RunwayUse? runways, Metar? metar, string remark, bool russian)
    {
        var sb = new StringBuilder();
        void Say(string s)
        {
            if (s.Length == 0) return;
            sb.Append(s.TrimEnd('.', ' ')).Append(". ");
        }
        string phonetic = Phonetic(letter, russian);
        string time = utc.ToString("HHmm", Inv);
        Say(russian ? $"{airportName}, информация {phonetic}, время {time}" : $"{airportName} information {phonetic}, time {time}");

        if (runways != null)
        {
            string dep = string.Join(russian ? " и " : " and ", runways.Departure.Select(r => Runway(r, russian)));
            string arr = string.Join(russian ? " и " : " and ", runways.Arrival.Select(r => Runway(r, russian)));
            if (dep.Length > 0 && dep == arr) Say(russian ? $"Рабочая полоса {dep}" : $"Runway in use {dep}");
            else
            {
                if (arr.Length > 0) Say(russian ? $"Посадка на полосу {arr}" : $"Landing runway {arr}");
                if (dep.Length > 0) Say(russian ? $"Взлёт с полосы {dep}" : $"Departure runway {dep}");
            }
        }

        if (metar != null)
        {
            if (metar.VariableWind || metar.WindDirection == null && metar.WindSpeed > 0)
                Say(russian ? $"Ветер переменный {Mps(metar.WindSpeed)} метра в секунду" : $"Wind variable {metar.WindSpeed} knots");
            else if (metar.WindSpeed == 0)
                Say(russian ? "Штиль" : "Wind calm");
            else
            {
                string gust = metar.Gust is { } g ? russian ? $", порывы {Mps(g)}" : $", gusting {g}" : "";
                Say(russian ? $"Ветер {metar.WindDirection:000} градусов {Mps(metar.WindSpeed)} метра в секунду{gust}"
                            : $"Wind {metar.WindDirection:000} degrees {metar.WindSpeed} knots{gust}");
            }
            DescribeBody(metar.Raw, russian, Say);
            if (metar.Qnh is { } q) Say(russian ? $"Давление QNH {q} гектопаскалей" : $"QNH {q}");
        }

        Say(remark.Trim());
        Say(russian ? $"Сообщите диспетчеру о получении информации {phonetic}" : $"Advise on initial contact you have information {phonetic}");
        return sb.ToString().TrimEnd();
    }

    private static int Mps(int knots) => (int)Math.Round(knots / 1.94384);

    /// <summary>Visibility, weather, clouds and temperature groups of the METAR, in words.</summary>
    private static void DescribeBody(string raw, bool russian, Action<string> say)
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
                say(m >= 9999 ? russian ? "Видимость более 10 километров" : "Visibility 10 kilometers or more"
                    : m >= 5000 ? russian ? $"Видимость {m / 1000} километров" : $"Visibility {m / 1000} kilometers"
                    : russian ? $"Видимость {m} метров" : $"Visibility {m} meters");
                continue;
            }
            if (t.EndsWith("SM", StringComparison.Ordinal))
            {
                say(russian ? $"Видимость {t[..^2]} мили" : $"Visibility {t[..^2]} miles");
                continue;
            }
            if (t is "NSC" or "NCD" or "SKC" or "CLR")
            {
                say(russian ? "Без существенной облачности" : "No significant cloud");
                continue;
            }
            if (CloudGroup().Match(t) is { Success: true } c)
            {
                int feet = int.Parse(c.Groups["h"].Value, Inv) * 100;
                string cover = c.Groups["cover"].Value switch
                {
                    "FEW" => russian ? "незначительная" : "few",
                    "SCT" => russian ? "рассеянная" : "scattered",
                    "BKN" => russian ? "значительная" : "broken",
                    "OVC" => russian ? "сплошная" : "overcast",
                    _ => russian ? "вертикальная видимость" : "vertical visibility",
                };
                string type = c.Groups["type"].Value switch
                {
                    "CB" => russian ? " кучево-дождевая" : " cumulonimbus",
                    "TCU" => russian ? " мощно-кучевая" : " towering cumulus",
                    _ => "",
                };
                clouds.Add(russian ? $"{cover}{type} {Math.Round(feet * 0.3048 / 10) * 10:0} метров"
                                   : $"{cover}{type} {feet} feet");
                continue;
            }
            if (TemperatureGroup().Match(t) is { Success: true } temp)
            {
                if (clouds.Count > 0)
                {
                    say((russian ? "Облачность " : "Clouds ") + string.Join(", ", clouds));
                    clouds.Clear();
                }
                string T(string v) => v.StartsWith('M') ? "минус " + int.Parse(v[1..], Inv) : int.Parse(v, Inv).ToString(Inv);
                string TEn(string v) => v.StartsWith('M') ? "minus " + int.Parse(v[1..], Inv) : int.Parse(v, Inv).ToString(Inv);
                string dew = temp.Groups["d"].Success ? temp.Groups["d"].Value : "";
                say(russian ? $"Температура {T(temp.Groups["t"].Value)}" + (dew.Length > 0 ? $", точка росы {T(dew)}" : "")
                            : $"Temperature {TEn(temp.Groups["t"].Value)}" + (dew.Length > 0 ? $", dew point {TEn(dew)}" : ""));
                continue;
            }
            if (t.Length >= 2 && t != "AUTO" && !t.Contains('/') && WeatherGroup().Match(t) is { Success: true } w && w.Groups["ph"].Success)
                say(Weather(w, russian));
        }
        if (clouds.Count > 0) say((russian ? "Облачность " : "Clouds ") + string.Join(", ", clouds));
    }

    private static string Weather(Match w, bool russian)
    {
        var parts = new List<string>();
        string intensity = w.Groups["int"].Value;
        if (intensity == "-") parts.Add(russian ? "слабый" : "light");
        else if (intensity == "+") parts.Add(russian ? "сильный" : "heavy");
        else if (intensity == "VC") parts.Add(russian ? "в окрестности" : "in the vicinity");
        string desc = w.Groups["desc"].Value;
        if (desc.Length > 0)
            parts.Add(desc switch
            {
                "SH" => russian ? "ливневый" : "showers of",
                "TS" => russian ? "гроза," : "thunderstorm",
                "FZ" => russian ? "переохлаждённый" : "freezing",
                "BL" => russian ? "метель," : "blowing",
                "DR" => russian ? "позёмок," : "drifting",
                _ => desc,
            });
        string ph = w.Groups["ph"].Value;
        for (int i = 0; i + 1 < ph.Length; i += 2)
            parts.Add(ph.Substring(i, 2) switch
            {
                "RA" => russian ? "дождь" : "rain",
                "SN" => russian ? "снег" : "snow",
                "DZ" => russian ? "морось" : "drizzle",
                "BR" => russian ? "дымка" : "mist",
                "FG" => russian ? "туман" : "fog",
                "HZ" => russian ? "мгла" : "haze",
                "GR" => russian ? "град" : "hail",
                "GS" => russian ? "мелкий град" : "small hail",
                "PL" => russian ? "ледяной дождь" : "ice pellets",
                "SG" => russian ? "снежные зёрна" : "snow grains",
                "FU" => russian ? "дым" : "smoke",
                "SQ" => russian ? "шквал" : "squalls",
                var other => other,
            });
        return string.Join(' ', parts);
    }
}
