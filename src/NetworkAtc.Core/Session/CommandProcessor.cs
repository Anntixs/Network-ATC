using System.Globalization;
using System.Text;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Plugins;
using NetworkAtc.Core.Radar;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Session;

/// <summary>
/// The command line. Plain text goes out on the primary frequency; dot-commands act on the
/// selected aircraft or the station. Plugin commands and profile aliases are resolved here too.
/// </summary>
public sealed class CommandProcessor(AtcSession session, Func<Profile> profile, PluginRegistry plugins, Func<Track?> selected)
{
    private DemoTraffic? _demo;

    /// <summary>Center used by ".demo"; set by the UI to the sector center.</summary>
    public GeoPoint DemoCenter { get; set; } = new(55.97, 37.41);

    /// <summary>The UI should center the map on this point.</summary>
    public event EventHandler<GeoPoint>? CenterRequested;
    /// <summary>The UI should show this aircraft (select it and open its flight plan).</summary>
    public event EventHandler<Track>? SelectRequested;
    /// <summary>Annotations changed; the radar should redraw.</summary>
    public event EventHandler? Changed;

    public static readonly IReadOnlyList<(string Command, string Help)> BuiltIn =
    [
        (".cfl 350 | A045", "разрешённый эшелон/высота выбранного борта"),
        (".hdg 270", "назначенный курс (.hdg — снять)"),
        (".spd 250", "назначенная скорость (.spd — снять)"),
        (".sq [4521]", "назначить код ответчика (без кода — свободный из диапазона)"),
        (".scratch текст", "заметка к борту"),
        (".track / .drop", "взять борт на сопровождение / отпустить"),
        (".find AFL123", "найти борт и выделить его"),
        (".fp [AFL123]", "запросить план полёта с сервера"),
        (".msg ПОЗЫВНОЙ текст", "личное сообщение"),
        (".freq 118.100", "основная частота"),
        (".range 150", "дальность видимости, NM"),
        (".plugins", "список плагинов и их команд"),
        (".demo", "демо-трафик без сервера (повторно — выключить)"),
        (".help", "эта справка"),
    ];

    public async Task<string?> ExecuteAsync(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return null;
        line = ExpandAlias(line);
        if (line[0] != '.')
        {
            await session.SendRadioAsync(line).ConfigureAwait(false);
            return null;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0][1..].ToLowerInvariant();
        var args = parts.Skip(1).ToList();
        string rest = args.Count > 0 ? line[(line.IndexOf(' ') + 1)..].Trim() : "";

        switch (cmd)
        {
            case "cfl":
                return WithSelected(t =>
                {
                    if (args.Count == 0) { t.ClearedAltitude = null; return $"{t.Callsign}: CFL снят"; }
                    if (!TryParseAltitude(args[0], out var feet)) return "Пример: .cfl 350 или .cfl A045";
                    t.ClearedAltitude = feet;
                    return $"{t.Callsign}: CFL {FormatAltitude(feet)}";
                });
            case "hdg":
                return WithSelected(t =>
                {
                    if (args.Count == 0) { t.AssignedHeading = null; return $"{t.Callsign}: курс снят"; }
                    if (!int.TryParse(args[0], out var h) || h is < 1 or > 360) return "Курс 1–360";
                    t.AssignedHeading = h;
                    return $"{t.Callsign}: курс {h:000}";
                });
            case "spd":
                return WithSelected(t =>
                {
                    if (args.Count == 0) { t.AssignedSpeed = null; return $"{t.Callsign}: скорость снята"; }
                    if (!int.TryParse(args[0], out var s) || s is < 60 or > 999) return "Скорость 60–999";
                    t.AssignedSpeed = s;
                    return $"{t.Callsign}: скорость {s}";
                });
            case "sq":
            case "squawk":
                return WithSelected(t =>
                {
                    int code;
                    if (args.Count > 0)
                    {
                        if (!IsSquawk(args[0])) return "Код — 4 цифры от 0 до 7";
                        code = int.Parse(args[0], CultureInfo.InvariantCulture);
                    }
                    else if (FreeSquawk() is { } free) code = free;
                    else return $"Нет свободных кодов в диапазоне {profile().SquawkRange}";
                    t.AssignedSquawk = code;
                    return $"{t.Callsign}: код {code:0000}";
                });
            case "scratch":
                return WithSelected(t =>
                {
                    t.Scratchpad = rest;
                    return rest.Length == 0 ? $"{t.Callsign}: заметка удалена" : null;
                });
            case "track":
                return WithSelected(t => { t.IsTracked = true; return $"{t.Callsign}: на сопровождении"; });
            case "drop":
                return WithSelected(t => { t.IsTracked = false; return $"{t.Callsign}: отпущен"; });
            case "find":
            {
                if (args.Count == 0) return "Пример: .find AFL123";
                var t = session.Find(args[0]);
                if (t == null) return $"{args[0].ToUpperInvariant()} не найден";
                CenterRequested?.Invoke(this, t.Position);
                SelectRequested?.Invoke(this, t);
                return null;
            }
            case "fp":
            {
                string cs = args.Count > 0 ? args[0] : selected()?.Callsign ?? "";
                if (cs.Length == 0) return "Выберите борт или укажите позывной";
                await session.RequestFlightPlanAsync(cs).ConfigureAwait(false);
                return $"Запрошен план полёта {cs.ToUpperInvariant()}";
            }
            case "msg":
            case "chat":
                if (args.Count < 2) return "Пример: .msg AFL123 текст";
                await session.SendPrivateAsync(args[0], rest[(rest.IndexOf(' ') + 1)..]).ConfigureAwait(false);
                return null;
            case "freq":
            {
                if (args.Count == 0 || !Frequency.TryParse(args[0], out var khz)) return "Пример: .freq 118.100";
                var p = profile();
                p.Station.Frequency = Frequency.Format(khz);
                if (session.Info is { } info) await session.UpdateStationAsync(khz, info.VisualRange, info.Center).ConfigureAwait(false);
                return $"Основная частота {p.Station.Frequency}";
            }
            case "range":
            {
                if (args.Count == 0 || !int.TryParse(args[0], out var nm) || nm is < 1 or > 600) return "Дальность 1–600 NM";
                profile().Station.VisualRange = nm;
                if (session.Info is { } info) await session.UpdateStationAsync(info.FrequencyKhz, nm, info.Center).ConfigureAwait(false);
                return $"Дальность видимости {nm} NM";
            }
            case "demo":
                if (_demo != null)
                {
                    _demo.Dispose();
                    _demo = null;
                    return "Демо-трафик выключен";
                }
                if (session.IsConnected) return "Демо-трафик доступен только без подключения к сети";
                _demo = new DemoTraffic(session, DemoCenter);
                return "Демо-трафик включён: 10 бортов вокруг сектора. .demo — выключить";
            case "plugins":
            {
                var sb = new StringBuilder();
                foreach (var c in plugins.Commands.Values.OrderBy(c => c.Name))
                    sb.AppendLine($".{c.Name} — {c.Description} ({c.Owner})");
                return sb.Length == 0 ? "Команд плагинов нет" : sb.ToString().TrimEnd();
            }
            case "help":
            case "?":
                return string.Join('\n', BuiltIn.Select(b => $"{b.Command} — {b.Help}")) +
                       (plugins.Commands.IsEmpty ? "" : "\nКоманды плагинов: .plugins");
        }

        if (plugins.Commands.TryGetValue(cmd, out var pc))
        {
            try { return pc.Handler(args); }
            catch (Exception e) { return $"Ошибка в плагине {pc.Owner}: {e.Message}"; }
        }
        return $"Неизвестная команда .{cmd}. Введите .help";
    }

    /// <summary>".ctc UUEE_TWR 118.1" with alias ".ctc" = "contact $1 on $2" → "contact UUEE_TWR on 118.1".</summary>
    public string ExpandAlias(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !profile().Aliases.TryGetValue(parts[0], out var template)) return line;
        var sb = new StringBuilder(template);
        for (int i = parts.Length - 1; i >= 1; i--) sb.Replace("$" + i, parts[i]);
        if (selected() is { } t) sb.Replace("$cs", t.Callsign);
        return sb.ToString();
    }

    private string? WithSelected(Func<Track, string?> action)
    {
        var t = selected();
        if (t == null) return "Сначала выберите борт на радаре";
        var result = action(t);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public bool TryParseAltitude(string text, out int feet)
    {
        feet = 0;
        text = text.Trim().ToUpperInvariant();
        bool altitude = text.StartsWith('A');
        if (text.StartsWith('F') || altitude) text = text[1..];
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var hundreds) || hundreds is < 0 or > 999) return false;
        feet = hundreds * 100;
        return true;
    }

    private string FormatAltitude(int feet) =>
        (feet >= profile().TransitionAltitude ? "F" : "A") + (feet / 100).ToString("000", CultureInfo.InvariantCulture);

    public static bool IsSquawk(string text) => text.Length == 4 && text.All(c => c is >= '0' and <= '7');

    /// <summary>First code of the profile's squawk range not used or assigned by anyone.</summary>
    public int? FreeSquawk()
    {
        var range = profile().SquawkRange.Split('-');
        if (range.Length != 2 || !IsSquawk(range[0].Trim()) || !IsSquawk(range[1].Trim())) return null;
        int from = int.Parse(range[0], CultureInfo.InvariantCulture), to = int.Parse(range[1], CultureInfo.InvariantCulture);
        var used = new HashSet<int>(session.Tracks.SelectMany(t => new[] { t.Squawk, t.AssignedSquawk ?? -1 }));
        for (int code = from; code <= to; code++)
            if (IsSquawk(code.ToString("0000", CultureInfo.InvariantCulture)) && !used.Contains(code)) return code;
        return null;
    }
}
