namespace NetworkAtc.Core.Tags;

/// <summary>What clicking a tag field does.</summary>
public static class TagActions
{
    public const string None = "none";
    public const string Select = "select";
    public const string ToggleTrack = "track";
    public const string AircraftMenu = "menu";
    public const string ClearedLevel = "cfl";
    public const string Heading = "hdg";
    public const string Speed = "spd";
    public const string Squawk = "squawk";
    public const string Scratchpad = "scratch";
    public const string FlightPlan = "fp";
    public const string PrivateMessage = "msg";
    /// <summary>The click handler a plugin registered for its own field.</summary>
    public const string Plugin = "plugin";

    public static IReadOnlyList<(string Id, string Title)> All { get; } =
    [
        (None, "Ничего"),
        (Select, "Выбрать борт"),
        (ToggleTrack, "Сопровождать / отпустить"),
        (AircraftMenu, "Меню борта"),
        (ClearedLevel, "Список CFL"),
        (Heading, "Список курсов"),
        (Speed, "Список скоростей"),
        (Squawk, "Код ответчика"),
        (Scratchpad, "Редактировать заметку"),
        (FlightPlan, "Открыть план полёта"),
        (PrivateMessage, "Личное сообщение"),
        (Plugin, "Обработчик плагина"),
    ];
}

public sealed class TagClickBinding
{
    public string Left { get; set; } = TagActions.Select;
    public string Right { get; set; } = TagActions.AircraftMenu;

    public TagClickBinding() { }
    public TagClickBinding(string left, string right) => (Left, Right) = (left, right);
}

/// <summary>Value lists shown by the tag editors.</summary>
public static class TagMenus
{
    /// <summary>
    /// Levels around the cleared (or current) level, highest first: 1000 ft steps at and above the
    /// transition altitude, 500 ft below it.
    /// </summary>
    public static IReadOnlyList<int> Levels(int currentFeet, int? clearedFeet, int transitionAltitude, int count = 21)
    {
        int center = clearedFeet ?? (int)Math.Round(currentFeet / 1000.0) * 1000;
        var levels = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a))) { center };
        int up = center, down = center;
        while (levels.Count < count)
        {
            up += up >= transitionAltitude ? 1000 : 500;
            if (up <= 60000) levels.Add(up);
            down -= down > transitionAltitude ? 1000 : 500;
            if (down >= 0) levels.Add(down);
            if (up > 60000 && down < 0) break;
        }
        return levels.ToList();
    }

    /// <summary>Headings every 5°, starting at the one nearest to the current heading.</summary>
    public static IReadOnlyList<int> Headings(double current)
    {
        int start = ((int)Math.Round(current / 5.0) * 5 + 359) % 360 + 1;
        return Enumerable.Range(0, 72).Select(i => (start - 1 + i * 5) % 360 + 1).ToList();
    }

    public static IReadOnlyList<int> Speeds() => Enumerable.Range(0, 36).Select(i => 490 - i * 10).ToList();

    /// <summary>Index in <paramref name="values"/> nearest to <paramref name="target"/>.</summary>
    public static int NearestIndex(IReadOnlyList<int> values, double target)
    {
        int best = 0;
        for (int i = 1; i < values.Count; i++)
            if (Math.Abs(values[i] - target) < Math.Abs(values[best] - target)) best = i;
        return best;
    }
}
