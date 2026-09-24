using System.Globalization;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Tags;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Import;

/// <summary>Lower case letters and digits only: "Transfer to me initiated" → "transfertomeinitiated".</summary>
internal static class EsNames
{
    public static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static bool TryInt(string s, out int value) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static bool TryDouble(string s, out double value) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith(';') && !l.TrimStart().StartsWith("//"));
}

/// <summary>
/// EuroScope symbology file: after the "SYMBOLOGY" / "SYMBOLSIZE" headers each line is
/// "Group:Item:color:size:linewidth:linestyle:textalign", e.g. "Sector:active sector boundary:3289650:3.5:0:0:7";
/// the color is a Windows COLORREF (0x00BBGGRR). Symbol drawing definitions (SYMBOL, SYMBOLITEM) are ignored.
/// </summary>
public static class EuroScopeSymbology
{
    public sealed record Item(string Group, string Name, string Color, double Size);

    /// <summary>"group:item" (normalized) → theme colors it sets.</summary>
    private static readonly Dictionary<string, string[]> Map = new()
    {
        ["other:background"] = [nameof(Theme.RadarBackground)],
        ["airports:symbol"] = [nameof(Theme.Airport)],
        ["runways:centerline"] = [nameof(Theme.Runway)],
        ["runways:extendedcenterline"] = [nameof(Theme.Centerline)],
        ["fixes:symbol"] = [nameof(Theme.Fix)],
        ["vors:symbol"] = [nameof(Theme.Vor)],
        ["ndbs:symbol"] = [nameof(Theme.Ndb)],
        ["geo:line"] = [nameof(Theme.Geo)],
        ["sids:line"] = [nameof(Theme.Sid)],
        ["stars:line"] = [nameof(Theme.Star)],
        ["lowairways:line"] = [nameof(Theme.LowAirway)],
        ["highairways:line"] = [nameof(Theme.HighAirway)],
        ["artccboundary:line"] = [nameof(Theme.Artcc)],
        ["artcchighboundary:line"] = [nameof(Theme.ArtccHigh)],
        ["artcclowboundary:line"] = [nameof(Theme.ArtccLow)],
        ["sector:activesectorboundary"] = [nameof(Theme.SectorLine)],
        ["regions:fill"] = [nameof(Theme.Region)],
        ["freetext:text"] = [nameof(Theme.Label)],
        ["airports:name"] = [nameof(Theme.Label)],
        ["other:rangerings"] = [nameof(Theme.RangeRings)],
        ["other:rangering"] = [nameof(Theme.RangeRings)],
        ["other:historydots"] = [nameof(Theme.History)],
        ["other:history"] = [nameof(Theme.History)],
        ["other:leaderline"] = [nameof(Theme.PredictionLine)],
        ["other:predictionline"] = [nameof(Theme.PredictionLine)],
        ["other:route"] = [nameof(Theme.RouteLine)],
        ["other:routeline"] = [nameof(Theme.RouteLine)],
        ["datablock:nonconcerned"] = [nameof(Theme.TagText), nameof(Theme.Target)],
        ["datablock:notified"] = [nameof(Theme.TagConcerned)],
        ["datablock:concerned"] = [nameof(Theme.TagConcerned)],
        ["datablock:assumed"] = [nameof(Theme.TagTextTracked), nameof(Theme.TargetTracked)],
        ["datablock:transfertomeinitiated"] = [nameof(Theme.TagTransferToMe)],
        ["datablock:transferfrommeinitiated"] = [nameof(Theme.TagTransferFromMe)],
        ["datablock:redundant"] = [nameof(Theme.TagRedundant)],
        ["datablock:emergency"] = [nameof(Theme.Emergency)],
        ["datablock:selected"] = [nameof(Theme.TagSelected)],
        ["datablock:activeitem"] = [nameof(Theme.TagSelected)],
        ["datablock:groundtraffic"] = [nameof(Theme.TargetOnGround)],
    };

    public static List<Item> Parse(string text)
    {
        var items = new List<Item>();
        foreach (var line in EsNames.Lines(text))
        {
            var f = line.Split(':');
            if (f.Length < 3 || !long.TryParse(f[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var colorRef)) continue;
            if (f[0].Trim().ToUpperInvariant() is "SYMBOL" or "SYMBOLITEM") continue;
            double size = f.Length > 3 && EsNames.TryDouble(f[3], out var s) ? s : 0;
            items.Add(new Item(f[0].Trim(), f[1].Trim(), SectorParser.ColorFromColorRef(colorRef), size));
        }
        return items;
    }

    /// <summary>Sets the theme colors the items map to; returns the items that have no counterpart.</summary>
    public static List<Item> ApplyTo(IEnumerable<Item> items, Theme theme)
    {
        var unused = new List<Item>();
        var set = new HashSet<string>();
        foreach (var item in items)
        {
            if (!Map.TryGetValue(EsNames.Normalize(item.Group) + ":" + EsNames.Normalize(item.Name), out var keys))
            {
                unused.Add(item);
                continue;
            }
            // The first item wins when several map to the same color (e.g. airport names and free text → labels).
            foreach (var key in keys)
                if (set.Add(key)) theme.Set(key, item.Color);
        }
        return unused;
    }
}

public enum EsTagState { Untracked, Tracked, Detailed }

public sealed record EsTagItem(string Type, bool NewLine, string Left, string Right);

public sealed class EsTagType
{
    public string Definition { get; init; } = "";
    public EsTagState? State { get; init; }
    public List<EsTagItem> Items { get; } = [];
}

public sealed class EsTagFamily
{
    public string Name { get; init; } = "";
    public List<EsTagType> Types { get; } = [];
}

/// <summary>
/// EuroScope tag definitions ("Tag editor" settings file): "TAGFAMILY:name", then per tag type
/// "TAGTYPE:type…" and its items "TAGITEM:item:newline:left function:right function…". Item types and
/// functions are written as names or as the numeric codes of the plugin interface; both are accepted,
/// items of other plugins and unknown lines are reported and skipped.
/// </summary>
public static class EuroScopeTags
{
    private const string NewLineItem = "\n";

    private static readonly Dictionary<string, string> Items = Table(new()
    {
        ["callsign"] = ["callsign", "1"],
        ["fl"] = ["altitude", "altitudemodec", "reportedaltitude", "actualaltitude", "flightlevel", "2"],
        ["cfl"] = ["tempaltitude", "temporaryaltitude", "temporaryaltitudetoplevel", "clearedaltitude", "clearedflightlevel", "cfl", "3"],
        ["rfl"] = ["finalaltitude", "requestedflightlevel", "requestedaltitude", "finallevel", "rfl"],
        ["gs"] = ["groundspeed", "groundspeedwithn", "groundspeedwoutn", "groundspeedwithoutn", "gs"],
        ["gs10"] = ["groundspeed10", "groundspeeddividedby10", "groundspeedtens", "groundspeedshort"],
        ["vs"] = ["verticalspeedindicator", "climbdescentindicator", "climbdescendindicator", "vsi"],
        ["vsfpm"] = ["verticalspeed", "climbrate", "verticalspeedfpm", "verticalrate"],
        ["wtc"] = ["aircraftcategory", "aircraftcategorywithslash", "wakecategory", "wakecategorywithslash", "wtc", "waketurbulencecategory"],
        ["type"] = ["planetype", "aircrafttype", "type", "planetypewithcategory", "aircrafttypewithcategory"],
        ["squawk"] = ["squawk", "transpondercode", "actualsquawk"],
        ["asq"] = ["assignedsquawk", "assignedsquawkcode"],
        ["dest"] = ["destination", "destinationairport", "ades"],
        ["dep"] = ["origin", "departure", "departureairport", "adep"],
        ["ahdg"] = ["assignedheading", "assignedheadingorroute", "assignedheadingorfix"],
        ["hdg"] = ["heading", "actualheading"],
        ["aspd"] = ["assignedspeed", "assignedmach", "assignedspeedmach", "assignedspeedormach"],
        ["scratch"] = ["scratchpad", "scratchpadstring", "scratchpadtext"],
        ["owner"] = ["sectorindicator", "owner", "trackingcontroller", "trackingcontrollerid", "controllerid"],
        ["ho"] = ["handofftarget", "transfertarget", "handoff", "handoffindicator"],
        ["next"] = ["nextsector", "nextcontroller", "nextsectorindicator"],
        ["comm"] = ["communicationtype", "commtype", "voicetype"],
        ["rwy"] = ["assignedrunway", "runway"],
        ["drwy"] = ["departurerunway", "assigneddeparturerunway"],
        ["arwy"] = ["arrivalrunway", "assignedarrivalrunway"],
        ["sid"] = ["sid", "assignedsid"],
        ["star"] = ["star", "assignedstar"],
        ["proc"] = ["sidstar", "procedure", "assignedsidstar"],
        ["clr"] = ["clearancereceivedflag", "clearancereceived", "clearance", "clearanceflag"],
        ["gstate"] = ["groundstatus", "groundstate"],
        ["rules"] = ["flightrules", "flightplantype", "flightplanrules"],
        ["ident"] = ["ident", "squawkident"],
        ["warn"] = ["emergency", "emergencyindicator", "clam", "clamwarning", "duplicatesquawk", "squawkerror", "warning", "warnings", "alerts"],
        [NewLineItem] = ["newline", "linebreak", "nl"],
    });

    private static readonly Dictionary<string, string> Functions = Table(new()
    {
        [TagActions.None] = ["noaction", "none", "nofunction", "0"],
        [TagActions.Select] = ["select", "selectaircraft", "asel"],
        [TagActions.ToggleTrack] = ["assume", "release", "assumerelease", "toggletrack", "assumeaircraft", "releaseaircraft", "starttracking", "stoptracking"],
        [TagActions.Handoff] = ["accepthandoff", "handoff", "handoffpopup", "handoffpopupmenu", "transfer", "initiatehandoff", "transferpopup",
            "acceptorrefusehandoff", "refusehandoff", "nextcontroller", "transfertonextcontroller"],
        [TagActions.AircraftMenu] = ["aircraftfunctionspopup", "aircraftfunctionpopup", "aircraftpopup", "aircraftmenu", "aircraftfunctions"],
        [TagActions.ClearedLevel] = ["clearedaltitudepopup", "temporaryaltitudepopup", "tempaltitudepopup", "cflpopup", "assignaltitude", "altitudepopup"],
        [TagActions.Heading] = ["assignedheadingpopup", "headingpopup", "assignheading"],
        [TagActions.Speed] = ["assignedspeedpopup", "speedpopup", "assignedmachpopup", "assignspeed", "machpopup"],
        [TagActions.Squawk] = ["squawkassign", "assignsquawk", "squawkpopup", "setsquawk", "autoassignsquawk", "squawkassignpopup"],
        [TagActions.FlightPlan] = ["openfpdialog", "openflightplandialog", "flightplandialog", "editflightplan", "fpdialog", "openfp",
            "finalaltitudepopup", "requestedaltitudepopup"],
        [TagActions.Scratchpad] = ["scratchpadedit", "editscratchpad", "scratchpad", "scratchpadeditor"],
        [TagActions.Route] = ["routedisplay", "toggleroutedraw", "showroute", "routedraw", "drawroute", "displayroute"],
        [TagActions.PrivateMessage] = ["privatemessage", "openchat", "chat", "openprivatechat"],
        [TagActions.Procedure] = ["sidpopup", "starpopup", "assignedsidpopup", "assignedstarpopup", "sidstarpopup", "procedurepopup"],
        [TagActions.Runway] = ["runwaypopup", "assignedrunwaypopup", "departurerunwaypopup", "arrivalrunwaypopup"],
        [TagActions.Clearance] = ["clearancereceived", "toggleclearanceflag", "clearanceflag", "setclearancereceived", "clearancereceivedflag"],
    });

    private static Dictionary<string, string> Table(Dictionary<string, string[]> byTarget)
    {
        var result = new Dictionary<string, string>();
        foreach (var (target, names) in byTarget)
            foreach (var n in names) result.TryAdd(n, target);
        return result;
    }

    /// <summary>Tag field for a EuroScope item name or code, or null.</summary>
    public static string? FieldOf(string item) => Items.GetValueOrDefault(EsNames.Normalize(item));

    /// <summary>Click action for a EuroScope function name or code, or null.</summary>
    public static string? ActionOf(string function) =>
        function.Trim().Length == 0 ? TagActions.None : Functions.GetValueOrDefault(EsNames.Normalize(function));

    public static List<EsTagFamily> Parse(string text)
    {
        var families = new List<EsTagFamily>();
        EsTagFamily? family = null;
        EsTagType? type = null;
        foreach (var line in EsNames.Lines(text))
        {
            var f = line.Trim().Split(':');
            switch (f[0].Trim().ToUpperInvariant())
            {
                case "TAGFAMILY":
                    family = new EsTagFamily { Name = string.Join(':', f.Skip(1)).Trim() };
                    families.Add(family);
                    type = null;
                    break;
                case "TAGTYPE":
                    family ??= AddUnnamed(families);
                    type = new EsTagType { Definition = string.Join(':', f.Skip(1)).Trim(), State = StateOf(f.Skip(1).ToList()) };
                    family.Types.Add(type);
                    break;
                case "TAGITEM" when f.Length >= 2:
                    if (type == null)
                    {
                        family ??= AddUnnamed(families);
                        type = new EsTagType { Definition = "", State = EsTagState.Tracked };
                        family.Types.Add(type);
                    }
                    type.Items.Add(ParseItem(f));
                    break;
                case "TAGLINE" or "NEWLINE":
                    type?.Items.Add(new EsTagItem(NewLineItem, true, "", ""));
                    break;
            }
        }
        return families;
    }

    private static EsTagFamily AddUnnamed(List<EsTagFamily> families)
    {
        var f = new EsTagFamily { Name = "" };
        families.Add(f);
        return f;
    }

    private static EsTagItem ParseItem(string[] f)
    {
        string item = f[1].Trim();
        var rest = f.Skip(2).Select(x => x.Trim()).ToList();
        bool newLine = false;
        // "TAGITEM:item:0|1:left:right…": the flag starts the item on a new line.
        if (rest.Count >= 3 && rest[0] is "0" or "1")
        {
            newLine = rest[0] == "1";
            rest.RemoveAt(0);
        }
        return new EsTagItem(item, newLine, rest.Count > 0 ? rest[0] : "", rest.Count > 1 ? rest[1] : "");
    }

    /// <summary>
    /// Tag state from a TAGTYPE definition: names ("Untagged", "Tagged", "Detailed") or the plugin
    /// interface codes 1, 2, 3. Tags of other radar kinds (primary only, uncorrelated, flight plan track) are null.
    /// </summary>
    private static EsTagState? StateOf(List<string> fields)
    {
        var names = fields.Select(EsNames.Normalize).ToList();
        if (names.Any(n => n.Contains("primary") || n.Contains("uncorrelated") || n.Contains("flightplantrack") || n.Contains("coast"))) return null;
        if (names.Any(n => n.Contains("detailed"))) return EsTagState.Detailed;
        if (names.Any(n => n.Contains("untagged") || n.Contains("nottagged") || n.Contains("nottracked") || n.Contains("untracked"))) return EsTagState.Untracked;
        if (names.Any(n => n.Contains("tagged") || n.Contains("tracked") || n.Contains("assumed"))) return EsTagState.Tracked;
        if (fields.Count > 0 && EsNames.TryInt(fields[0], out var code))
            return code switch { 1 => EsTagState.Untracked, 2 => EsTagState.Tracked, 3 => EsTagState.Detailed, _ => null };
        return null;
    }

    /// <summary>
    /// Writes the family's layouts and click functions into the profile; problems go to <paramref name="skipped"/>.
    /// Returns the tag states that got a layout. Warning items are not placed in the layout: Network-ATC shows
    /// warnings above the tag. The EuroScope detailed tag is a whole tag, while Network-ATC's detailed layout is
    /// the lines added under the normal tag, so only the fields the tracked tag does not show are kept for it.
    /// </summary>
    public static List<EsTagState> ApplyTo(EsTagFamily family, Profile profile, List<string> skipped, out int clickBindings)
    {
        clickBindings = 0;
        var done = new List<EsTagState>();
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Network-ATC has one click binding per field: the tracked tag's functions win, then detailed, then untracked.
        foreach (var type in family.Types.OrderBy(t => t.State switch { EsTagState.Tracked => 0, EsTagState.Detailed => 1, _ => 2 }))
        {
            if (type.State is not { } state)
            {
                skipped.Add($"тип тега «{type.Definition}»");
                continue;
            }
            if (done.Contains(state)) continue;
            var lines = new List<List<string>> { new() };
            foreach (var item in type.Items)
            {
                string? field = FieldOf(item.Type);
                if (item.NewLine && lines[^1].Count > 0) lines.Add([]);
                if (field == NewLineItem)
                {
                    if (lines[^1].Count > 0) lines.Add([]);
                    continue;
                }
                if (field == null)
                {
                    var functions = new[] { item.Left, item.Right }.Where(f => f.Length > 0 && ActionOf(f) is null or TagActions.None && f != "0").ToList();
                    skipped.Add($"элемент тега «{item.Type}»" + (functions.Count > 0 ? $" (функции: {string.Join(", ", functions)})" : ""));
                    continue;
                }
                if (!bound.Contains(field) && Bind(profile, field, item, skipped))
                {
                    bound.Add(field);
                    clickBindings++;
                }
                if (field == "warn")
                {
                    profile.Tags.ShowWarnings = true;
                    continue;
                }
                if (!lines[^1].Contains("{" + field + "}")) lines[^1].Add("{" + field + "}");
            }
            if (state == EsTagState.Detailed)
            {
                var shown = TagTemplate.Parse(profile.Tags.Tracked).FieldKeys.Select(k => "{" + k + "}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines) line.RemoveAll(shown.Contains);
            }
            var text = lines.Where(l => l.Count > 0).Select(l => string.Join(' ', l)).ToList();
            if (state != EsTagState.Detailed && !text.Any(l => l.Contains("{callsign}")))
            {
                skipped.Add($"тег «{type.Definition}» без позывного");
                continue;
            }
            if (text.Count == 0)
            {
                skipped.Add($"тег «{type.Definition}»: нет полей сверх обычного тега");
                continue;
            }
            string layout = string.Join('\n', text);
            switch (state)
            {
                case EsTagState.Untracked: profile.Tags.Untracked = layout; break;
                case EsTagState.Tracked: profile.Tags.Tracked = layout; break;
                case EsTagState.Detailed: profile.Tags.Detailed = layout; break;
            }
            done.Add(state);
        }
        return done;
    }

    private static bool Bind(Profile profile, string field, EsTagItem item, List<string> skipped)
    {
        string? left = ActionOf(item.Left), right = ActionOf(item.Right);
        if (left == null) skipped.Add($"функция «{item.Left}»");
        if (right == null) skipped.Add($"функция «{item.Right}»");
        if (left is null or TagActions.None && right is null or TagActions.None) return false;
        var current = profile.TagClicks.GetValueOrDefault(field) ?? new TagClickBinding();
        profile.TagClicks[field] = new TagClickBinding(
            left is null or TagActions.None ? current.Left : left,
            right is null or TagActions.None ? current.Right : right);
        return true;
    }
}

/// <summary>
/// EuroScope general and screen settings files: "key:value" lines (often "m_Name:value"). Only
/// settings with a clear counterpart are used; the others are returned so the report can list them.
/// </summary>
public static class EuroScopeScreenSettings
{
    public static List<(string Key, string Value)> Parse(string text)
    {
        var result = new List<(string, string)>();
        foreach (var line in EsNames.Lines(text))
        {
            int i = line.IndexOfAny([':', '=', '\t']);
            if (i <= 0) continue;
            result.Add((line[..i].Trim(), line[(i + 1)..].Trim()));
        }
        return result;
    }

    /// <summary>Applies one setting; false when it has no counterpart.</summary>
    public static bool Apply(string key, string value, Profile profile)
    {
        string k = key.StartsWith("m_", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
        k = EsNames.Normalize(k);
        switch (k)
        {
            case "transitionaltitude" or "transalt" or "transitionalt" or "transitionlevelaltitude" when EsNames.TryInt(value, out var ta) && ta > 0:
                profile.TransitionAltitude = ta < 300 ? ta * 100 : ta;
                return true;
            case "historydots" or "historydotscount" or "historynumber" or "historycount" or "history" when EsNames.TryInt(value, out var h) && h is >= 0 and <= 50:
                profile.Targets.HistoryDots = h;
                return true;
            case "leader" or "leaderlength" or "leaderlinelength" or "predictionlength" or "predictionline" or "vectorlength" or "speedvector"
                when EsNames.TryDouble(value, out var p) && p is >= 0 and <= 20:
                profile.Targets.PredictionMinutes = p;
                return true;
            // Altitude filter: aircraft below BELOW and above ABOVE are hidden, 0 = no filter.
            case "below" or "filterbelow" or "altitudefilterlow" or "lowerfilter" or "filterlow" or "altfilterlow" when EsNames.TryInt(value, out var lo):
                if (lo > 0) profile.Targets.FilterFloor = lo < 1000 ? lo * 100 : lo;
                return true;
            case "above" or "filterabove" or "altitudefilterhigh" or "upperfilter" or "filterhigh" or "altfilterhigh" when EsNames.TryInt(value, out var hi):
                if (hi > 0) profile.Targets.FilterCeiling = hi < 1000 ? hi * 100 : hi;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>EuroScope alias file: ".hi Good day $callsign" lines, ';' comments.</summary>
public static class EuroScopeAliases
{
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in EsNames.Lines(text))
        {
            string s = line.Trim();
            if (!s.StartsWith('.') || s.Length < 2) continue;
            int space = s.IndexOfAny([' ', '\t']);
            if (space < 0) continue;
            string value = s[(space + 1)..].Trim();
            if (value.Length > 0) result[s[..space]] = value;
        }
        return result;
    }
}

/// <summary>
/// EuroScope ASR (display settings) file: "DisplayTypeName:…", "SECTORFILE:…", "TAGFAMILY:…",
/// "WINDOWAREA:lat1:lon1:lat2:lon2", "BELOW:", "ABOVE:", "HISTORY_DOTS:", "LEADER:" and the shown map
/// elements as "Category:Name:item" ("Geo:UUEE GROUND:", "Fixes:ABC:symbol", "Fixes:ABC:name").
/// </summary>
public sealed class EuroScopeAsr
{
    /// <summary>ASR category (normalized) → Network-ATC layer.</summary>
    private static readonly Dictionary<string, string> Categories = new()
    {
        ["airports"] = "AIRPORTS", ["runways"] = "RUNWAYS", ["fixes"] = "FIXES", ["vors"] = "VOR", ["ndbs"] = "NDB",
        ["geo"] = "GEO", ["artccboundary"] = "ARTCC", ["artcchighboundary"] = "ARTCC HIGH", ["artcclowboundary"] = "ARTCC LOW",
        ["lowairways"] = "LOW AIRWAY", ["highairways"] = "HIGH AIRWAY", ["sids"] = "SID", ["stars"] = "STAR",
        ["regions"] = "REGIONS", ["freetext"] = "FREETEXT", ["labels"] = "LABELS",
    };

    /// <summary>Layers an ASR decides about; the others (range rings, sector lines, labels) are left alone unless listed.</summary>
    public static IReadOnlyList<string> ControlledLayers { get; } =
        ["AIRPORTS", "RUNWAYS", "FIXES", "FIX NAMES", "VOR", "NDB", "GEO", "ARTCC", "ARTCC HIGH", "ARTCC LOW",
         "LOW AIRWAY", "HIGH AIRWAY", "SID", "STAR", "REGIONS", "FREETEXT"];

    public string DisplayType { get; private set; } = "";
    public string? SectorFile { get; private set; }
    public string? TagFamily { get; private set; }
    /// <summary>Layers with at least one element shown.</summary>
    public HashSet<string> VisibleLayers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int ElementCount { get; private set; }
    public (GeoPoint A, GeoPoint B)? WindowArea { get; private set; }
    /// <summary>Other "KEY:value" lines.</summary>
    public List<(string Key, string Value)> Values { get; } = [];

    public static EuroScopeAsr Parse(string text)
    {
        var asr = new EuroScopeAsr();
        foreach (var line in EsNames.Lines(text))
        {
            var f = line.Split(':');
            string key = f[0].Trim();
            if (f.Length < 2) continue;
            string value = string.Join(':', f.Skip(1)).Trim();
            if (f.Length >= 3 && Categories.TryGetValue(EsNames.Normalize(key), out var layer))
            {
                asr.ElementCount++;
                asr.VisibleLayers.Add(layer);
                if (layer == "FIXES" && f[^1].Trim().Equals("name", StringComparison.OrdinalIgnoreCase)) asr.VisibleLayers.Add("FIX NAMES");
                continue;
            }
            switch (key.ToUpperInvariant())
            {
                case "DISPLAYTYPENAME": asr.DisplayType = value; break;
                case "SECTORFILE": if (value.Length > 0) asr.SectorFile = value; break;
                case "TAGFAMILY": if (value.Length > 0) asr.TagFamily = value; break;
                case "WINDOWAREA" when f.Length >= 5:
                    if (SectorParser.TryParseCoordinate(f[1], out var lat1) && SectorParser.TryParseCoordinate(f[2], out var lon1) &&
                        SectorParser.TryParseCoordinate(f[3], out var lat2) && SectorParser.TryParseCoordinate(f[4], out var lon2))
                        asr.WindowArea = (new GeoPoint(lat1, lon1), new GeoPoint(lat2, lon2));
                    break;
                default: asr.Values.Add((key, value)); break;
            }
        }
        return asr;
    }

    /// <summary>Center and scale for the window area, for a radar window of about 1200 × 800 pixels.</summary>
    public (GeoPoint Center, double NmPerPixel)? View()
    {
        if (WindowArea is not var (a, b)) return null;
        var center = new GeoPoint((a.Latitude + b.Latitude) / 2, (a.Longitude + b.Longitude) / 2);
        double heightNm = Math.Abs(a.Latitude - b.Latitude) * 60;
        double widthNm = Math.Abs(a.Longitude - b.Longitude) * 60 * Math.Cos(center.Latitude * Math.PI / 180);
        double scale = Math.Max(widthNm / 1200, heightNm / 800);
        return scale > 0 ? (center, Math.Clamp(scale, 0.005, 5)) : null;
    }
}
