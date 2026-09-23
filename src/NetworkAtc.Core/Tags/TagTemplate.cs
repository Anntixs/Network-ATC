using System.Text;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Tags;

/// <summary>A piece of a rendered tag line; <see cref="Field"/> is the template field that produced it, or null.</summary>
public sealed record TagSpan(string Text, string? Field);

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

    /// <summary>Tag lines as plain text.</summary>
    public IReadOnlyList<string> Render(IAircraft aircraft, TagFields fields) =>
        RenderSpans(aircraft, fields).Select(line => string.Concat(line.Select(s => s.Text))).ToList();

    /// <summary>
    /// Tag lines as spans, each remembering which field produced it (null for literal text),
    /// so the radar can tell which field was clicked.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TagSpan>> RenderSpans(IAircraft aircraft, TagFields fields)
    {
        var result = new List<IReadOnlyList<TagSpan>>();
        foreach (var line in _lines)
        {
            var spans = new List<TagSpan>();
            bool anyField = false, anyValue = false;
            foreach (var part in line)
            {
                switch (part)
                {
                    case Literal l:
                        Append(spans, l.Text, null);
                        break;
                    case Field f:
                        anyField = true;
                        string value = fields.Resolve(f.Key, aircraft) ?? $"{{{f.Key}}}";
                        if (value.Length == 0) value = f.Fallback;
                        if (value.Length > 0) anyValue = true;
                        Append(spans, value, f.Key);
                        break;
                }
            }
            Trim(spans);
            if (spans.Count > 0 && (!anyField || anyValue)) result.Add(spans);
        }
        return result;
    }

    /// <summary>Appends text, collapsing runs of spaces across span boundaries.</summary>
    private static void Append(List<TagSpan> spans, string text, string? key)
    {
        var sb = new StringBuilder(text.Length);
        char prev = spans.Count > 0 && spans[^1].Text.Length > 0 ? spans[^1].Text[^1] : '\0';
        foreach (char c in text)
        {
            if (c == ' ' && (prev == ' ' || prev == '\0' && spans.Count == 0 && sb.Length == 0)) continue;
            sb.Append(c);
            prev = c;
        }
        if (sb.Length > 0) spans.Add(new TagSpan(sb.ToString(), key));
    }

    private static void Trim(List<TagSpan> spans)
    {
        while (spans.Count > 0 && spans[0].Text.TrimStart().Length == 0) spans.RemoveAt(0);
        while (spans.Count > 0 && spans[^1].Text.TrimEnd().Length == 0) spans.RemoveAt(spans.Count - 1);
        if (spans.Count == 0) return;
        spans[0] = spans[0] with { Text = spans[0].Text.TrimStart() };
        spans[^1] = spans[^1] with { Text = spans[^1].Text.TrimEnd() };
    }
}
