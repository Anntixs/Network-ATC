using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NetworkAtc.App.Controls;

/// <summary>
/// A EuroScope-style list window floating over the radar: drag it by the title bar, resize it by
/// the corner, close it with ✕. It lives on a Canvas; position and size are stored in the profile.
/// </summary>
public class FloatingPanel : HeaderedContentControl
{
    private static int _topZ = 10;

    /// <summary>Key of this window in <c>Profile.Windows</c>.</summary>
    public string WindowId { get; set; } = "";

    /// <summary>Moved, resized or closed; the window should save the layout.</summary>
    public event EventHandler? LayoutChanged;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Title") is Thumb title)
        {
            title.DragDelta += (_, e) =>
            {
                var (maxX, maxY) = Bounds();
                Canvas.SetLeft(this, Math.Clamp(Left + e.HorizontalChange, 0, Math.Max(0, maxX - 60)));
                Canvas.SetTop(this, Math.Clamp(Top + e.VerticalChange, 0, Math.Max(0, maxY - 24)));
            };
            title.DragCompleted += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        if (GetTemplateChild("PART_Resize") is Thumb resize)
        {
            resize.DragDelta += (_, e) =>
            {
                Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange);
                Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange);
            };
            resize.DragCompleted += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        if (GetTemplateChild("PART_Close") is Button close)
        {
            close.Click += (_, _) =>
            {
                Visibility = Visibility.Collapsed;
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            };
        }
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        BringToFront();
        base.OnPreviewMouseDown(e);
    }

    public void BringToFront() => Panel.SetZIndex(this, ++_topZ);

    public double Left => double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
    public double Top => double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);

    private (double, double) Bounds() =>
        Parent is FrameworkElement p ? (p.ActualWidth, p.ActualHeight) : (double.MaxValue, double.MaxValue);

    /// <summary>Place the window; negative coordinates count from the right / bottom edge.</summary>
    public void Place(double x, double y, double width, double height, double canvasWidth, double canvasHeight)
    {
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
        if (x < 0) x = canvasWidth + x;
        if (y < 0) y = canvasHeight + y;
        Canvas.SetLeft(this, Math.Clamp(x, 0, Math.Max(0, canvasWidth - 80)));
        Canvas.SetTop(this, Math.Clamp(y, 0, Math.Max(0, canvasHeight - 30)));
    }
}
