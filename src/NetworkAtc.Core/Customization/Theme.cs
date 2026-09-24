namespace NetworkAtc.Core.Customization;

/// <summary>
/// Every color the radar uses, as "#RRGGBB" / "#AARRGGBB". Users can edit any of them, and
/// themes are plain JSON files, so they can be shared.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "SkyNetwork";

    // Window chrome: graphite panels, as in Aurora; compact like EuroScope.
    public string Background { get; set; } = "#1C2126";
    public string Panel { get; set; } = "#242A30";
    public string PanelBorder { get; set; } = "#353D46";
    public string Text { get; set; } = "#E1E6EB";
    public string MutedText { get; set; } = "#8A95A1";
    public string Accent { get; set; } = "#3FA7D6";
    public string Danger { get; set; } = "#E5484D";
    public string Success { get; set; } = "#3DBB7E";

    // Map: deep blue-grey scope.
    public string RadarBackground { get; set; } = "#17232C";
    public string Artcc { get; set; } = "#3C5566";
    public string ArtccHigh { get; set; } = "#34495A";
    public string ArtccLow { get; set; } = "#34495A";
    public string Sid { get; set; } = "#3F7A63";
    public string Star { get; set; } = "#7D6A3F";
    public string LowAirway { get; set; } = "#26394A";
    public string HighAirway { get; set; } = "#26394A";
    public string Geo { get; set; } = "#2C4150";
    public string Region { get; set; } = "#1D2E39";
    public string Runway { get; set; } = "#C9D2DA";
    public string Airport { get; set; } = "#8FA3B3";
    public string Fix { get; set; } = "#4B6474";
    public string Vor { get; set; } = "#6C8FB0";
    public string Ndb { get; set; } = "#8C7BB0";
    public string Label { get; set; } = "#7F93A3";
    public string SectorLine { get; set; } = "#52708A";
    public string RangeRings { get; set; } = "#1F303B";

    // Traffic: white targets, light tags, yellow selection (ASEL).
    public string Target { get; set; } = "#DCE6EE";
    public string TargetTracked { get; set; } = "#FFFFFF";
    public string TargetOnGround { get; set; } = "#7B8C99";
    public string TagText { get; set; } = "#B9C8D4";
    public string TagTextTracked { get; set; } = "#FFFFFF";
    public string TagBackground { get; set; } = "#00000000";
    public string TagSelected { get; set; } = "#F2C94C";
    public string History { get; set; } = "#4F6B7C";
    public string PredictionLine { get; set; } = "#6F8797";
    public string Conflict { get; set; } = "#FF5A5F";
    public string Emergency { get; set; } = "#FF3B30";

    // EuroScope tag states: TagText is "not concerned", TagTextTracked is "assumed".
    public string TagConcerned { get; set; } = "#DCE8F1";
    public string TagTransferToMe { get; set; } = "#FF9F1C";
    public string TagTransferFromMe { get; set; } = "#6FD3FF";
    public string TagRedundant { get; set; } = "#6E8494";
    /// <summary>CLAM, DUPE and wrong-code markers.</summary>
    public string Warning { get; set; } = "#FFB020";

    // Tools.
    public string RouteLine { get; set; } = "#C58BE8";
    public string Halo { get; set; } = "#FF9F1C";
    public string Measure { get; set; } = "#F2C94C";
    public string Centerline { get; set; } = "#56707F";

    public Theme Clone() => (Theme)MemberwiseClone();

    /// <summary>Color properties by name, for the settings editor.</summary>
    public static IReadOnlyList<string> ColorKeys { get; } =
        typeof(Theme).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name != nameof(Name)).Select(p => p.Name).ToList();

    public string Get(string key) => (string)typeof(Theme).GetProperty(key)!.GetValue(this)!;
    public void Set(string key, string value) => typeof(Theme).GetProperty(key)!.SetValue(this, value);

    public static IReadOnlyList<Theme> BuiltIn { get; } =
    [
        new Theme(),
        new Theme
        {
            // Close to EuroScope's own palette, for controllers who want the classic look.
            Name = "EuroScope Classic",
            Background = "#2B3238", Panel = "#353E46", PanelBorder = "#4A555F", Text = "#E6E6E6", MutedText = "#9AA3AB",
            Accent = "#8FB8D8", RadarBackground = "#3A4852", Artcc = "#6B7C88", ArtccHigh = "#5E6F7B", ArtccLow = "#5E6F7B",
            Sid = "#5A8A6A", Star = "#8A7A5A", LowAirway = "#4C5C68", HighAirway = "#4C5C68", Geo = "#56666F", Region = "#34424B",
            Runway = "#E6E6E6", Airport = "#B4BEC6", Fix = "#6F808C", Vor = "#8FA8BD", Ndb = "#A89BBD", Label = "#A3AFB8",
            SectorLine = "#7F93A3", RangeRings = "#44535D", Target = "#E6E6E6", TargetTracked = "#FFFFFF", TargetOnGround = "#9AA3AB",
            TagText = "#9AA8B2", TagTextTracked = "#FFFFFF", TagConcerned = "#E6E6E6", TagSelected = "#FFE24A",
            TagTransferToMe = "#FF8C00", TagTransferFromMe = "#00D8FF", TagRedundant = "#7F8F9A", History = "#76889A",
            PredictionLine = "#A3AFB8", Centerline = "#6F808C",
        },
        new Theme
        {
            Name = "Midnight",
            Background = "#0E1116", Panel = "#151A21", PanelBorder = "#232A33", Text = "#D7DDE4", MutedText = "#6B7682",
            Accent = "#4FB3FF", Danger = "#FF5A5F", Success = "#3DD68C",
            RadarBackground = "#0B0E13", Artcc = "#2B3440", ArtccHigh = "#24303B", ArtccLow = "#26313A", Sid = "#2F5D50",
            Star = "#5A4A2F", LowAirway = "#1F2A33", HighAirway = "#1F2A33", Geo = "#303A45", Region = "#161C24",
            Runway = "#9AA7B4", Airport = "#7C8894", Fix = "#3E4A56", Vor = "#58708A", Ndb = "#6A5E80", Label = "#5C6773",
            SectorLine = "#3A4A5C", RangeRings = "#161D26", Target = "#C9D3DD", TargetTracked = "#FFFFFF", TargetOnGround = "#6B7682",
            TagText = "#AEB9C4", TagTextTracked = "#F2F6FA", TagSelected = "#4FB3FF", History = "#3A4653", PredictionLine = "#56616D",
        },
        new Theme
        {
            Name = "Graphite",
            Background = "#1A1B1E", Panel = "#222326", PanelBorder = "#2E3035", Text = "#E4E4E7", MutedText = "#7C7F87",
            Accent = "#F5A524", RadarBackground = "#18191C", Artcc = "#3A3C42", ArtccHigh = "#34363B", ArtccLow = "#34363B",
            Geo = "#3C3F45", Region = "#202125", RangeRings = "#212226", Fix = "#4A4D55", Label = "#6B6F78",
            TagSelected = "#F5A524",
        },
        new Theme
        {
            Name = "Scope Green",
            Background = "#050A07", Panel = "#0A140E", PanelBorder = "#143020", Text = "#9DF5B5", MutedText = "#3F7A52",
            Accent = "#4CFF8A", RadarBackground = "#030805", Artcc = "#0F3A20", ArtccHigh = "#0D321C", ArtccLow = "#0D321C",
            Sid = "#1C5A34", Star = "#3E5A1C", Geo = "#12402A", Region = "#07120B", Runway = "#6FD08F", Airport = "#4FA06D",
            Fix = "#1F5A36", Vor = "#2F7A4C", Ndb = "#2F7A4C", Label = "#3F7A52", RangeRings = "#0A1A10",
            Target = "#7CFFA6", TargetTracked = "#C8FFD9", TagText = "#6FE095", TagTextTracked = "#C8FFD9",
            History = "#1E5A34", PredictionLine = "#2F7A4C", TagSelected = "#FFFFFF",
        },
        new Theme
        {
            Name = "Daylight",
            Background = "#F4F6F8", Panel = "#FFFFFF", PanelBorder = "#DCE1E6", Text = "#1D2530", MutedText = "#7A8591",
            Accent = "#0A6CFF", RadarBackground = "#E9EDF1", Artcc = "#B8C2CC", ArtccHigh = "#C2CBD4", ArtccLow = "#C2CBD4",
            Sid = "#7FB8A4", Star = "#C8A874", Geo = "#AEB8C2", Region = "#DDE3E9", Runway = "#3A4450", Airport = "#56616D",
            Fix = "#A0AAB4", Vor = "#6C88A6", Ndb = "#8A7AA6", Label = "#7A8591", SectorLine = "#9AAABB", RangeRings = "#DDE3E9",
            Target = "#1D2530", TargetTracked = "#000000", TargetOnGround = "#8A96A2", TagText = "#34404C",
            TagTextTracked = "#000000", History = "#AEB8C2", PredictionLine = "#8A96A2", TagSelected = "#0A6CFF",
        },
    ];
}
