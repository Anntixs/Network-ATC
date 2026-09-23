using System.Text;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Tags;

/// <summary>
/// A tag layout such as "{callsign} {wtc}\n{fl}{vs} {cfl}\n{gs10} {dest}".
/// Each line is a sequence of text and {field} placeholders; {field|text} shows text when the field is empty.
/// Runs of spaces left by empty fields are collapsed and empty lines are dropped.
/// </summary>
public sealed class TagTemplate
{
    private abstract record Part;
    private sealed record Literal(string Text) : Part;
    private sealed record Field(string Key, string Fallback) : Part;

    private readonly List<List<Part>> _lines;

    public string Source { get; }

    private TagTemplate(string source, List<List<Part>> lines)
    {
        Source = source;
        _lines = lines;
    }

    public IEnumerable<string> FieldKeys => _lines.SelectMany(l => l.OfType<Field>()).Select(f => f.Key).Distinct();

    public static TagTemplate Parse(string source)
    {
        var lines = new List<List<Part>>();
        foreach (var rawLine in source.Replace("\r", "").Replace("\\n", "\n").Split('\n'))
        {
            var parts = new List<Part>();
            int i = 0;
            while (i < rawLine.Length)
            {
                int open = rawLine.IndexOf('{', i);
                if (open < 0)
                {
                    parts.Add(new Literal(rawLine[i..]));
                    break;
                }
                if (open > i) parts.Add(new Literal(rawLine[i..open]));
                int close = rawLine.IndexOf('}', open);
                if (close < 0)
                {
                    parts.Add(new Literal(rawLine[open..]));
                    break;
                }
                string inner = rawLine[(open + 1)..close];
                int bar = inner.IndexOf('|');
                parts.Add(bar >= 0 ? new Field(inner[..bar].Trim(), inner[(bar + 1)..]) : new Field(inner.Trim(), ""));
                i = close + 1;
            }
            lines.Add(parts);
        }
        return new TagTemplate(source, lines);
    }

    public IReadOnlyList<string> Render(IAircraft aircraft, TagFields fields)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in _lines)
        {
            sb.Clear();
            bool anyField = false, anyValue = false;
            foreach (var part in line)
            {
                switch (part)
                {
                    case Literal l:
                        sb.Append(l.Text);
                        break;
                    case Field f:
                        anyField = true;
                        string value = fields.Resolve(f.Key, aircraft) ?? $"{{{f.Key}}}";
                        if (value.Length == 0) value = f.Fallback;
                        if (value.Length > 0) anyValue = true;
                        sb.Append(value);
                        break;
                }
            }
            string text = CollapseSpaces(sb.ToString()).Trim();
            if (text.Length > 0 && (!anyField || anyValue)) result.Add(text);
        }
        return result;
    }

    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (c != ' ' || sb.Length == 0 || sb[^1] != ' ') sb.Append(c);
        return sb.ToString();
    }
}
