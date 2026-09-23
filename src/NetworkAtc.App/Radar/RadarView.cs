using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetworkAtc.App.Services;
using NetworkAtc.Core.Customization;
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
    private static readonly Typeface UiTypeface = new("Segoe UI");
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
    private readonly Dictionary<string, Rect> _tagBounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Rect Bounds, string Callsign, string Field)> _fieldZones = [];
    private (string Callsign, string Field)? _hoveredField;
    private (string Callsign, string? Field, Point Start)? _pendingTagClick;
    private readonly Dictionary<string, Point> _targetPoints = new(StringComparer.OrdinalIgnoreCase);
    private Typeface _tagTypeface = new("Consolas");
    private readonly Dictionary<string, TagTemplate> _templates = [];

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
    public Track? Selected { get; private set; }
    public Track? Hovered { get; private set; }

    public event EventHandler<Track?>? SelectionChanged;
    public event EventHandler? ViewChanged;
    /// <summary>Right click on a target symbol; the window builds the context menu.</summary>
    public event EventHandler<Track>? TargetMenuRequested;
    /// <summary>Click on a tag (Field is null when the click was not on a field).</summary>
    public event EventHandler<TagClickEventArgs>? TagClicked;

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

    private bool OnScreen(Point p, double margin = 50) =>
        p.X > -margin && p.Y > -margin && p.X < ActualWidth + margin && p.Y < ActualHeight + margin;

    // ---- rendering -------------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var theme = Profile.Theme;
        dc.DrawRectangle(Paint.Brush(theme.RadarBackground), null, new Rect(0, 0, ActualWidth, ActualHeight));
        _tagTypeface = new Typeface(Profile.Tags.FontFamily.Split(',')[0].Trim());
        if (Profile.IsLayerVisible("RANGE RINGS")) DrawRangeRings(dc, theme);
        if (Sector != null) DrawSector(dc, Sector, theme);
        DrawOverlays(dc);
        DrawTraffic(dc, theme);
        DrawScaleBar(dc, theme);
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

    private void DrawSector(DrawingContext dc, SectorFile s, Theme theme)
    {
        if (Profile.IsLayerVisible("REGIONS"))
        {
            foreach (var region in s.Regions)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(ToScreen(region.Points[0]), true, true);
                    for (int i = 1; i < region.Points.Count; i++) ctx.LineTo(ToScreen(region.Points[i]), false, false);
                }
                geo.Freeze();
                dc.DrawGeometry(Paint.Brush(MapColor(region.Color, theme.Region)), null, geo);
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
            foreach (var l in lines)
            {
                Point a = ToScreen(l.From), b = ToScreen(l.To);
                if (!OnScreen(a, 4000) && !OnScreen(b, 4000)) continue;
                dc.DrawLine(Paint.Pen(MapColor(l.Color, color), 1, dashed), a, b);
            }
        }

        if (Profile.IsLayerVisible("SECTORLINES"))
        {
            var pen = Paint.Pen(theme.SectorLine, 1, dashed: true);
            foreach (var line in s.SectorLines.Values)
                for (int i = 1; i < line.Count; i++) dc.DrawLine(pen, ToScreen(line[i - 1]), ToScreen(line[i]));
        }

        if (Profile.IsLayerVisible("RUNWAYS"))
        {
            double width = Math.Clamp(0.03 / _nmPerPixel, 1.5, 6);
            foreach (var r in s.Runways) dc.DrawLine(Paint.Pen(theme.Runway, width), ToScreen(r.End1), ToScreen(r.End2));
        }

        bool names = _nmPerPixel < 0.25;
        if (Profile.IsLayerVisible("FIXES"))
        {
            var pen = Paint.Pen(theme.Fix, 1);
            bool fixNames = Profile.IsLayerVisible("FIX NAMES") && names;
            foreach (var f in s.Fixes)
            {
                var p = ToScreen(f.Position);
                if (!OnScreen(p)) continue;
                var tri = new StreamGeometry();
                using (var ctx = tri.Open())
                {
                    ctx.BeginFigure(new Point(p.X, p.Y - 3.5), false, true);
                    ctx.LineTo(new Point(p.X + 3, p.Y + 2.5), true, false);
                    ctx.LineTo(new Point(p.X - 3, p.Y + 2.5), true, false);
                }
                tri.Freeze();
                dc.DrawGeometry(null, pen, tri);
                if (fixNames) DrawSmallText(dc, f.Name, new Point(p.X + 5, p.Y - 6), theme.Fix);
            }
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
        if (Profile.IsLayerVisible("LABELS"))
            foreach (var l in s.Labels) DrawSmallText(dc, l.Text, ToScreen(l.Position), MapColor(l.Color, theme.Label));
        if (Profile.IsLayerVisible("FREETEXT"))
            foreach (var l in s.FreeTexts) DrawSmallText(dc, l.Text, ToScreen(l.Position), theme.Label);
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

        // STCA lines under everything else.
        foreach (var c in Conflicts)
        {
            Point a = ToScreen(c.A.Position), b = ToScreen(c.B.Position);
            dc.DrawLine(Paint.Pen(theme.Conflict, 1.2, dashed: c.Predicted), a, b);
            var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            DrawSmallText(dc, $"STCA {c.DistanceNm:0.0} NM", mid, theme.Conflict);
        }

        foreach (var t in tracks.OrderBy(t => t.IsTracked).ThenBy(t => ReferenceEquals(t, Selected)))
        {
            var p = ToScreen(t.Position);
            if (!OnScreen(p, 200)) continue;
            _targetPoints[t.Callsign] = p;
            bool selected = ReferenceEquals(t, Selected), hovered = ReferenceEquals(t, Hovered);
            bool emergency = t.Squawk is 7500 or 7600 or 7700;
            string symbolColor = emergency ? theme.Emergency
                : conflicted.Contains(t.Callsign) ? theme.Conflict
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
            if (selected) dc.DrawEllipse(null, Paint.Pen(theme.TagSelected, 1), p, s + 8, s + 8);

            DrawTag(dc, t, p, theme, selected || hovered, emergency || conflicted.Contains(t.Callsign), dip);
        }
    }

    private void DrawTag(DrawingContext dc, Track t, Point target, Theme theme, bool detailed, bool alert, double dip)
    {
        string layout = detailed ? Profile.Tags.Detailed : t.IsTracked ? Profile.Tags.Tracked : Profile.Tags.Untracked;
        if (!_templates.TryGetValue(layout, out var template))
        {
            if (_templates.Count > 20) _templates.Clear();
            _templates[layout] = template = TagTemplate.Parse(layout);
        }
        var lines = template.RenderSpans(t, TagFields);
        if (lines.Count == 0) return;
        string color = alert ? theme.Conflict
            : ReferenceEquals(t, Selected) ? theme.TagSelected
            : t.Highlight ?? (t.IsTracked ? theme.TagTextTracked : theme.TagText);
        var brush = Paint.Brush(color);
        double size = Profile.Tags.FontSize, lineHeight = size * 1.3;

        // Measure every span so each field gets its own clickable rectangle.
        var measured = lines.Select(line => line.Select(span => (Span: span,
            Text: new FormattedText(span.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _tagTypeface, size, brush, dip))).ToList()).ToList();
        double width = measured.Max(line => line.Sum(x => x.Text.WidthIncludingTrailingWhitespace));
        double height = lineHeight * measured.Count;

        var offset = _tagOffsets.TryGetValue(t.Callsign, out var o) ? o : new Vector(Profile.Tags.DefaultOffsetX, Profile.Tags.DefaultOffsetY);
        var origin = target + offset;
        var bounds = new Rect(origin.X - 4, origin.Y - 2, width + 8, height + 4);
        _tagBounds[t.Callsign] = bounds;

        if (Profile.Tags.ShowTagLeader)
        {
            // Leader from the symbol to the nearest edge of the tag.
            var edge = new Point(Math.Clamp(target.X, bounds.Left, bounds.Right), Math.Clamp(target.Y, bounds.Top, bounds.Bottom));
            var dir = edge - target;
            if (dir.Length > Profile.Targets.SymbolSize)
            {
                dir.Normalize();
                dc.DrawLine(Paint.Pen(Paint.WithAlpha(color, 0x70), 1), target + dir * (Profile.Targets.SymbolSize / 2 + 3), edge);
            }
        }
        var bg = Paint.ToColor(theme.TagBackground);
        if (bg.A > 0 || detailed)
            dc.DrawRoundedRectangle(Paint.Brush(bg.A > 0 ? theme.TagBackground : Paint.WithAlpha(theme.Panel, 0xD8)), null, bounds, 3, 3);

        double y = origin.Y;
        foreach (var line in measured)
        {
            double x = origin.X;
            foreach (var (span, text) in line)
            {
                double w = text.WidthIncludingTrailingWhitespace;
                if (span.Field != null && span.Text.Trim().Length > 0)
                {
                    var zone = new Rect(x - 1, y, w + 2, lineHeight);
                    _fieldZones.Add((zone, t.Callsign, span.Field));
                    if (_hoveredField is { } h && h.Callsign.Equals(t.Callsign, StringComparison.OrdinalIgnoreCase) && h.Field == span.Field)
                        dc.DrawRoundedRectangle(Paint.Brush(Paint.WithAlpha(theme.Accent, 0x38)), null, zone, 2, 2);
                }
                dc.DrawText(text, new Point(x, y + (lineHeight - size * 1.2) / 2));
                x += w;
            }
            y += lineHeight;
        }
    }

    private void DrawSmallText(DrawingContext dc, string text, Point at, string color, double size = 10)
    {
        if (!OnScreen(at)) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, size,
            Paint.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, at);
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

    private string? HitTag(Point p) => _tagBounds.FirstOrDefault(kv => kv.Value.Contains(p)).Key;

    private (string Callsign, string Field)? HitField(Point p)
    {
        foreach (var z in _fieldZones)
            if (z.Bounds.Contains(p)) return (z.Callsign, z.Field);
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
        if (_pendingTagClick is { } click && FindTrack(click.Callsign) is { } clicked)
            TagClicked?.Invoke(this, new TagClickEventArgs(clicked, click.Field, false, e.GetPosition(this)));
        _pendingTagClick = null;
        if (_panStart is { } start && (e.GetPosition(this) - start).Length <= 2) Select(null);
        _panStart = null;
        _draggingTag = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
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
        // Middle button resets a dragged tag to the default position.
        if (e.ChangedButton == MouseButton.Middle && HitTag(e.GetPosition(this)) is { } tag)
        {
            _tagOffsets.Remove(tag);
            InvalidateVisual();
        }
        base.OnMouseDown(e);
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
