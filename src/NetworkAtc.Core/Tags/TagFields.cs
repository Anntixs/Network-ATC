using System.Globalization;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Tags;

public sealed record TagField(string Key, string Description, Func<IAircraft, string> Value);

/// <summary>All fields usable in tag templates: built-in ones plus those registered by plugins.</summary>
public sealed class TagFields
{
    private readonly Dictionary<string, TagField> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Altitudes at or above this are shown as flight levels.</summary>
    public int TransitionAltitude { get; set; } = 10000;

    public TagFields()
    {
        Add("callsign", "Позывной", a => a.Callsign);
        Add("type", "Тип ВС", a => a.AircraftType);
        Add("wtc", "Категория турбулентности", a => a is Radar.Track t ? t.WakeCategory.ToString() : "");
        Add("alt", "Высота в сотнях футов (350)", a => Hundreds(a.Altitude));
        Add("fl", "Эшелон или высота (F350 / A045)", a => FlightLevel(a.Altitude));
        Add("vs", "Стрелка набора/снижения", a => a.VerticalSpeed > 300 ? "↑" : a.VerticalSpeed < -300 ? "↓" : "");
        Add("vsfpm", "Вертикальная скорость, фут/мин", a => a.VerticalSpeed == 0 ? "" : a.VerticalSpeed.ToString("+0;-0", CultureInfo.InvariantCulture));
        Add("gs", "Путевая скорость, узлы", a => a.GroundSpeed.ToString(CultureInfo.InvariantCulture));
        Add("gs10", "Путевая скорость / 10", a => (a.GroundSpeed / 10).ToString("00", CultureInfo.InvariantCulture));
        Add("hdg", "Курс", a => ((int)Math.Round(a.Heading) % 360).ToString("000", CultureInfo.InvariantCulture));
        Add("squawk", "Код ответчика", a => a.Squawk.ToString("0000", CultureInfo.InvariantCulture));
        Add("dep", "Аэродром вылета", a => a.Departure);
        Add("dest", "Аэродром назначения", a => a.Destination);
        Add("rfl", "Заявленный эшелон", a => a.FiledAltitude);
        Add("cfl", "Разрешённая высота", a => a.ClearedAltitude is { } c ? FlightLevel(c) : "");
        Add("ahdg", "Назначенный курс", a => a.AssignedHeading is { } h ? "H" + h.ToString("000", CultureInfo.InvariantCulture) : "");
        Add("aspd", "Назначенная скорость", a => a.AssignedSpeed is { } s ? "S" + s : "");
        Add("asq", "Назначенный код", a => a.AssignedSquawk is { } q && q != a.Squawk ? q.ToString("0000", CultureInfo.InvariantCulture) : "");
        Add("scratch", "Заметка диспетчера", a => a.Scratchpad);
        Add("ident", "IDENT", a => a.Ident ? "ID" : "");
        Add("modec", "Режим ответчика (пусто, если Mode C)", a => a.ModeC ? "" : "STBY");
    }

    public IEnumerable<TagField> All => _fields.Values.OrderBy(f => f.Key);

    public void Add(string key, string description, Func<IAircraft, string> value) =>
        _fields[key] = new TagField(key, description, value);

    public string? Resolve(string key, IAircraft aircraft)
    {
        if (!_fields.TryGetValue(key, out var f)) return null;
        try { return f.Value(aircraft) ?? ""; }
        catch (Exception) { return "?"; } // a broken plugin field must not break the radar
    }

    public string FlightLevel(int feet) => feet >= TransitionAltitude ? "F" + Hundreds(feet) : "A" + Hundreds(feet);

    private static string Hundreds(int feet) => Math.Max(0, (int)Math.Round(feet / 100.0)).ToString("000", CultureInfo.InvariantCulture);
}
