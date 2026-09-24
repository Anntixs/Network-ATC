using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Sectors;

/// <summary>
/// Reader for EuroScope sector files. The .sct (SCT2) format is a line based text format with
/// [SECTION] headers; coordinates are written as N055.58.21.000 E037.24.53.000 or replaced by
/// the name of a VOR, NDB, fix or airport. The .ese file adds controller positions, free text and
/// airspace sectors.
/// </summary>
public static partial class SectorParser
{
    private static readonly string[] LineSections =
        ["ARTCC", "ARTCC HIGH", "ARTCC LOW", "SID", "STAR", "LOW AIRWAY", "HIGH AIRWAY", "GEO"];

    [GeneratedRegex(@"^([NSEW])(\d{1,3})\.(\d{1,2})\.(\d{1,2}(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex DmsRegex();

    public static SectorFile LoadFiles(string sctPath, string? esePath = null)
    {
        var sector = ParseSct(ReadText(sctPath));
        if (esePath == null)
        {
            var guess = Path.ChangeExtension(sctPath, ".ese");
            if (File.Exists(guess)) esePath = guess;
        }
        if (esePath != null && File.Exists(esePath)) ParseEse(ReadText(esePath), sector);
        if (sector.Name.Length == 0) sector.Name = Path.GetFileNameWithoutExtension(sctPath);
        return sector;
    }

    /// <summary>Sector files are usually Windows-1252; fall back to Latin-1 when they are not valid UTF-8.</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // ---- coordinates and colors ------------------------------------------------------------

    /// <summary>Parses "N055.58.21.000" / "E037.24.53.000" or plain decimal degrees.</summary>
    public static bool TryParseCoordinate(string token, out double degrees)
    {
        degrees = 0;
        var m = DmsRegex().Match(token.Trim());
        if (m.Success)
        {
            degrees = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                      + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 60.0
                      + double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) / 3600.0;
            if (m.Groups[1].Value.ToUpperInvariant() is "S" or "W") degrees = -degrees;
            return true;
        }
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees) && Math.Abs(degrees) <= 180;
    }

    public static bool IsCoordinate(string token) => DmsRegex().IsMatch(token.Trim());

    /// <summary>EuroScope colors are Windows COLORREF numbers: R + 256*G + 65536*B.</summary>
    public static string ColorFromColorRef(long value)
    {
        int r = (int)(value & 0xFF), g = (int)((value >> 8) & 0xFF), b = (int)((value >> 16) & 0xFF);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    // ---- .sct --------------------------------------------------------------------------------

    public static SectorFile ParseSct(string text)
    {
        var sector = new SectorFile();
        var sections = SplitSections(text, sector);

        // Pass 1: named points, so later sections can refer to them.
        if (sections.TryGetValue("INFO", out var info)) ParseInfo(info, sector);
        ParsePoints(sections.GetValueOrDefault("VOR"), sector.Vors, hasFrequency: true, sector);
        ParsePoints(sections.GetValueOrDefault("NDB"), sector.Ndbs, hasFrequency: true, sector);
        ParsePoints(sections.GetValueOrDefault("AIRPORT"), sector.Airports, hasFrequency: true, sector);
        ParsePoints(sections.GetValueOrDefault("FIXES"), sector.Fixes, hasFrequency: false, sector);

        var points = new Dictionary<string, GeoPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in sector.Fixes.Concat(sector.Ndbs).Concat(sector.Vors).Concat(sector.Airports))
            points[p.Name] = p.Position;

        // Pass 2: everything that uses points.
        if (sections.TryGetValue("RUNWAY", out var rwy)) ParseRunways(rwy, sector);
        foreach (var name in LineSections)
        {
            if (!sections.TryGetValue(name, out var lines)) continue;
            sector.Lines[name] = ParseLines(lines, sector, points, name);
        }
        if (sections.TryGetValue("REGIONS", out var regions)) ParseRegions(regions, sector, points);
        if (sections.TryGetValue("LABELS", out var labels)) ParseLabels(labels, sector);
        if (sector.Center == default && sector.Airports.Count > 0) sector.Center = sector.Airports[0].Position;
        return sector;
    }

    private sealed record RawLine(int Number, string Text);

    private static Dictionary<string, List<RawLine>> SplitSections(string text, SectorFile sector)
    {
        var sections = new Dictionary<string, List<RawLine>>(StringComparer.OrdinalIgnoreCase);
        List<RawLine>? current = null;
        int n = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            n++;
            string line = StripComment(rawLine.TrimEnd('\r'));
            if (line.Trim().Length == 0) continue;
            string trimmed = line.Trim();
            if (trimmed.StartsWith("#define", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var colorRef))
                    sector.Colors[parts[1]] = ColorFromColorRef(colorRef);
                continue;
            }
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                string name = trimmed[1..^1].Trim();
                if (!sections.TryGetValue(name, out current)) sections[name] = current = [];
                continue;
            }
            current?.Add(new RawLine(n, line));
        }
        return sections;
    }

    private static string StripComment(string line)
    {
        int i = line.IndexOf(';');
        return i >= 0 ? line[..i] : line;
    }

    private static void ParseInfo(List<RawLine> lines, SectorFile sector)
    {
        var values = lines.Select(l => l.Text.Trim()).ToList();
        if (values.Count > 0) sector.Name = values[0];
        if (values.Count > 1) sector.DefaultCallsign = values[1];
        if (values.Count > 2) sector.DefaultAirport = values[2];
        if (values.Count > 4 && TryParseCoordinate(values[3], out var lat) && TryParseCoordinate(values[4], out var lon))
            sector.Center = new GeoPoint(lat, lon);
        if (values.Count > 7 && double.TryParse(values[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var magVar))
            sector.MagneticVariation = magVar;
    }

    private static string[] Tokens(string line) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static void ParsePoints(List<RawLine>? lines, List<NamedPoint> target, bool hasFrequency, SectorFile sector)
    {
        if (lines == null) return;
        foreach (var line in lines)
        {
            var t = Tokens(line.Text);
            int latIndex = hasFrequency ? 2 : 1;
            if (t.Length > latIndex + 1 && TryParseCoordinate(t[latIndex], out var lat) && TryParseCoordinate(t[latIndex + 1], out var lon))
                target.Add(new NamedPoint(t[0], new GeoPoint(lat, lon), hasFrequency ? t[1] : ""));
            else
                sector.Warnings.Add($"строка {line.Number}: не удалось прочитать точку");
        }
    }

    private static void ParseRunways(List<RawLine> lines, SectorFile sector)
    {
        foreach (var line in lines)
        {
            var t = Tokens(line.Text);
            if (t.Length >= 8 &&
                TryParseCoordinate(t[4], out var lat1) && TryParseCoordinate(t[5], out var lon1) &&
                TryParseCoordinate(t[6], out var lat2) && TryParseCoordinate(t[7], out var lon2))
            {
                int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h1);
                int.TryParse(t[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h2);
                sector.Runways.Add(new Runway(t.Length > 8 ? t[8] : "", t[0], t[1], h1, h2, new GeoPoint(lat1, lon1), new GeoPoint(lat2, lon2)));
            }
            else
            {
                sector.Warnings.Add($"строка {line.Number}: не удалось прочитать ВПП");
            }
        }
    }

    private static bool TryResolve(string latToken, string lonToken, Dictionary<string, GeoPoint> points, out GeoPoint point)
    {
        point = default;
        if (TryParseCoordinate(latToken, out var lat) && TryParseCoordinate(lonToken, out var lon))
        {
            point = new GeoPoint(lat, lon);
            return true;
        }
        // Named point, written twice: "KUNOV KUNOV".
        return points.TryGetValue(latToken, out point);
    }

    private static string? ResolveColor(string token, SectorFile sector)
    {
        if (sector.Colors.TryGetValue(token, out var c)) return c;
        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var colorRef)) return ColorFromColorRef(colorRef);
        return null;
    }

    private static List<SectorLine> ParseLines(List<RawLine> lines, SectorFile sector, Dictionary<string, GeoPoint> points, string section)
    {
        var result = new List<SectorLine>();
        string lastName = "";
        foreach (var line in lines)
        {
            var t = Tokens(line.Text).ToList();
            string? color = null;
            // Optional trailing color: a #define name or a number that is not part of a coordinate pair.
            if (t.Count >= 5)
            {
                var c = ResolveColor(t[^1], sector);
                bool lastIsPoint = IsCoordinate(t[^1]) || points.ContainsKey(t[^1]);
                if (c != null && !lastIsPoint)
                {
                    color = c;
                    t.RemoveAt(t.Count - 1);
                }
            }
            if (t.Count < 4)
            {
                sector.Warnings.Add($"[{section}] строка {line.Number}: мало значений");
                continue;
            }
            var coords = t.GetRange(t.Count - 4, 4);
            string name = string.Join(' ', t.Take(t.Count - 4));
            // Continuation lines of SID/STAR/ARTCC entries start with whitespace and have no name.
            if (name.Length == 0 && char.IsWhiteSpace(line.Text[0])) name = lastName;
            else if (name.Length > 0) lastName = name;
            if (TryResolve(coords[0], coords[1], points, out var a) && TryResolve(coords[2], coords[3], points, out var b))
                result.Add(new SectorLine(name, a, b, color));
            else
                sector.Warnings.Add($"[{section}] строка {line.Number}: неизвестная точка");
        }
        return result;
    }

    private static void ParseRegions(List<RawLine> lines, SectorFile sector, Dictionary<string, GeoPoint> points)
    {
        string name = "";
        string? color = null;
        var current = new List<GeoPoint>();

        void Flush()
        {
            if (current.Count >= 3) sector.Regions.Add(new Region(name, color, current));
            current = [];
        }

        foreach (var line in lines)
        {
            var t = Tokens(line.Text);
            if (t.Length >= 1 && t[0].Equals("REGIONNAME", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                name = string.Join(' ', t.Skip(1));
                color = null;
                continue;
            }
            if (t.Length >= 3 && !IsCoordinate(t[0]))
            {
                // "<color> <lat> <lon>" starts a new polygon.
                Flush();
                color = ResolveColor(t[0], sector);
                if (TryResolve(t[1], t[2], points, out var p)) current.Add(p);
                continue;
            }
            if (t.Length >= 2 && TryResolve(t[0], t[1], points, out var q)) current.Add(q);
            else sector.Warnings.Add($"[REGIONS] строка {line.Number}: не удалось прочитать точку");
        }
        Flush();
    }

    private static void ParseLabels(List<RawLine> lines, SectorFile sector)
    {
        foreach (var line in lines)
        {
            string s = line.Text.Trim();
            if (!s.StartsWith('"')) continue;
            int end = s.IndexOf('"', 1);
            if (end < 0) continue;
            string text = s[1..end];
            var t = Tokens(s[(end + 1)..]);
            if (t.Length >= 2 && TryParseCoordinate(t[0], out var lat) && TryParseCoordinate(t[1], out var lon))
                sector.Labels.Add(new SectorLabel(text, new GeoPoint(lat, lon), t.Length > 2 ? ResolveColor(t[2], sector) : null));
        }
    }

    // ---- .ese --------------------------------------------------------------------------------

    public static void ParseEse(string text, SectorFile sector)
    {
        string section = "";
        string? currentLine = null;
        AirspaceBuilder? airspace = null;

        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith(';') || line.Trim().Length == 0) continue;
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].ToUpperInvariant();
                continue;
            }
            var f = trimmed.Split(':');
            switch (section)
            {
                case "POSITIONS" when f.Length >= 7:
                    sector.Positions.Add(new AtcPosition(f[0], f[1], f[2], f[3], f[5], f[6],
                        f.Length > 10 ? ParseSquawk(f[9]) : null, f.Length > 10 ? ParseSquawk(f[10]) : null));
                    break;
                case "SIDSSTARS" when f.Length >= 5 && f[0].ToUpperInvariant() is "SID" or "STAR":
                    sector.Procedures.Add(new Procedure(f[0].Equals("SID", StringComparison.OrdinalIgnoreCase) ? ProcedureKind.Sid : ProcedureKind.Star,
                        f[1].Trim().ToUpperInvariant(), f[2].Trim().ToUpperInvariant(), f[3].Trim().ToUpperInvariant(),
                        f[4].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToUpperInvariant()).ToList()));
                    break;
                case "FREETEXT" when f.Length >= 4:
                    if (TryParseCoordinate(f[0], out var lat) && TryParseCoordinate(f[1], out var lon))
                        sector.FreeTexts.Add(new SectorLabel(string.Join(':', f.Skip(3)), new GeoPoint(lat, lon), null, f[2]));
                    break;
                case "AIRSPACE":
                    switch (f[0].ToUpperInvariant())
                    {
                        case "SECTORLINE" when f.Length >= 2:
                            currentLine = f[1];
                            sector.SectorLines[currentLine] = [];
                            break;
                        case "COORD" when f.Length >= 3 && currentLine != null:
                            if (TryParseCoordinate(f[1], out var clat) && TryParseCoordinate(f[2], out var clon))
                                sector.SectorLines[currentLine].Add(new GeoPoint(clat, clon));
                            break;
                        case "SECTOR" when f.Length >= 4:
                            airspace?.Build(sector);
                            airspace = new AirspaceBuilder(f[1],
                                int.TryParse(f[2], out var floor) ? floor : 0,
                                int.TryParse(f[3], out var ceiling) ? ceiling : 99999);
                            currentLine = null;
                            break;
                        case "OWNER" when airspace != null:
                            airspace.Owners.AddRange(f.Skip(1));
                            break;
                        case "BORDER" when airspace != null:
                            airspace.Borders.AddRange(f.Skip(1));
                            break;
                    }
                    break;
            }
        }
        airspace?.Build(sector);
    }

    /// <summary>"4201" → 4201; "0000", "-" or anything that is not an octal code → null.</summary>
    private static int? ParseSquawk(string text)
    {
        text = text.Trim();
        return text.Length == 4 && text.All(c => c is >= '0' and <= '7') && text != "0000"
            ? int.Parse(text, CultureInfo.InvariantCulture) : null;
    }

    private sealed class AirspaceBuilder(string name, int floor, int ceiling)
    {
        public List<string> Owners { get; } = [];
        public List<string> Borders { get; } = [];
        public void Build(SectorFile sector) => sector.Sectors.Add(new AirspaceSector(name, floor, ceiling, Owners, Borders));
    }
}
