using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NetworkAtc.App.Views;

public sealed record EditorItem(string Text, string Value, bool Accent = false)
{
    public override string ToString() => Text;
}

/// <summary>
/// Small popup opened next to a tag field: an optional text box for typing a value and an
/// optional list of choices. Enter in the text box or a click in the list applies the value.
/// </summary>
public static class TagEditor
{
    public static void Show(FrameworkElement anchor, Point at, string title, IReadOnlyList<EditorItem> items, int selectedIndex,
        string? manualText, string? manualHint, Action<string> apply)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Relative,
            HorizontalOffset = at.X + 8,
            VerticalOffset = at.Y - 12,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };

        var panel = new StackPanel { MinWidth = 150 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)anchor.FindResource("SectionTitle"),
            Margin = new Thickness(4, 0, 0, 6),
        });

        void Done(string value)
        {
            popup.IsOpen = false;
            apply(value);
        }

        TextBox? box = null;
        if (manualText != null)
        {
            box = new TextBox
            {
                Text = manualText,
                FontFamily = (System.Windows.Media.FontFamily)anchor.FindResource("MonoFont"),
                ToolTip = manualHint,
                Margin = new Thickness(0, 0, 0, items.Count > 0 ? 6 : 0),
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) Done(box.Text.Trim());
                else if (e.Key == Key.Escape) popup.IsOpen = false;
            };
            panel.Children.Add(box);
        }

        if (items.Count > 0)
        {
            var list = new ListBox
            {
                ItemsSource = items,
                MaxHeight = 260,
                FontFamily = (System.Windows.Media.FontFamily)anchor.FindResource("MonoFont"),
                SelectedIndex = Math.Clamp(selectedIndex, -1, items.Count - 1),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            list.ItemTemplate = BuildItemTemplate();
            list.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListBoxItem { Content: EditorItem item })
                    Done(item.Value);
            };
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && list.SelectedItem is EditorItem item) Done(item.Value);
                else if (e.Key == Key.Escape) popup.IsOpen = false;
            };
            list.Loaded += (_, _) =>
            {
                if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
            };
            panel.Children.Add(list);
        }

        popup.Child = new Border
        {
            Style = (Style)anchor.FindResource("Card"),
            Padding = new Thickness(8),
            Child = panel,
        };
        popup.Opened += (_, _) =>
        {
            if (box != null)
            {
                box.Focus();
                box.SelectAll();
            }
        };
        popup.IsOpen = true;
    }

    private static DataTemplate BuildItemTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(EditorItem.Text)));
        var style = new Style(typeof(TextBlock));
        var accent = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(EditorItem.Accent)), Value = true };
        accent.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentBrush")));
        style.Triggers.Add(accent);
        text.SetValue(FrameworkElement.StyleProperty, style);
        return new DataTemplate { VisualTree = text };
    }
}
