using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetworkAtc.App.Services;
using NetworkAtc.Plugins;

namespace NetworkAtc.App.Radar;

/// <summary><see cref="IRadarCanvas"/> for plugin overlays, drawing into the radar's DrawingContext.</summary>
internal sealed class WpfRadarCanvas(DrawingContext dc, Func<GeoPoint, Point> toScreen, double nmPerPixel, Typeface typeface, double dip)
    : IRadarCanvas
{
    public double NmPerPixel => nmPerPixel;

    public void Line(GeoPoint from, GeoPoint to, string color, double width = 1) =>
        dc.DrawLine(Paint.Pen(color, width), toScreen(from), toScreen(to));

    public void Polyline(IReadOnlyList<GeoPoint> points, string color, double width = 1, bool closed = false)
    {
        if (points.Count < 2) return;
        dc.DrawGeometry(null, Paint.Pen(color, width), Build(points, closed));
    }

    public void Polygon(IReadOnlyList<GeoPoint> points, string fill, string? stroke = null)
    {
        if (points.Count < 3) return;
        dc.DrawGeometry(Paint.Brush(fill), stroke == null ? null : Paint.Pen(stroke), Build(points, true));
    }

    public void Circle(GeoPoint center, double radiusNm, string color, double width = 1)
    {
        double r = radiusNm / nmPerPixel;
        dc.DrawEllipse(null, Paint.Pen(color, width), toScreen(center), r, r);
    }

    public void Text(GeoPoint at, string text, string color, double size = 11) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Paint.Brush(color), dip), toScreen(at));

    private StreamGeometry Build(IReadOnlyList<GeoPoint> points, bool closed)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(toScreen(points[0]), closed, closed);
            for (int i = 1; i < points.Count; i++) ctx.LineTo(toScreen(points[i]), true, false);
        }
        g.Freeze();
        return g;
    }
}
