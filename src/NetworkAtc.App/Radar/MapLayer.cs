using System.Windows;
using System.Windows.Media;

namespace NetworkAtc.App.Radar;

/// <summary>
/// The sector map under the radar: drawn once into a cached picture (<see cref="BitmapCache"/>) and, while the view
/// is being panned or zoomed, only moved and scaled as that picture on the graphics card. The radar redraws it crisply
/// when the view has settled or the map itself changes; traffic updates never touch it.
/// </summary>
public sealed class MapLayer : FrameworkElement
{
    private readonly DrawingVisual _visual = new() { CacheMode = new BitmapCache { EnableClearType = false, SnapsToDevicePixels = false } };
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

    /// <summary>Moves and scales the last picture to follow the view until it is redrawn.</summary>
    public void Follow(Matrix fromDrawnToNow)
    {
        var t = new MatrixTransform(fromDrawnToNow);
        t.Freeze();
        _visual.Transform = t;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    protected override void OnRender(DrawingContext dc) =>
        dc.DrawRectangle(_background, null, new Rect(0, 0, ActualWidth, ActualHeight));
}
