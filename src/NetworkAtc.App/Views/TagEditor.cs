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
/// The EuroScope-style popup opened next to a tag field: a title bar, an optional text box and a list of
/// values. Typing goes to the text box, the arrow keys and the wheel move through the list; Enter applies
/// the typed value or, when nothing is typed, the highlighted item. A click on an item applies it at once;
/// Esc or a click elsewhere closes the popup.
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
            HorizontalOffset = at.X + 10,
            VerticalOffset = at.Y - 10,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        var mono = (System.Windows.Media.FontFamily)anchor.FindResource("MonoFont");

        void Done(string value)
        {
            popup.IsOpen = false;
            apply(value);
        }

        var panel = new DockPanel { MinWidth = 128, LastChildFill = true };

        // Title bar, like the EuroScope popup lists.
        var close = new Button { Content = "✕", Style = (Style)anchor.FindResource("Ghost"), Padding = new Thickness(5, 0, 5, 0), FontSize = 10 };
        close.Click += (_, _) => popup.IsOpen = false;
        var header = new DockPanel { Background = (System.Windows.Media.Brush)anchor.FindResource("HoverBrush") };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(7, 3, 4, 3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        TextBox? box = null;
        ListBox? list = null;
        if (manualText != null)
        {
            box = new TextBox
            {
                Text = manualText,
                FontFamily = mono,
                ToolTip = manualHint,
                Margin = new Thickness(4, 4, 4, 2),
                Padding = new Thickness(4, 1, 4, 1),
                // Values (levels, codes) are typed in capitals; free text such as a note keeps its case.
                CharacterCasing = items.Count > 0 ? CharacterCasing.Upper : CharacterCasing.Normal,
            };
            DockPanel.SetDock(box, Dock.Top);
            panel.Children.Add(box);
        }

        if (items.Count > 0)
        {
            list = new ListBox
            {
                ItemsSource = items,
                MaxHeight = 250,
                FontFamily = mono,
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 2, 0, 2),
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
            // Letters and digits typed while the list has the focus go to the text box.
            list.PreviewTextInput += (_, e) =>
            {
                if (box == null) return;
                box.Focus();
                box.Text += e.Text;
                box.CaretIndex = box.Text.Length;
                e.Handled = true;
            };
            list.Loaded += (_, _) =>
            {
                if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
            };
            panel.Children.Add(list);
        }

        if (box != null)
        {
            box.PreviewKeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case Key.Enter:
                        if (box.Text.Trim().Length > 0 || list?.SelectedItem is not EditorItem selected) Done(box.Text.Trim());
                        else Done(selected.Value);
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        popup.IsOpen = false;
                        e.Handled = true;
                        break;
                    case Key.Up or Key.Down when list != null:
                        list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Up ? -1 : 1), 0, items.Count - 1);
                        list.ScrollIntoView(list.SelectedItem);
                        e.Handled = true;
                        break;
                }
            };
            // Typing highlights the first matching value in the list.
            box.TextChanged += (_, _) =>
            {
                if (list == null || box.Text.Trim().Length == 0) return;
                string typed = box.Text.Trim();
                int match = items.ToList().FindIndex(i => i.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                                                        || i.Text.TrimStart('F', 'A', 'H', 'S').StartsWith(typed, StringComparison.OrdinalIgnoreCase));
                if (match < 0) return;
                list.SelectedIndex = match;
                list.ScrollIntoView(list.SelectedItem);
            };
        }

        popup.Child = new Border
        {
            Background = (System.Windows.Media.Brush)anchor.FindResource("PanelBrush"),
            BorderBrush = (System.Windows.Media.Brush)anchor.FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
        popup.Opened += (_, _) =>
        {
            if (box != null)
            {
                box.Focus();
                box.SelectAll();
            }
            else list?.Focus();
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
