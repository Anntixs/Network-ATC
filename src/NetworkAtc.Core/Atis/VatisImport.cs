using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetworkAtc.Core.Atis;

/// <summary>
/// Reads vATIS profiles (the JSON file vATIS exports: "Stations", or "Composites" in older versions) into ATIS
/// stations: airport, frequency, arrival/departure suffix, spoken name, and every preset with its template, airport
/// conditions and NOTAMs. Template variables become alias variables ([ATIS_CODE] → $atiscode($airport)...),
/// contractions (@ILS) are written out.
/// </summary>
public static partial class VatisImport
{
    public static List<AtisSettings> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var stations = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array) stations.AddRange(root.EnumerateArray());
        else if (Get(root, "Stations", "Composites") is { ValueKind: JsonValueKind.Array } list) stations.AddRange(list.EnumerateArray());
        else if (Get(root, "Identifier") != null) stations.Add(root);
        else throw new FormatException("This is not a vATIS profile: no stations found");

        var result = new List<AtisSettings>();
        foreach (var st in stations)
        {
            if (st.ValueKind != JsonValueKind.Object) continue;
            string airport = Text(st, "Identifier", "Airport").Trim().ToUpperInvariant();
            if (airport.Length == 0) continue;
            var contractions = Contractions(st);
            var s = new AtisSettings
            {
                Airport = airport,
                Suffix = Suffix(Get(st, "AtisType", "Type")),
                Frequency = FrequencyText(Get(st, "Frequency")),
                SpokenName = Text(st, "Name").Trim(),
                Voice = UsesSpeech(st) ? AtisVoiceMode.Speech : AtisVoiceMode.None,
                Language = "en",
            };
            if (Get(st, "Presets") is { ValueKind: JsonValueKind.Array } presets)
                foreach (var p in presets.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    string conditions = Expand(Join(Text(p, "AirportConditions"), Definitions(st, "AirportConditionDefinitions")), contractions);
                    string notams = Expand(Join(Text(p, "Notams"), Definitions(st, "NotamDefinitions")), contractions);
                    var lines = Template(Expand(Text(p, "Template"), contractions));
                    s.Presets.Add(new AtisPreset
                    {
                        Name = Text(p, "Name").Trim() is { Length: > 0 } n ? n : $"Preset {s.Presets.Count + 1}",
                        Text = lines.Count > 0 ? lines : [.. AtisSettings.DefaultText],
                        Remark = Join(conditions, notams),
                    });
                }
            if (s.Presets.Count > 0) s.ApplyPreset(s.Presets[0]);
            result.Add(s);
        }
        if (result.Count == 0) throw new FormatException("The vATIS profile has no stations with an airport");
        return result;
    }

    /// <summary>A vATIS template as text lines with alias variables.</summary>
    public static List<string> Template(string template)
    {
        string text = TextOnly().Replace(template, m => m.Groups["t"].Value);
        text = VoiceOnly().Replace(text, "");
        text = Variable().Replace(text, m => m.Groups["name"].Value.ToUpperInvariant() switch
        {
            "FACILITY" or "ARPT_ID" or "AIRPORT" or "STATION" => "$airport",
            "ATIS_CODE" or "CODE" or "ATIS_LETTER" => "$atiscode($airport)",
            "OBS_TIME" or "ATIS_TIME" or "TIME" => "$time",
            "FULL_WX_STRING" or "METAR" or "WX" or "FULL_WX" => "$metar($airport)",
            "WIND" or "SURFACE_WIND" => "$wind($airport)",
            "PRESSURE" or "QNH" => "$qnh($airport)",
            "ALTIMETER" or "ALTIM" => "$altim($airport)",
            "ARR_RWY" or "ARRIVAL_RUNWAYS" or "ARR_RWYS" => "$arrrwy($airport)",
            "DEP_RWY" or "DEPARTURE_RUNWAYS" or "DEP_RWYS" => "$deprwy($airport)",
            _ => "", // [ARPT_COND], [NOTAMS] go to the remark; weather details are in $metar
        });
        return text.Replace("\r", "").Split('\n')
            .Select(l => Spaces().Replace(l, " ").Replace(" .", ".").Replace(" ,", ",").Trim())
            .Where(l => l.Length > 0 && l.Any(char.IsLetterOrDigit))
            .ToList();
    }

    private static JsonElement? Get(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in obj.EnumerateObject())
            foreach (var n in names)
                if (prop.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return prop.Value;
        return null;
    }

    private static string Text(JsonElement obj, params string[] names) => Get(obj, names) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString() ?? "",
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => "",
    };

    private static string Join(params string[] parts) =>
        string.Join(" ", parts.Select(p => p.Trim()).Where(p => p.Length > 0));

    private static string Join(string first, IEnumerable<string> rest) => Join([first, .. rest]);

    /// <summary>Airport conditions / NOTAMs kept in the station's list and switched on there.</summary>
    private static IEnumerable<string> Definitions(JsonElement st, string name)
    {
        if (Get(st, name) is not { ValueKind: JsonValueKind.Array } list) yield break;
        foreach (var d in list.EnumerateArray())
            if (Get(d, "Enabled") is { ValueKind: JsonValueKind.True } && Text(d, "Text", "Value").Trim() is { Length: > 0 } t)
                yield return t;
    }

    private static List<(string From, string To)> Contractions(JsonElement st)
    {
        var list = new List<(string, string)>();
        if (Get(st, "Contractions") is { ValueKind: JsonValueKind.Array } items)
            foreach (var c in items.EnumerateArray())
            {
                string from = Text(c, "VariableName", "String", "Name").Trim().TrimStart('@');
                string to = Text(c, "Text", "Spoken", "Voice").Trim();
                if (from.Length > 0) list.Add(("@" + from, to));
            }
        return [.. list.OrderByDescending(c => c.Item1.Length)];
    }

    private static string Expand(string text, List<(string From, string To)> contractions)
    {
        foreach (var (from, to) in contractions)
            text = Regex.Replace(text, Regex.Escape(from) + @"\b", to.Replace("$", "$$"), RegexOptions.IgnoreCase);
        return text;
    }

    private static string Suffix(JsonElement? type) => type switch
    {
        { ValueKind: JsonValueKind.String } v => (v.GetString() ?? "").ToLowerInvariant() switch
        {
            "departure" or "dep" => "D",
            "arrival" or "arr" => "A",
            _ => "",
        },
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt32(out var n) => n switch { 1 => "D", 2 => "A", _ => "" },
        _ => "",
    };

    /// <summary>vATIS keeps frequencies in Hz (133800000); older files in kHz (133800) or MHz.</summary>
    public static string FrequencyText(JsonElement? value)
    {
        double f = 0;
        if (value is { ValueKind: JsonValueKind.Number } n) f = n.GetDouble();
        else if (value is { ValueKind: JsonValueKind.String } s)
            double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f);
        if (f >= 1e7) f /= 1e6;
        else if (f >= 1e4) f /= 1e3;
        return f is >= 100 and < 200 ? f.ToString("0.000", CultureInfo.InvariantCulture) : "";
    }

    private static bool UsesSpeech(JsonElement st) =>
        Get(st, "AtisVoice") is { } v ? Get(v, "UseTextToSpeech") is not { ValueKind: JsonValueKind.False } : true;

    [GeneratedRegex(@"\[(?<name>[A-Za-z_]+)\]")]
    private static partial Regex Variable();

    [GeneratedRegex(@"\[TEXT:(?<t>[^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex TextOnly();

    [GeneratedRegex(@"\[VOICE:[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex VoiceOnly();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();
}
