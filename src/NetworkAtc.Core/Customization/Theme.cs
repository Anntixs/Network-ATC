namespace NetworkAtc.Core.Customization;

/// <summary>
/// Every color the radar uses, as "#RRGGBB" / "#AARRGGBB". Users can edit any of them, and
/// themes are plain JSON files, so they can be shared.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "Midnight";

    // Window chrome
    public string Background { get; set; } = "#0E1116";
    public string Panel { get; set; } = "#151A21";
    public string PanelBorder { get; set; } = "#232A33";
    public string Text { get; set; } = "#D7DDE4";
    public string MutedText { get; set; } = "#6B7682";
    public string Accent { get; set; } = "#4FB3FF";
    public string Danger { get; set; } = "#FF5A5F";
    public string Success { get; set; } = "#3DD68C";

    // Map
    public string RadarBackground { get; set; } = "#0B0E13";
    public string Artcc { get; set; } = "#2B3440";
    public string ArtccHigh { get; set; } = "#24303B";
    public string ArtccLow { get; set; } = "#26313A";
    public string Sid { get; set; } = "#2F5D50";
    public string Star { get; set; } = "#5A4A2F";
    public string LowAirway { get; set; } = "#1F2A33";
    public string HighAirway { get; set; } = "#1F2A33";
    public string Geo { get; set; } = "#303A45";
    public string Region { get; set; } = "#161C24";
    public string Runway { get; set; } = "#9AA7B4";
    public string Airport { get; set; } = "#7C8894";
    public string Fix { get; set; } = "#3E4A56";
    public string Vor { get; set; } = "#58708A";
    public string Ndb { get; set; } = "#6A5E80";
    public string Label { get; set; } = "#5C6773";
    public string SectorLine { get; set; } = "#3A4A5C";
    public string RangeRings { get; set; } = "#161D26";

    // Traffic
    public string Target { get; set; } = "#C9D3DD";
    public string TargetTracked { get; set; } = "#FFFFFF";
    public string TargetOnGround { get; set; } = "#6B7682";
    public string TagText { get; set; } = "#AEB9C4";
    public string TagTextTracked { get; set; } = "#F2F6FA";
    public string TagBackground { get; set; } = "#00000000";
    public string TagSelected { get; set; } = "#4FB3FF";
    public string History { get; set; } = "#3A4653";
    public string PredictionLine { get; set; } = "#56616D";
    public string Conflict { get; set; } = "#FF5A5F";
    public string Emergency { get; set; } = "#FF3B30";

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
