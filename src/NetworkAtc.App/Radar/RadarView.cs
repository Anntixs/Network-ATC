using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetworkAtc.App.Services;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.EsPlugins;
using NetworkAtc.Core.Geo;
using NetworkAtc.Core.Plugins;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Tags;
using NetworkAtc.Plugins;

namespace NetworkAtc.App.Radar;

/// <summary>
/// The radar scope: draws the sector file, traffic, tags, STCA and plugin overlays.
/// Mouse: wheel zooms at the cursor, drag on empty space pans, click selects a target,
/// drag a tag to move it, right click opens the aircraft menu.
/// </summary>
public sealed class RadarView : FrameworkElement
{
    private static readonly Typeface UiTypeface = new(AppFonts.Family(AppFonts.Ui), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private const double MinNmPerPixel = 0.002, MaxNmPerPixel = 5;

    private Projection _projection = new(new GeoPoint(55.97, 37.41));
    private double _cx, _cy;               // view center, plane NM
    private double _nmPerPixel = 0.15;
    private Point? _panStart;
    private (double X, double Y) _panOrigin;
    private string? _draggingTag;
    private Point _dragStart;
    private Vector _dragOrigin;
    private Point _mouse;
    private readonly Dictionary<string, Vector> _tagOffsets = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tag rectangles in drawing order: the last one is on top.</summary>
    private readonly List<(string Callsign, Rect Bounds)> _tagBounds = [];
    private readonly List<(Rect Bounds, string Callsign, string Field)> _fieldZones = [];
    private (string Callsign, string Field)? _hoveredField;
    private (string Callsign, string? Field, Point Start)? _pendingTagClick;
    private readonly Dictionary<string, Point> _targetPoints = new(StringComparer.OrdinalIgnoreCase);
    private Typeface _tagTypeface = new(AppFonts.Family(AppFonts.Mono), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private string _tagFontName = AppFonts.Mono;

    /// <summary>End of a measuring line: a moving aircraft or a fixed point.</summary>
    private sealed record Anchor(Track? Track, GeoPoint Point)
    {
        public GeoPoint Position => Track?.Position ?? Point;
    }

    private readonly List<(Anchor A, Anchor B)> _measures = [];
    private Anchor? _measureStart;
    private Point _rightDown;
    private readonly Dictionary<string, TagTemplate> _templates = [];
    private (EsScreenObject Object, Point Start, int Button, bool Moved)? _objectPress;
    private EsScreenObject? _overObject;

    public RadarView()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    // ---- inputs set by the window -------------------------------------------------------

    public SectorFile? Sector { get; private set; }
    public Profile Profile { get; set; } = new();
    public TagFields TagFields { get; set; } = new();
    public PluginRegistry? Plugins { get; set; }
    public Func<IReadOnlyList<Track>> Tracks { get; set; } = () => [];
    public IReadOnlyList<Conflict> Conflicts { get; set; } = [];
    /// <summary>Aircraft heard on the radio right now (voice): drawn with a ring around the symbol.</summary>
    public Func<string, bool> IsHeard { get; set; } = _ => false;
    public Track? Selected { get; private set; }
    public Track? Hovered { get; private set; }
    /// <summary>Tag states, warnings, routes and runways (EuroScope logic); optional.</summary>
    public NetworkAtc.Core.Session.Workspace? Workspace { get; set; }
    /// <summary>True while something on the scope blinks (an aircraft offered to us); the window keeps redrawing.</summary>
    public bool NeedsAnimation { get; private set; }

    public event EventHandler<Track?>? SelectionChanged;
    public event EventHandler? ViewChanged;
    /// <summary>Right click on a target symbol; the window builds the context menu.</summary>
    public event EventHandler<Track>? TargetMenuRequested;
    /// <summary>Click on a tag (Field is null when the click was not on a field).</summary>
    public event EventHandler<TagClickEventArgs>? TagClicked;
    /// <summary>
    /// Mouse on an object a EuroScope plugin put on the screen: kind 0 over, 1 down, 2 up, 3 click, 4 double click,
    /// 5 move; the last value is the button (1 left, 2 middle, 3 right) or, for a move, 1 when the button was released.
    /// </summary>
    public event Action<EsScreenObject, int, Point, int>? ScreenObjectMouse;

    /// <summary>What the EuroScope plugins drew on this screen: under the tags, over them, and their clickable objects.</summary>
    public PluginDrawing? EsDrawing { get; set; }
    /// <summary>A plugin display that draws everything itself: the sector and traffic of Network-ATC are not drawn.</summary>
    public bool HideOwnContent { get; set; }
    /// <summary>Color of a tag field chosen by its plugin (field key, callsign), or null for the tag color.</summary>
    public Func<string, string, string?>? FieldColor { get; set; }

    /// <summary>The projection centre and the view centre on its plane (NM): what the plugin host needs to draw like us.</summary>
    public (GeoPoint ProjectionCenter, double Cx, double Cy) Geometry => (_projection.Center, _cx, _cy);
    public GeoPoint GeoAt(Point p) => ToGeo(p);
    public Point ScreenAt(GeoPoint p) => ToScreen(p);

    public double NmPerPixel => _nmPerPixel;
    public GeoPoint ViewCenter => _projection.FromPlane(_cx, _cy);
    public GeoPoint MouseGeo => ToGeo(_mouse);

    public void SetSector(SectorFile? sector)
    {
        Sector = sector;
        var center = sector?.Center ?? new GeoPoint(55.97, 37.41);
        if (center == default) center = new GeoPoint(55.97, 37.41);
        _projection = new Projection(center);
        _cx = _cy = 0;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CenterOn(GeoPoint p)
    {
        (_cx, _cy) = _projection.ToPlane(p);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetView(GeoPoint center, double nmPerPixel)
    {
        (_cx, _cy) = _projection.ToPlane(center);
        _nmPerPixel = Math.Clamp(nmPerPixel, MinNmPerPixel, MaxNmPerPixel);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Zoom(double factor) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), factor);

    public void Select(Track? track)
    {
        if (ReferenceEquals(Selected, track)) return;
        Selected = track;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, track);
    }

    /// <summary>Visible range across the shorter side of the view, NM.</summary>
    public double RangeNm => Math.Min(ActualWidth, ActualHeight) * _nmPerPixel;

    // ---- coordinates ------------------------------------------------------------------------

    private Point ToScreen(GeoPoint p)
    {
        var (x, y) = _projection.ToPlane(p);
        return new Point((x - _cx) / _nmPerPixel + ActualWidth / 2, ActualHeight / 2 - (y - _cy) / _nmPerPixel);
    }

    private GeoPoint ToGeo(Point s) =>
        _projection.FromPlane((s.X - ActualWidth / 2) * _nmPerPixel + _cx, (ActualHeight / 2 - s.Y) * _nmPerPixel + _cy);

    private bool OnScreen(Point p, double margin = 50)
    {
        margin = Math.Max(margin, _cullMargin);
        return p.X > -margin && p.Y > -margin && p.X < ActualWidth + margin && p.Y < ActualHeight + margin;
    }

    // ---- rendering -------------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var theme = Profile.Theme;
        if (Map != null)
        {
            // The map lies underneath in its own layer; this element only needs to be hit-testable everywhere.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            Map.Background = Paint.Brush(theme.RadarBackground);
        }
        else dc.DrawRectangle(Paint.Brush(theme.RadarBackground), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (!string.Equals(_tagFontName, Profile.Tags.FontFamily, StringComparison.Ordinal))
        {
            _tagFontName = Profile.Tags.FontFamily;
            _tagTypeface = new Typeface(AppFonts.Family(_tagFontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _textCache.Clear();
        }
        var drawing = EsDrawing;
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        if (Map != null) UpdateMap(theme);
        if (HideOwnContent)
        {
            _tagBounds.Clear();
            _fieldZones.Clear();
            _targetPoints.Clear();
            if (drawing?.Back is { } onlyBack) dc.DrawImage(onlyBack, full);
            if (drawing?.Front is { } onlyFront) dc.DrawImage(onlyFront, full);
            return;
        }
        if (Map == null)
        {
            if (Profile.IsLayerVisible("RANGE RINGS")) DrawRangeRings(dc, theme);
            if (Sector != null) DrawSector(dc, Sector, theme);
        }
        if (Sector != null && Profile.IsLayerVisible("CENTERLINES")) DrawCenterlines(dc, Sector, theme);
        if (drawing?.Back is { } back) dc.DrawImage(back, full);
        DrawOverlays(dc);
        DrawRoutes(dc, theme);
        DrawTraffic(dc, theme);
        if (drawing?.Front is { } front) dc.DrawImage(front, full);
        DrawMeasures(dc, theme);
        DrawScaleBar(dc, theme);
    }

    // ---- map layer ------------------------------------------------------------------------------

    /// <summary>The layer the sector map is drawn into (under this element); null draws the map here, every frame.</summary>
    public MapLayer? Map { get; set; }

    /// <summary>How long the view must stay still before the moved map picture is redrawn crisply.</summary>
    private static readonly TimeSpan MapSettle = TimeSpan.FromMilliseconds(140);
    private readonly System.Windows.Threading.DispatcherTimer _mapSettleTimer = new() { Interval = MapSettle };
    private (double Cx, double Cy, double NmPerPixel, double W, double H) _mapDrawnView, _lastView;
    private DateTime _lastViewChange;
    private string _mapContentKey = "";
    /// <summary>While the map is drawn, things this far outside the screen are drawn too, so panning shows them.</summary>
    private double _cullMargin;

    private string MapContentKey(Theme theme)
    {
        var key = new System.Text.StringBuilder(Sector != null ? MapKey(Sector, theme) : "no sector");
        foreach (var layer in new[] { "RANGE RINGS", "FIXES", "FIX NAMES", "VOR", "NDB", "AIRPORTS", "LABELS", "FREETEXT" })
            key.Append(Profile.IsLayerVisible(layer) ? '1' : '0');
        key.Append('|').Append(HideOwnContent).Append('|').Append(Profile.RangeRingCount).Append('|').Append(Profile.RangeRingSpacingNm)
           .Append('|').Append(theme.RangeRings).Append(theme.Fix).Append(theme.Vor).Append(theme.Ndb).Append(theme.Airport).Append(theme.Label)
           .Append('|').Append(VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return key.ToString();
    }

    /// <summary>
    /// Keeps the map layer in step with the view: while the view moves the last picture is only shifted and scaled
    /// (cheap, on the graphics card); once it has been still for a moment, or the map itself changed, it is redrawn.
    /// </summary>
    private void UpdateMap(Theme theme)
    {
        var view = (_cx, _cy, _nmPerPixel, ActualWidth, ActualHeight);
        var now = DateTime.UtcNow;
        if (view != _lastView)
        {
            _lastView = view;
            _lastViewChange = now;
        }
        string key = MapContentKey(theme);
        bool sizeChanged = view.ActualWidth != _mapDrawnView.W || view.ActualHeight != _mapDrawnView.H;
        if (key != _mapContentKey || sizeChanged || (view != _mapDrawnView && now - _lastViewChange >= MapSettle))
        {
            RenderMap(theme, key, view);
            return;
        }
        if (view == _mapDrawnView) return;
        // Drawn: screen = k0 * (x, -y) + b0; now: k1 * (x, -y) + b1. So now = s * drawn + (b1 - s * b0), s = k1 / k0.
        double k0 = 1 / _mapDrawnView.NmPerPixel, k1 = 1 / _nmPerPixel, s = k1 / k0;
        double b0x = _mapDrawnView.W / 2 - _mapDrawnView.Cx * k0, b0y = _mapDrawnView.H / 2 + _mapDrawnView.Cy * k0;
        double b1x = ActualWidth / 2 - _cx * k1, b1y = ActualHeight / 2 + _cy * k1;
        Map!.Follow(new Matrix(s, 0, 0, s, b1x - s * b0x, b1y - s * b0y));
        if (!_mapSettleTimer.IsEnabled)
        {
            _mapSettleTimer.Tick -= OnMapSettle;
            _mapSettleTimer.Tick += OnMapSettle;
            _mapSettleTimer.Start();
        }
    }

    private void OnMapSettle(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow - _lastViewChange < MapSettle) return; // still moving: wait for the next tick
        _mapSettleTimer.Stop();
        InvalidateVisual();
    }

    private void RenderMap(Theme theme, string key, (double, double, double, double, double) view)
    {
        _mapSettleTimer.Stop();
        using (var dc = Map!.Open())
        {
            if (!HideOwnContent)
            {
                _cullMargin = Math.Max(ActualWidth, ActualHeight) * 0.6;
                try
                {
                    if (Profile.IsLayerVisible("RANGE RINGS")) DrawRangeRings(dc, theme);
                    if (Sector != null) DrawSector(dc, Sector, theme);
                }
                finally { _cullMargin = 0; }
            }
        }
        _mapContentKey = key;
        _mapDrawnView = view;
    }

    private string MapColor(string? fileColor, string themeColor) =>
        Profile.UseSectorFileColors && fileColor != null ? fileColor : themeColor;

    private void DrawRangeRings(DrawingContext dc, Theme theme)
    {
        var center = ToScreen(Sector?.Center ?? _projection.Center);
        var pen = Paint.Pen(theme.RangeRings, 1);
        for (int i = 1; i <= Profile.RangeRingCount; i++)
        {
            double r = i * Profile.RangeRingSpacingNm / _nmPerPixel;
            dc.DrawEllipse(null, pen, center, r, r);
        }
    }

    /// <summary>
    /// One piece of the static map in projection-plane coordinates (NM): all lines of one color, the regions of one color,
    /// the sector lines or the runways. Built once per sector, theme and layer choice; every frame only moves and
    /// scales it, which is far cheaper than projecting and drawing thousands of segments one by one.
    /// </summary>
    private sealed record MapPiece(Geometry Geometry, string? Fill, string? Stroke, bool Dashed, bool Runway);
    private List<MapPiece> _mapPieces = [];
    private string _mapKey = "";
    private static readonly string[] MapLayers = ["REGIONS", "GEO", "LOW AIRWAY", "HIGH AIRWAY", "ARTCC LOW", "ARTCC HIGH", "ARTCC", "SID", "STAR", "SECTORLINES", "RUNWAYS"];

    private string MapKey(SectorFile s, Theme theme)
    {
        var key = new System.Text.StringBuilder();
        key.Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s)).Append('|').Append(_projection.Center)
           .Append('|').Append(Profile.UseSectorFileColors)
           .Append('|').Append(theme.Region).Append(theme.Geo).Append(theme.LowAirway).Append(theme.HighAirway).Append(theme.ArtccLow)
           .Append(theme.ArtccHigh).Append(theme.Artcc).Append(theme.Sid).Append(theme.Star).Append(theme.SectorLine).Append(theme.Runway);
        foreach (var layer in MapLayers) key.Append(Profile.IsLayerVisible(layer) ? '1' : '0');
        foreach (var layer in s.CustomLayers) key.Append('|').Append(layer).Append(Profile.IsLayerVisible(layer) ? '1' : '0');
        return key.ToString();
    }

    private Point ToPlane(GeoPoint p)
    {
        var (x, y) = _projection.ToPlane(p);
        return new Point(x, y);
    }

    private List<MapPiece> BuildMap(SectorFile s, Theme theme)
    {
        var pieces = new List<MapPiece>();

        if (Profile.IsLayerVisible("REGIONS"))
        {
            foreach (var group in s.Regions.Where(r => r.Points.Count > 2).GroupBy(r => MapColor(r.Color, theme.Region)))
            {
                var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
                using (var ctx = geo.Open())
                    foreach (var region in group)
                    {
                        ctx.BeginFigure(ToPlane(region.Points[0]), true, true);
                        for (int i = 1; i < region.Points.Count; i++) ctx.LineTo(ToPlane(region.Points[i]), false, false);
                    }
                geo.Freeze();
                pieces.Add(new MapPiece(geo, group.Key, null, false, false));
            }
        }

        void AddLines(IEnumerable<SectorLine> lines, Func<SectorLine, string> color, bool dashed)
        {
            foreach (var group in lines.GroupBy(color))
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                    foreach (var l in group)
                    {
                        ctx.BeginFigure(ToPlane(l.From), false, false);
                        ctx.LineTo(ToPlane(l.To), true, false);
                    }
                geo.Freeze();
                pieces.Add(new MapPiece(geo, null, group.Key, dashed, false));
            }
        }

        (string Layer, string Color, bool Dashed)[] layers =
        [
            ("GEO", theme.Geo, false), ("LOW AIRWAY", theme.LowAirway, false), ("HIGH AIRWAY", theme.HighAirway, false),
            ("ARTCC LOW", theme.ArtccLow, false), ("ARTCC HIGH", theme.ArtccHigh, false), ("ARTCC", theme.Artcc, false),
            ("SID", theme.Sid, false), ("STAR", theme.Star, false),
        ];
        foreach (var (layer, color, dashed) in layers)
        {
            if (!Profile.IsLayerVisible(layer) || !s.Lines.TryGetValue(layer, out var lines)) continue;
            string layerColor = s.LayerColors.TryGetValue(layer, out var lc) && Profile.UseSectorFileColors ? lc : color;
            AddLines(lines, l => MapColor(l.Color, layerColor), dashed);
        }
        // Layers that only exist in Network-ATC sectors always use their own colors.
        foreach (var layer in s.CustomLayers)
        {
            if (!Profile.IsLayerVisible(layer)) continue;
            string layerColor = s.LayerColors.GetValueOrDefault(layer, theme.Geo);
            AddLines(s.Lines[layer], l => l.Color ?? layerColor, false);
        }

        if (Profile.IsLayerVisible("SECTORLINES"))
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
                foreach (var line in s.SectorLines.Values.Where(l => l.Count > 1))
                {
                    ctx.BeginFigure(ToPlane(line[0]), false, false);
                    for (int i = 1; i < line.Count; i++) ctx.LineTo(ToPlane(line[i]), true, false);
                }
            geo.Freeze();
            pieces.Add(new MapPiece(geo, null, theme.SectorLine, true, false));
        }

        if (Profile.IsLayerVisible("RUNWAYS"))
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
                foreach (var r in s.Runways)
                {
                    ctx.BeginFigure(ToPlane(r.End1), false, false);
                    ctx.LineTo(ToPlane(r.End2), true, false);
                }
            geo.Freeze();
            pieces.Add(new MapPiece(geo, null, theme.Runway, false, true));
        }
        return pieces;
    }

    private void DrawSector(DrawingContext dc, SectorFile s, Theme theme)
    {
        string key = MapKey(s, theme);
        if (key != _mapKey)
        {
            _mapPieces = BuildMap(s, theme);
            _mapKey = key;
        }
        // Plane (NM, north up) to screen: the same formula as ToScreen, as one matrix.
        double k = 1 / _nmPerPixel;
        var view = new MatrixTransform(k, 0, 0, -k, ActualWidth / 2 - _cx * k, ActualHeight / 2 + _cy * k);
        view.Freeze();
        // Rounded to half pixels: pens are cached by width, a continuous width would fill the cache while zooming.
        double runwayWidth = Math.Round(Math.Clamp(0.03 / _nmPerPixel, 1.5, 6) * 2) / 2;
        foreach (var piece in _mapPieces)
        {
            // The transform sits on the geometry, not on the drawing context, so lines keep their width in pixels.
            var placed = new GeometryGroup { Transform = view, FillRule = FillRule.Nonzero };
            placed.Children.Add(piece.Geometry);
            dc.DrawGeometry(piece.Fill != null ? Paint.Brush(piece.Fill) : null,
                piece.Stroke != null ? Paint.Pen(piece.Stroke, piece.Runway ? runwayWidth : 1, piece.Dashed) : null, placed);
        }

        bool names = _nmPerPixel < 0.25;
        if (Profile.IsLayerVisible("FIXES"))
        {
            bool fixNames = Profile.IsLayerVisible("FIX NAMES") && names;
            var triangles = new StreamGeometry();
            using (var ctx = triangles.Open())
                foreach (var f in s.Fixes)
                {
                    var p = ToScreen(f.Position);
                    if (!OnScreen(p)) continue;
                    ctx.BeginFigure(new Point(p.X, p.Y - 3.5), false, true);
                    ctx.LineTo(new Point(p.X + 3, p.Y + 2.5), true, false);
                    ctx.LineTo(new Point(p.X - 3, p.Y + 2.5), true, false);
                    if (fixNames) DrawSmallText(dc, f.Name, new Point(p.X + 5, p.Y - 6), theme.Fix);
                }
            triangles.Freeze();
            dc.DrawGeometry(null, Paint.Pen(theme.Fix, 1), triangles);
        }
        if (Profile.IsLayerVisible("VOR"))
        {
            foreach (var v in s.Vors)
            {
                var p = ToScreen(v.Position);
                if (!OnScreen(p)) continue;
                dc.DrawRectangle(null, Paint.Pen(theme.Vor, 1), new Rect(p.X - 3.5, p.Y - 3.5, 7, 7));
                if (names) DrawSmallText(dc, v.Name, new Point(p.X + 6, p.Y - 6), theme.Vor);
            }
        }
        if (Profile.IsLayerVisible("NDB"))
        {
            foreach (var n in s.Ndbs)
            {
                var p = ToScreen(n.Position);
                if (!OnScreen(p)) continue;
                dc.DrawEllipse(null, Paint.Pen(theme.Ndb, 1, dashed: true), p, 4, 4);
                if (names) DrawSmallText(dc, n.Name, new Point(p.X + 6, p.Y - 6), theme.Ndb);
            }
        }
        if (Profile.IsLayerVisible("AIRPORTS"))
        {
            foreach (var a in s.Airports)
            {
                var p = ToScreen(a.Position);
                if (!OnScreen(p)) continue;
                dc.DrawEllipse(null, Paint.Pen(theme.Airport, 1), p, 2.5, 2.5);
                if (_nmPerPixel < 0.6) DrawSmallText(dc, a.Name, new Point(p.X + 5, p.Y + 2), theme.Airport);
            }
        }
        // Labels of an own map layer (plugin maps, stand numbers) follow that layer; the others follow LABELS.
        bool labels = Profile.IsLayerVisible("LABELS");
        foreach (var l in s.Labels)
        {
            bool own = l.Group.Length > 0 && s.CustomLayers.Contains(l.Group);
            if (own ? Profile.IsLayerVisible(l.Group) : labels)
                DrawSmallText(dc, l.Text, ToScreen(l.Position), own ? l.Color ?? s.LayerColors.GetValueOrDefault(l.Group, theme.Label) : MapColor(l.Color, theme.Label));
        }
        if (Profile.IsLayerVisible("FREETEXT"))
            foreach (var l in s.FreeTexts) DrawSmallText(dc, l.Text, ToScreen(l.Position), theme.Label);
    }

    /// <summary>Extended centerlines of the active arrival runways, 15 NM with a tick every 5 NM.</summary>
    private void DrawCenterlines(DrawingContext dc, SectorFile s, Theme theme)
    {
        var pen = Paint.Pen(theme.Centerline, 1, dashed: true);
        foreach (var (airport, use) in Profile.ActiveRunways)
            foreach (var id in use.Arrival)
            {
                var rwy = s.Runways.FirstOrDefault(r => r.Airport.Equals(airport, StringComparison.OrdinalIgnoreCase) &&
                    (r.Id1.Equals(id, StringComparison.OrdinalIgnoreCase) || r.Id2.Equals(id, StringComparison.OrdinalIgnoreCase)));
                if (rwy == null) continue;
                bool first = rwy.Id1.Equals(id, StringComparison.OrdinalIgnoreCase);
                var threshold = first ? rwy.End1 : rwy.End2;
                var other = first ? rwy.End2 : rwy.End1;
                double outbound = GeoMath.BearingDeg(other, threshold);
                var a = ToScreen(threshold);
                dc.DrawLine(pen, a, ToScreen(GeoMath.Offset(threshold, outbound, 15)));
                var tickPen = Paint.Pen(theme.Centerline, 1);
                for (int nm = 5; nm <= 15; nm += 5)
                {
                    var c = GeoMath.Offset(threshold, outbound, nm);
                    dc.DrawLine(tickPen, ToScreen(GeoMath.Offset(c, outbound + 90, 0.5)), ToScreen(GeoMath.Offset(c, outbound - 90, 0.5)));
                }
            }
    }

    /// <summary>Flight plan routes of aircraft with route display on, from the aircraft to the destination.</summary>
    private void DrawRoutes(DrawingContext dc, Theme theme)
    {
        if (Workspace == null) return;
        var pen = Paint.Pen(theme.RouteLine, 1.2, dashed: true);
        foreach (var t in Tracks().Where(t => t.ShowRoute && t.LastUpdate != default))
        {
            var points = Workspace.Procedures.ResolveRoute(t);
            if (points.Count == 0) continue;
            // Skip the points already behind the aircraft.
            int start = 0;
            double best = double.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                double d = GeoMath.DistanceNm(t.Position, points[i].Position);
                if (d < best) (best, start) = (d, i);
            }
            if (start + 1 < points.Count &&
                GeoMath.DistanceNm(t.Position, points[start + 1].Position) < GeoMath.DistanceNm(points[start].Position, points[start + 1].Position))
                start++;
            var prev = ToScreen(t.Position);
            for (int i = start; i < points.Count; i++)
            {
                var p = ToScreen(points[i].Position);
                dc.DrawLine(pen, prev, p);
                dc.DrawEllipse(null, Paint.Pen(theme.RouteLine, 1), p, 2.5, 2.5);
                DrawSmallText(dc, points[i].Name, new Point(p.X + 5, p.Y + 2), theme.RouteLine);
                prev = p;
            }
        }
    }

    /// <summary>Distance/bearing lines; between two aircraft also the closest point of approach.</summary>
    private void DrawMeasures(DrawingContext dc, Theme theme)
    {
        var all = _measures.ToList();
        if (_measureStart != null) all.Add((_measureStart, new Anchor(HitTarget(_mouse), ToGeo(_mouse))));
        foreach (var (a, b) in all)
        {
            Point pa = ToScreen(a.Position), pb = ToScreen(b.Position);
            dc.DrawLine(Paint.Pen(theme.Measure, 1), pa, pb);
            double nm = GeoMath.DistanceNm(a.Position, b.Position);
            string text = $"{nm:0.0} NM  {GeoMath.BearingDeg(a.Position, b.Position):000}°";
            if (a.Track is { } ta && b.Track is { } tb && !ReferenceEquals(ta, tb))
            {
                var (min, minutes) = NetworkAtc.Core.Session.CommandProcessor.ClosestApproach(ta, tb, 20);
                if (minutes > 0.1) text += $"\nmin {min:0.0} NM · {minutes:0} min";
            }
            DrawSmallText(dc, text, new Point((pa.X + pb.X) / 2 + 6, (pa.Y + pb.Y) / 2 - 6), theme.Measure, 11);
        }
    }

    /// <summary>Removes all measuring lines.</summary>
    public void ClearTools()
    {
        _measures.Clear();
        InvalidateVisual();
    }

    private void DrawOverlays(DrawingContext dc)
    {
        if (Plugins == null) return;
        List<RegisteredOverlay> overlays;
        lock (Plugins.Overlays) overlays = Plugins.Overlays.ToList();
        var canvas = new WpfRadarCanvas(dc, ToScreen, _nmPerPixel, UiTypeface, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        foreach (var o in overlays)
        {
            if (!Profile.IsLayerVisible("plugin:" + o.Overlay.Name)) continue;
            try { o.Overlay.Draw(canvas); }
            catch (Exception) { /* a broken overlay must not stop the radar */ }
        }
    }

    private bool PassesFilter(Track t) =>
        (Profile.Targets.ShowGroundTraffic || !t.OnGround) &&
        t.Altitude >= Profile.Targets.FilterFloor && t.Altitude <= Profile.Targets.FilterCeiling;

    private void DrawTraffic(DrawingContext dc, Theme theme)
    {
        _tagBounds.Clear();
        _fieldZones.Clear();
        _targetPoints.Clear();
        var conflicted = new HashSet<string>(Conflicts.SelectMany(c => new[] { c.A.Callsign, c.B.Callsign }), StringComparer.OrdinalIgnoreCase);
        var tracks = Tracks().Where(t => t.LastUpdate != default && PassesFilter(t)).ToList();
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        NeedsAnimation = false;

        // STCA lines under everything else.
        foreach (var c in Conflicts)
        {
            Point a = ToScreen(c.A.Position), b = ToScreen(c.B.Position);
            dc.DrawLine(Paint.Pen(theme.Conflict, 1.2, dashed: c.Predicted), a, b);
            var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            DrawSmallText(dc, $"STCA {c.DistanceNm:0.0} NM", mid, theme.Conflict);
        }

        foreach (var t in tracks.OrderBy(t => t.IsTracked).ThenBy(t => ReferenceEquals(t, Selected)).ThenBy(t => ReferenceEquals(t, Hovered)))
        {
            var p = ToScreen(t.Position);
            if (!OnScreen(p, 200)) continue;
            _targetPoints[t.Callsign] = p;
            bool selected = ReferenceEquals(t, Selected), hovered = ReferenceEquals(t, Hovered);
            // Emergency squawks tint only the callsign; an STCA conflict turns the whole target and tag red.
            bool emergency = t.Squawk is 7500 or 7600 or 7700;
            bool conflict = conflicted.Contains(t.Callsign);
            string symbolColor = conflict ? theme.Conflict
                : t.Highlight ?? (t.OnGround ? theme.TargetOnGround : t.IsTracked ? theme.TargetTracked : theme.Target);

            // History dots, fading out.
            int n = 0;
            var history = t.History.Reverse().Take(Profile.Targets.HistoryDots).ToList();
            foreach (var (_, pos) in history)
            {
                var hp = ToScreen(pos);
                byte alpha = (byte)(200 - 150 * n++ / Math.Max(1, history.Count));
                dc.DrawRectangle(Paint.Brush(Paint.WithAlpha(theme.History, alpha)), null, new Rect(hp.X - 1.5, hp.Y - 1.5, 3, 3));
            }

            // Prediction line.
            if (Profile.Targets.PredictionMinutes > 0 && !t.OnGround && t.GroundSpeed > 30)
                dc.DrawLine(Paint.Pen(theme.PredictionLine, 1), p, ToScreen(t.Predict(Profile.Targets.PredictionMinutes)));

            // Symbol: diamond for untracked, filled square for tracked, dot on the ground.
            double s = Profile.Targets.SymbolSize / 2;
            if (t.OnGround)
            {
                dc.DrawEllipse(Paint.Brush(symbolColor), null, p, s * 0.6, s * 0.6);
            }
            else if (t.IsTracked)
            {
                dc.DrawRoundedRectangle(Paint.Brush(symbolColor), null, new Rect(p.X - s, p.Y - s, 2 * s, 2 * s), 1.5, 1.5);
            }
            else
            {
                var d = new StreamGeometry();
                using (var ctx = d.Open())
                {
                    ctx.BeginFigure(new Point(p.X, p.Y - s - 1), false, true);
                    ctx.LineTo(new Point(p.X + s + 1, p.Y), true, false);
                    ctx.LineTo(new Point(p.X, p.Y + s + 1), true, false);
                    ctx.LineTo(new Point(p.X - s - 1, p.Y), true, false);
                }
                d.Freeze();
                dc.DrawGeometry(null, Paint.Pen(symbolColor, 1.3), d);
            }
            if (t.Ident) dc.DrawEllipse(null, Paint.Pen(symbolColor, 1), p, s + 5, s + 5);
            if (t.HaloNm is { } halo)
            {
                double r = halo / _nmPerPixel;
                dc.DrawEllipse(null, Paint.Pen(theme.Halo, 1, dashed: true), p, r, r);
                DrawSmallText(dc, $"{halo:0.#}", new Point(p.X + r * 0.7 + 2, p.Y - r * 0.7 - 12), theme.Halo, 9.5);
            }
            if (selected) dc.DrawEllipse(null, Paint.Pen(theme.TagSelected, 1), p, s + 8, s + 8);
            if (IsHeard(t.Callsign)) dc.DrawEllipse(null, Paint.Pen(theme.Accent, 2), p, s + 11, s + 11);

            DrawTag(dc, t, p, theme, hovered, conflict, emergency, dip);
        }
    }

    private TrackState StateOf(Track t) =>
        Workspace?.StateOf(t) ?? (t.IsTracked ? TrackState.Assumed : TrackState.NotConcerned);

    private TagTemplate Template(string layout)
    {
        if (!_templates.TryGetValue(layout, out var template))
        {
            if (_templates.Count > 20) _templates.Clear();
            _templates[layout] = template = TagTemplate.Parse(layout);
        }
        return template;
    }

    /// <summary>
    /// Draws a tag the EuroScope way: the tag of the aircraft's state, plus the detailed lines under it
    /// while the mouse is over the aircraft. The first line never moves, so the field under the mouse
    /// stays under the mouse; warnings are written above the tag.
    /// </summary>
    private void DrawTag(DrawingContext dc, Track t, Point target, Theme theme, bool detailed, bool alert, bool emergency, double dip)
    {
        var state = StateOf(t);
        bool correlated = state is TrackState.Assumed or TrackState.TransferFromMe or TrackState.TransferToMe;
        var main = Template(correlated ? Profile.Tags.Tracked : Profile.Tags.Untracked);
        var lines = main.RenderSpans(t, TagFields).ToList();
        if (lines.Count == 0) return;
        int mainCount = lines.Count;
        if (detailed) lines.AddRange(Template(Profile.Tags.Detailed).RenderSpans(t, TagFields));
        string warning = Profile.Tags.ShowWarnings && !main.FieldKeys.Contains("warn") ? TagFields.Resolve("warn", t) ?? "" : "";

        bool blinkOff = false;
        if (state == TrackState.TransferToMe)
        {
            NeedsAnimation = true;
            blinkOff = DateTime.UtcNow.Millisecond >= 500;
        }
        string color = alert ? theme.Conflict
            : ReferenceEquals(t, Selected) ? theme.TagSelected
            : t.Highlight ?? state switch
            {
                TrackState.Assumed => theme.TagTextTracked,
                TrackState.TransferToMe => blinkOff ? theme.TagText : theme.TagTransferToMe,
                TrackState.TransferFromMe => theme.TagTransferFromMe,
                TrackState.Redundant => theme.TagRedundant,
                TrackState.Concerned => theme.TagConcerned,
                _ => theme.TagText,
            };
        var brush = Paint.Brush(color);
        // In a conflict every line of the tag is red, warnings included.
        var warningBrush = Paint.Brush(alert ? theme.Conflict : theme.Warning);
        var emergencyBrush = Paint.Brush(theme.EmergencyCallsign);
        double size = Profile.Tags.FontSize, lineHeight = Math.Round(size * 1.3);

        FormattedText MeasureText(string text, Brush b) => CachedText(text, b, size, _tagTypeface);

        // Measure every span so each field gets its own clickable rectangle.
        Brush SpanBrush(TagSpan span)
        {
            if (span.Field == "warn") return warningBrush;
            if (emergency && !alert && span.Field == "callsign") return emergencyBrush;
            if (span.Field != null && !alert && FieldColor?.Invoke(span.Field, t.Callsign) is { } own) return Paint.Brush(own);
            return brush;
        }
        var measured = lines.Select(line => line.Select(span => (Span: span, Text: MeasureText(span.Text, SpanBrush(span)))).ToList()).ToList();
        double width = measured.Max(line => line.Sum(x => x.Text.WidthIncludingTrailingWhitespace));
        var warningText = warning.Length > 0 ? MeasureText(warning, warningBrush) : null;
        if (warningText != null) width = Math.Max(width, warningText.WidthIncludingTrailingWhitespace);

        var offset = _tagOffsets.TryGetValue(t.Callsign, out var o) ? o : new Vector(Profile.Tags.DefaultOffsetX, Profile.Tags.DefaultOffsetY);
        var origin = target + offset;   // top left of the first line
        double top = origin.Y - (warningText != null ? lineHeight : 0);
        var bounds = new Rect(origin.X - 3, top - 1, width + 6, origin.Y + lineHeight * measured.Count - top + 2);
        _tagBounds.Add((t.Callsign, bounds));

        if (Profile.Tags.ShowTagLeader)
        {
            // The leader ends at the middle of the first line, on the side facing the aircraft.
            double lineY = origin.Y + lineHeight / 2;
            var end = target.X <= bounds.Left ? new Point(bounds.Left, lineY)
                : target.X >= bounds.Right ? new Point(bounds.Right, lineY)
                : new Point(Math.Clamp(target.X, bounds.Left, bounds.Right), target.Y < bounds.Top ? bounds.Top : bounds.Bottom);
            var dir = end - target;
            if (dir.Length > Profile.Targets.SymbolSize + 2)
            {
                dir.Normalize();
                dc.DrawLine(Paint.Pen(Paint.WithAlpha(color, 0x90), 1), target + dir * (Profile.Targets.SymbolSize / 2 + 3), end);
            }
        }
        var bg = Paint.ToColor(theme.TagBackground);
        if (bg.A > 0)
            dc.DrawRectangle(Paint.Brush(theme.TagBackground), null, bounds);
        else if (detailed && measured.Count > mainCount)
        {
            // Only the added lines get a panel, so they stay readable over the map.
            var extra = new Rect(bounds.Left, origin.Y + lineHeight * mainCount, bounds.Width, lineHeight * (measured.Count - mainCount) + 1);
            dc.DrawRectangle(Paint.Brush(Paint.WithAlpha(theme.Panel, 0xD8)), Paint.Pen(Paint.WithAlpha(color, 0x50), 1), extra);
        }

        if (warningText != null) dc.DrawText(warningText, new Point(origin.X, top + (lineHeight - size * 1.2) / 2));
        double y = origin.Y;
        foreach (var line in measured)
        {
            double x = origin.X;
            foreach (var (span, text) in line)
            {
                double w = text.WidthIncludingTrailingWhitespace;
                if (span.Field != null && span.Text.Trim().Length > 0)
                {
                    // The clickable area is the text itself, without the spaces around it.
                    int lead = span.Text.Length - span.Text.TrimStart().Length;
                    double leadWidth = lead > 0 ? MeasureText(span.Text[..lead], brush).WidthIncludingTrailingWhitespace : 0;
                    double textWidth = MeasureText(span.Text.Trim(), brush).WidthIncludingTrailingWhitespace;
                    var zone = new Rect(x + leadWidth - 1.5, y, textWidth + 3, lineHeight);
                    _fieldZones.Add((zone, t.Callsign, span.Field));
                    if (_hoveredField is { } h && h.Callsign.Equals(t.Callsign, StringComparison.OrdinalIgnoreCase) && h.Field == span.Field)
                        dc.DrawRectangle(null, Paint.Pen(color, 1), zone);
                }
                dc.DrawText(text, new Point(x, y + (lineHeight - size * 1.2) / 2));
                x += w;
            }
            y += lineHeight;
        }
    }

    /// <summary>
    /// Laid-out texts by (text, brush, size, typeface): map labels and tag lines repeat from frame to frame, and laying
    /// out text is the most expensive part of drawing them.
    /// </summary>
    private readonly Dictionary<(string Text, Brush Brush, double Size, Typeface Face), FormattedText> _textCache = [];
    private double _textDip;

    private FormattedText CachedText(string text, Brush brush, double size, Typeface face)
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dip != _textDip || _textCache.Count > 6000)
        {
            _textCache.Clear();
            _textDip = dip;
        }
        var key = (text, brush, size, face);
        if (!_textCache.TryGetValue(key, out var ft))
            _textCache[key] = ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dip);
        return ft;
    }

    private void DrawSmallText(DrawingContext dc, string text, Point at, string color, double size = 10)
    {
        if (!OnScreen(at)) return;
        dc.DrawText(CachedText(text, Paint.Brush(color), size, UiTypeface), at);
    }

    private void DrawScaleBar(DrawingContext dc, Theme theme)
    {
        // A "nice" length of about 100 px.
        double nm = 100 * _nmPerPixel;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(nm)));
        double nice = new[] { 1, 2, 5, 10 }.Select(k => k * pow).First(v => v >= nm * 0.6);
        double px = nice / _nmPerPixel;
        var y = ActualHeight - 16;
        var pen = Paint.Pen(theme.MutedText, 1);
        dc.DrawLine(pen, new Point(16, y), new Point(16 + px, y));
        dc.DrawLine(pen, new Point(16, y - 4), new Point(16, y));
        dc.DrawLine(pen, new Point(16 + px, y - 4), new Point(16 + px, y));
        DrawSmallText(dc, nice >= 1 ? $"{nice:0} NM" : $"{nice:0.##} NM", new Point(20 + px, y - 7), theme.MutedText);
    }

    // ---- input ---------------------------------------------------------------------------------

    /// <summary>The topmost plugin object under the point (the last one added is on top).</summary>
    private EsScreenObject? HitObject(Point p)
    {
        if (EsDrawing is not { } d) return null;
        for (int i = d.Objects.Count - 1; i >= 0; i--)
        {
            var a = d.Objects[i].Area;
            if (p.X >= a.Left && p.X < a.Right && p.Y >= a.Top && p.Y < a.Bottom) return d.Objects[i];
        }
        return null;
    }

    private bool PressObject(Point p, int button, int clickCount)
    {
        if (HitObject(p) is not { } o) return false;
        _objectPress = (o, p, button, false);
        ScreenObjectMouse?.Invoke(o, 1, p, button);
        if (clickCount == 2) ScreenObjectMouse?.Invoke(o, 4, p, button);
        CaptureMouse();
        return true;
    }

    private bool ReleaseObject(Point p, int button)
    {
        if (_objectPress is not { } press || press.Button != button) return false;
        _objectPress = null;
        ReleaseMouseCapture();
        ScreenObjectMouse?.Invoke(press.Object, 2, p, button);
        if (press.Moved) ScreenObjectMouse?.Invoke(press.Object, 5, p, 1);
        else ScreenObjectMouse?.Invoke(press.Object, 3, p, button);
        return true;
    }

    /// <summary>The topmost tag under the point.</summary>
    private string? HitTag(Point p)
    {
        for (int i = _tagBounds.Count - 1; i >= 0; i--)
            if (_tagBounds[i].Bounds.Contains(p)) return _tagBounds[i].Callsign;
        return null;
    }

    /// <summary>The field under the point, only in the topmost tag there.</summary>
    private (string Callsign, string Field)? HitField(Point p)
    {
        if (HitTag(p) is not { } tag) return null;
        foreach (var z in _fieldZones)
            if (z.Bounds.Contains(p) && z.Callsign.Equals(tag, StringComparison.OrdinalIgnoreCase)) return (z.Callsign, z.Field);
        return null;
    }

    private Track? FindTrack(string callsign) =>
        Tracks().FirstOrDefault(t => t.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));

    private Track? HitTarget(Point p, double radius = 9)
    {
        string? best = null;
        double bestDist = radius;
        foreach (var (cs, tp) in _targetPoints)
        {
            double d = (tp - p).Length;
            if (d < bestDist)
            {
                best = cs;
                bestDist = d;
            }
        }
        best ??= HitTag(p);
        return best == null ? null : Tracks().FirstOrDefault(t => t.Callsign.Equals(best, StringComparison.OrdinalIgnoreCase));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1 / 1.2 : 1.2);
        e.Handled = true;
    }

    private void ZoomAt(Point screen, double factor)
    {
        var (gx, gy) = _projection.ToPlane(ToGeo(screen));
        _nmPerPixel = Math.Clamp(_nmPerPixel * factor, MinNmPerPixel, MaxNmPerPixel);
        // Keep the point under the cursor fixed.
        _cx = gx - (screen.X - ActualWidth / 2) * _nmPerPixel;
        _cy = gy - (ActualHeight / 2 - screen.Y) * _nmPerPixel;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (PressObject(p, 1, e.ClickCount)) return;
        var tag = HitTag(p);
        if (tag != null)
        {
            _pendingTagClick = (tag, HitField(p)?.Field, p);
            _dragStart = p;
            _dragOrigin = _tagOffsets.TryGetValue(tag, out var o) ? o : new Vector(Profile.Tags.DefaultOffsetX, Profile.Tags.DefaultOffsetY);
            CaptureMouse();
            return;
        }
        var target = HitTarget(p);
        if (target != null)
        {
            Select(target);
            return;
        }
        _panStart = p;
        _panOrigin = (_cx, _cy);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        if (_objectPress is { } press)
        {
            if (press.Object.Moveable && ((_mouse - press.Start).Length > 2 || press.Moved))
            {
                _objectPress = press with { Moved = true };
                ScreenObjectMouse?.Invoke(press.Object, 5, _mouse, 0);
            }
            return;
        }
        var over = HitObject(_mouse);
        if (over != null && (over.ObjectType, over.ObjectId) != (_overObject?.ObjectType, _overObject?.ObjectId))
            ScreenObjectMouse?.Invoke(over, 0, _mouse, 0);
        _overObject = over;
        if (_measureStart != null)
        {
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_pendingTagClick is { } pending && (_mouse - pending.Start).Length > 3)
        {
            _draggingTag = pending.Callsign;
            _pendingTagClick = null;
        }
        if (_draggingTag != null)
        {
            _tagOffsets[_draggingTag] = _dragOrigin + (_mouse - _dragStart);
            InvalidateVisual();
            return;
        }
        if (_panStart is { } start)
        {
            var d = _mouse - start;
            if (d.Length > 2) Select(null);
            _cx = _panOrigin.X - d.X * _nmPerPixel;
            _cy = _panOrigin.Y + d.Y * _nmPerPixel;
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var hovered = HitTarget(_mouse);
        var field = HitField(_mouse);
        if (!ReferenceEquals(hovered, Hovered) || field != _hoveredField)
        {
            Hovered = hovered;
            _hoveredField = field;
            Cursor = field != null || hovered != null ? Cursors.Hand : null;
            InvalidateVisual();
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (ReleaseObject(e.GetPosition(this), 1)) return;
        if (_pendingTagClick is { } click && FindTrack(click.Callsign) is { } clicked)
            TagClicked?.Invoke(this, new TagClickEventArgs(clicked, click.Field, false, e.GetPosition(this)));
        _pendingTagClick = null;
        if (_panStart is { } start && (e.GetPosition(this) - start).Length <= 2) Select(null);
        _panStart = null;
        _draggingTag = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        _rightDown = p;
        if (PressObject(p, 3, e.ClickCount))
        {
            e.Handled = true;
            return;
        }
        if (HitTag(p) == null)
        {
            // Right-drag measures distance and bearing; from a target the line follows the aircraft.
            _measureStart = new Anchor(HitTarget(p), ToGeo(p));
            CaptureMouse();
        }
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (ReleaseObject(p, 3))
        {
            e.Handled = true;
            return;
        }
        if (_measureStart is { } start)
        {
            _measureStart = null;
            ReleaseMouseCapture();
            if ((p - _rightDown).Length > 6)
            {
                _measures.Add((start, new Anchor(HitTarget(p), ToGeo(p))));
                if (_measures.Count > 8) _measures.RemoveAt(0);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            InvalidateVisual();
        }
        if (HitTag(p) is { } tag && FindTrack(tag) is { } tagged)
        {
            TagClicked?.Invoke(this, new TagClickEventArgs(tagged, HitField(p)?.Field, true, p));
            e.Handled = true;
            return;
        }
        var target = HitTarget(p);
        if (target == null) return;
        Select(target);
        TargetMenuRequested?.Invoke(this, target);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && PressObject(e.GetPosition(this), 2, e.ClickCount))
        {
            e.Handled = true;
            return;
        }
        // Middle button resets a dragged tag to the default position.
        if (e.ChangedButton == MouseButton.Middle && HitTag(e.GetPosition(this)) is { } tag)
        {
            _tagOffsets.Remove(tag);
            InvalidateVisual();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ReleaseObject(e.GetPosition(this), 2)) e.Handled = true;
        base.OnMouseUp(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        Hovered = null;
        _hoveredField = null;
        InvalidateVisual();
    }
}

public sealed class TagClickEventArgs(Track track, string? field, bool right, Point position) : EventArgs
{
    public Track Track { get; } = track;
    /// <summary>Template field under the mouse, or null (e.g. a literal part of the tag).</summary>
    public string? Field { get; } = field;
    public bool Right { get; } = right;
    /// <summary>Click position relative to the radar view.</summary>
    public Point Position { get; } = position;
}

/// <summary>A picture of the EuroScope plugins' drawing, split around the tags, and the objects they made clickable.</summary>
public sealed record PluginDrawing(ImageSource? Back, ImageSource? Front, IReadOnlyList<EsScreenObject> Objects);
