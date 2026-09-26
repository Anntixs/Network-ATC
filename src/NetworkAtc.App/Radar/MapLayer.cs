using System.Windows;
using System.Windows.Media;

namespace NetworkAtc.App.Radar;

/// <summary>
/// The sector map under the radar, kept as vector drawing (always sharp). Traffic updates never touch it; while the
/// view is dragged it is only shifted, and the radar redraws it when the zoom or the map itself changes.
/// </summary>
public sealed class MapLayer : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    private Brush _background = Brushes.Black;

    public MapLayer()
    {
        IsHitTestVisible = false;
        AddVisualChild(_visual);
    }

    public Brush Background
    {
        get => _background;
        set
        {
            if (ReferenceEquals(_background, value)) return;
            _background = value;
            InvalidateVisual();
        }
    }

    /// <summary>Starts a new picture of the map, in the screen coordinates of the current view.</summary>
    public DrawingContext Open()
    {
        _visual.Transform = null;
        return _visual.RenderOpen();
    }

    /// <summary>Shifts the last drawing to follow a dragged view until it is redrawn.</summary>
    public void Follow(double dx, double dy)
    {
        var t = new TranslateTransform(dx, dy);
        t.Freeze();
        _visual.Transform = t;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    protected override void OnRender(DrawingContext dc) =>
        dc.DrawRectangle(_background, null, new Rect(0, 0, ActualWidth, ActualHeight));
}
