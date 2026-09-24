namespace NetworkAtc.Core.Import;

public enum ImportNoteKind { Imported, Skipped, Warning }

/// <summary>One line of an import report, for the "что импортировано / что пропущено" window.</summary>
public sealed record ImportNote(ImportNoteKind Kind, string Text)
{
    public override string ToString() => Kind switch
    {
        ImportNoteKind.Imported => "✓ " + Text,
        ImportNoteKind.Skipped => "— " + Text,
        _ => "! " + Text,
    };
}

public sealed class ImportReport
{
    public List<ImportNote> Notes { get; } = [];

    public IEnumerable<string> Imported => Of(ImportNoteKind.Imported);
    public IEnumerable<string> Skipped => Of(ImportNoteKind.Skipped);
    public IEnumerable<string> Warnings => Of(ImportNoteKind.Warning);

    public void Ok(string text) => Notes.Add(new ImportNote(ImportNoteKind.Imported, text));
    public void Skip(string text) => Notes.Add(new ImportNote(ImportNoteKind.Skipped, text));
    public void Warn(string text) => Notes.Add(new ImportNote(ImportNoteKind.Warning, text));

    private IEnumerable<string> Of(ImportNoteKind kind) => Notes.Where(n => n.Kind == kind).Select(n => n.Text);

    /// <summary>"A, B, C и ещё 4" for long lists in one report line.</summary>
    internal static string Short(IEnumerable<string> items, int max = 10)
    {
        var list = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count <= max ? string.Join(", ", list) : string.Join(", ", list.Take(max)) + $" и ещё {list.Count - max}";
    }
}
