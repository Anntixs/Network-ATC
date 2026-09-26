namespace NetworkAtc.Core.Customization;

/// <summary>
/// Every color the radar uses, as "#RRGGBB" / "#AARRGGBB". Users can edit any of them, and
/// themes are plain JSON files, so they can be shared.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "Graphite";

    // Window chrome: flat graphite panels with hairline borders, as on real ATC consoles.
    public string Background { get; set; } = "#12171B";
    public string Panel { get; set; } = "#161C21";
    public string PanelBorder { get; set; } = "#2A343C";
    public string Text { get; set; } = "#C9D1D7";
    public string MutedText { get; set; } = "#7D8A94";
    public string Accent { get; set; } = "#8FB4CC";
    public string Danger { get; set; } = "#E56A5E";
    public string Success { get; set; } = "#5FB88A";

    // Map: quiet blue-grey scope, the own sector slightly lighter than the rest.
    public string RadarBackground { get; set; } = "#1A2126";
    public string Artcc { get; set; } = "#2C3840";
    public string ArtccHigh { get; set; } = "#2C3840";
    public string ArtccLow { get; set; } = "#2C3840";
    public string Sid { get; set; } = "#3F6555";
    public string Star { get; set; } = "#6B5E3E";
    public string LowAirway { get; set; } = "#28333B";
    public string HighAirway { get; set; } = "#28333B";
    public string Geo { get; set; } = "#2E3A42";
    public string Region { get; set; } = "#1D252B";
    public string Runway { get; set; } = "#A9B4BC";
    public string Airport { get; set; } = "#7F8F9A";
    public string Fix { get; set; } = "#56666F";
    public string Vor { get; set; } = "#6E8499";
    public string Ndb { get; set; } = "#7E7496";
    public string Label { get; set; } = "#5B6B75";
    public string SectorLine { get; set; } = "#4A5C6B";
    public string RangeRings { get; set; } = "#222B31";

    // Traffic: white own targets, grey others, yellow selection (ASEL).
    public string Target { get; set; } = "#8B969E";
    public string TargetTracked { get; set; } = "#F2F4F5";
    public string TargetOnGround { get; set; } = "#6E7B85";
    public string TagText { get; set; } = "#8B969E";
    public string TagTextTracked { get; set; } = "#EEF1F3";
    public string TagBackground { get; set; } = "#00000000";
    public string TagSelected { get; set; } = "#F4D26B";
    public string History { get; set; } = "#5E6C76";
    public string PredictionLine { get; set; } = "#56626A";
    /// <summary>STCA: the whole tag and the target of both aircraft.</summary>
    public string Conflict { get; set; } = "#E56A5E";
    public string Emergency { get; set; } = "#E56A5E";
    /// <summary>Emergency squawk (7500, 7600, 7700): only the callsign takes this reddish tint.</summary>
    public string EmergencyCallsign { get; set; } = "#EE8A7E";

    // EuroScope tag states: TagText is "not concerned", TagTextTracked is "assumed".
    public string TagConcerned { get; set; } = "#C9D1D7";
    public string TagTransferToMe { get; set; } = "#E7A94A";
    public string TagTransferFromMe { get; set; } = "#8FC8DE";
    public string TagRedundant { get; set; } = "#6E7B85";
    /// <summary>CLAM, DUPE and wrong-code markers.</summary>
    public string Warning { get; set; } = "#E0B25C";

    // Tools.
    public string RouteLine { get; set; } = "#B08AD0";
    public string Halo { get; set; } = "#E7A94A";
    public string Measure { get; set; } = "#F4D26B";
    public string Centerline { get; set; } = "#303C44";

    public Theme Clone() => (Theme)MemberwiseClone();

    /// <summary>Color properties by name, for the settings editor.</summary>
    public static IReadOnlyList<string> ColorKeys { get; } =
        typeof(Theme).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name != nameof(Name)).Select(p => p.Name).ToList();

    public string Get(string key) => (string)typeof(Theme).GetProperty(key)!.GetValue(this)!;
    public void Set(string key, string value) => typeof(Theme).GetProperty(key)!.SetValue(this, value);

    /// <summary>
    /// The default look of Network-ATC before version 7, recognised by these colors so that profiles which kept it
    /// move to the new default, while a customised theme stays as it is.
    /// </summary>
    public static bool IsVersion6Default(Theme t) =>
        t.Name == "SkyNetwork" && t.Background == "#1C2126" && t.RadarBackground == "#17232C" && t.Panel == "#242A30";

    public static IReadOnlyList<Theme> BuiltIn { get; } =
    [
        new Theme(),
        new Theme
        {
            // Classic en-route console: mid-grey scope, light panels with dark text; reads well in a bright room.
            Name = "Grey Console",
            Background = "#EEF0F1", Panel = "#D9DDE0", PanelBorder = "#8E979D", Text = "#1E2429", MutedText = "#5A646B",
            Accent = "#1F4E79", Danger = "#C0392B", Success = "#2E7D4F",
            RadarBackground = "#3B4349", Artcc = "#5E686F", ArtccHigh = "#5E686F", ArtccLow = "#5E686F", Sid = "#6F9A84", Star = "#A08E62",
            LowAirway = "#56616A", HighAirway = "#56616A", Geo = "#56616A", Region = "#434C53", Runway = "#E6E9EB", Airport = "#C3CACE",
            Fix = "#8C969C", Vor = "#A9B8C6", Ndb = "#B3A8C6", Label = "#9AA4AA", SectorLine = "#9AA4AA", RangeRings = "#4B555C",
            Target = "#B8C0C5", TargetTracked = "#FFFFFF", TargetOnGround = "#9AA4AA", TagText = "#B8C0C5", TagTextTracked = "#FFFFFF",
            TagSelected = "#FFE45C", History = "#7D878D", PredictionLine = "#9AA4AA", Conflict = "#FF5A4F", Emergency = "#FF5A4F",
            EmergencyCallsign = "#FF8A80", TagConcerned = "#E6E9EB", TagTransferToMe = "#FFA23A", TagTransferFromMe = "#7FD6F2",
            TagRedundant = "#8C969C", Warning = "#FFC24A", RouteLine = "#D7A8F0", Halo = "#FFA23A", Measure = "#FFE45C", Centerline = "#6E787F",
        },
        new Theme
        {
            // Deep blue night: the map barely there, the own sector faintly lit, blue selection.
            Name = "Polar Night",
            Background = "#050A12", Panel = "#08101A", PanelBorder = "#152338", Text = "#C3D0DF", MutedText = "#6F82A0",
            Accent = "#7AB8FF", Danger = "#F0605A", Success = "#4FC08A",
            RadarBackground = "#050A12", Artcc = "#14233A", ArtccHigh = "#14233A", ArtccLow = "#14233A", Sid = "#1F4F4A", Star = "#4F4228",
            LowAirway = "#0F1C2E", HighAirway = "#0F1C2E", Geo = "#13223A", Region = "#07101C", Runway = "#8FA6C2", Airport = "#5A7090",
            Fix = "#223A55", Vor = "#3D5E86", Ndb = "#4E4A7A", Label = "#3E5878", SectorLine = "#264463", RangeRings = "#0B1624",
            Target = "#5E7494", TargetTracked = "#E4ECF6", TargetOnGround = "#40587A", TagText = "#6F85A5", TagTextTracked = "#E4ECF6",
            TagSelected = "#7AB8FF", History = "#22395A", PredictionLine = "#2A4A70", Conflict = "#F0605A", Emergency = "#F0605A",
            EmergencyCallsign = "#F08C84", TagConcerned = "#C3D0DF", TagTransferToMe = "#F2A65A", TagTransferFromMe = "#9FE0D0",
            TagRedundant = "#40587A", Warning = "#F2C35A", RouteLine = "#B69CFF", Halo = "#F2A65A", Measure = "#7AB8FF", Centerline = "#1C3450",
        },
    ];
}
