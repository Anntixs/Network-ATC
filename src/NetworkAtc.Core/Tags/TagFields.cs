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

    /// <summary>Source of the coordination fields (owner, next, SID/STAR, warnings); set by the application.</summary>
    public Session.Workspace? Workspace { get; set; }

    private string WithTrack(IAircraft a, Func<Radar.Track, Session.Workspace, string?> f) =>
        a is Radar.Track t && Workspace is { } w ? f(t, w) ?? "" : "";

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
        Add("owner", "Кто ведёт борт (идентификатор позиции)", a => WithTrack(a, (t, w) => t.Owner.Length == 0 ? "" : w.ShortName(t.Owner)));
        Add("ho", "Передача: кому / от кого (→EA, ←DC)", a => WithTrack(a, (t, w) =>
            !t.HandoffPending ? ""
            : t.HandoffFrom.Equals(w.Session.Me, StringComparison.OrdinalIgnoreCase) ? "→" + w.ShortName(t.HandoffTo)
            : "←" + w.ShortName(t.HandoffFrom)));
        Add("next", "Следующий диспетчер по сектору", a => WithTrack(a, (t, w) => w.NextController(t) is { } n ? "»" + w.ShortName(n) : ""));
        Add("proc", "SID для вылета / STAR для прилёта", a => WithTrack(a, (t, w) => w.ProcedureOf(t)));
        Add("sid", "SID", a => WithTrack(a, (t, w) => w.Procedures.Sid(t)));
        Add("star", "STAR", a => WithTrack(a, (t, w) => w.Procedures.Star(t)));
        Add("rwy", "ВПП (вылета для вылетающих, посадки для прилетающих)", a => WithTrack(a, (t, w) => w.RunwayOf(t)));
        Add("drwy", "ВПП вылета", a => WithTrack(a, (t, w) => w.Procedures.DepartureRunway(t)));
        Add("arwy", "ВПП посадки", a => WithTrack(a, (t, w) => w.Procedures.ArrivalRunway(t)));
        Add("clr", "Флаг «разрешение получено»", a => a is Radar.Track { ClearanceReceived: true } ? "✓" : "");
        Add("gstate", "Наземный статус (PUSH/TAXI/DEPA)", a => a is Radar.Track t ? t.GroundState : "");
        Add("comm", "Связь: /r — только приём, /t — только текст", a => a is Radar.Track t && t.CommType.Length > 0 ? "/" + t.CommType.ToLowerInvariant() : "");
        Add("warn", "Предупреждения: EMERG, DUPE, SQ, CLAM", a => WithTrack(a, (t, w) => Radar.Warnings.Text(t, w.WarningOf(t))));
        Add("rules", "Правила полёта (I/V)", a => a is Radar.Track t ? t.Rules : "");
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
