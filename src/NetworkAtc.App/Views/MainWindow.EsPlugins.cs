using System.IO;
using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetworkAtc.App.Controls;
using NetworkAtc.App.Radar;
using NetworkAtc.App.Services;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.EsPlugins;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Tags;
using NetworkAtc.Plugins;
using Track = NetworkAtc.Core.Radar.Track;

namespace NetworkAtc.App.Views;

/// <summary>
/// EuroScope plugins in the main window: the plugin host is fed every second, plugin tag items become tag
/// fields, their functions run from tag clicks, their popups open as tag editors, their drawing goes on the
/// radar and their flight plan lists float over it like our own lists.
/// </summary>
public partial class MainWindow
{
    private const string EsFunctionPrefix = "esfn:";

    /// <summary>EuroScope's own tag items (TAG_ITEM_TYPE_…) by the Network-ATC field that shows the same.</summary>
    private static readonly Dictionary<int, string> EsBuiltInItems = new()
    {
        [2] = "squawk", [4] = "alt", [9] = "callsign", [10] = "wtc", [11] = "comm", [12] = "vs", [13] = "gs", [14] = "ho",
        [15] = "owner", [16] = "type", [17] = "dest", [19] = "scratch", [20] = "cfl", [22] = "rfl", [23] = "aspd", [25] = "ahdg",
        [35] = "gs", [37] = "dest", [38] = "type", [40] = "gs", [41] = "gs", [42] = "warn", [43] = "cfl", [44] = "aspd", [46] = "ahdg",
        [47] = "rwy", [55] = "star", [56] = "sid", [58] = "clr", [59] = "gstate", [60] = "asq", [61] = "dep", [63] = "rules",
    };

    /// <summary>EuroScope's own tag functions (TAG_ITEM_FUNCTION_…) by the Network-ATC tag action doing the same.</summary>
    private static readonly Dictionary<int, string> EsBuiltInFunctions = new()
    {
        [1] = TagActions.Route, [7] = TagActions.FlightPlan, [8] = TagActions.Handoff, [9] = TagActions.Handoff, [11] = TagActions.ClearedLevel,
        [12] = TagActions.Speed, [14] = TagActions.Heading, [17] = TagActions.Procedure, [18] = TagActions.Procedure, [19] = TagActions.Runway,
        [20] = TagActions.Handoff, [27] = TagActions.Clearance, [28] = TagActions.AircraftMenu, [29] = TagActions.Scratchpad,
        [31] = TagActions.Squawk, [46] = TagActions.AircraftMenu,
    };

    private EsBridge? _es;
    private readonly HashSet<string> _esFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, FloatingPanel> _esListPanels = [];
    private readonly Dictionary<int, bool> _esListVisible = [];
    private string? _esOpenDisplay;
    private bool _esGeometryDirty = true;
    private uint _esKeyPixel;

    private static string EsSettingsFile => Path.Combine(Profile.DefaultDirectory, "es-plugins.txt");

    /// <summary>Starts the plugin host when it is installed, then loads the plugins of the profile.</summary>
    private async void StartEsPlugins()
    {
        string? exe = EsHost.FindHostExe(AppContext.BaseDirectory);
        if (exe == null)
        {
            if (_profile.EsPlugins.Count > 0) Error("Плагины EuroScope не запущены: нет папки esbridge рядом с программой");
            return;
        }
        var es = new EsBridge(_session, _workspace, () => _profile);
        es.Changed += () => Dispatcher.BeginInvoke(OnEsChanged);
        es.Log += (text, error) => Dispatcher.BeginInvoke(() => { if (error) Error(text); else Info(text); });
        es.UserMessage += m => Dispatcher.BeginInvoke(() => OnEsUserMessage(m));
        es.Popup += p => Dispatcher.BeginInvoke(() => ShowEsPopup(p));
        es.ViewDrawn += OnEsViewDrawn;
        es.RefreshRequested += _ => Dispatcher.BeginInvoke(() => _es?.RefreshView(EsBridge.MainView));
        es.BuiltInTagFunction += r => Dispatcher.BeginInvoke(() => OnEsBuiltInFunction(r));
        es.DisplayAreaRequested += (_, sw, ne) => Dispatcher.BeginInvoke(() => ShowArea(sw, ne));
        es.AliasAdded += (name, value) => Dispatcher.BeginInvoke(() => _profile.Aliases[name.StartsWith('.') ? name : "." + name] = value);
        es.ListChanged += l => Dispatcher.BeginInvoke(() => RenderEsList(l));
        es.ViewData += (_, name, _, value) => Dispatcher.BeginInvoke(() => _profile.EsDisplayData[name] = value);
        es.TagValuesChanged += () => _dirty = true;
        es.AselRequested += cs => Dispatcher.BeginInvoke(() =>
        {
            if (_session.Tracks.FirstOrDefault(t => t.Callsign.Equals(cs, StringComparison.OrdinalIgnoreCase)) is { } t) Radar.Select(t);
        });
        try
        {
            await es.StartAsync(exe, EsSettingsFile);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Error("Плагины EuroScope не запущены: " + ex.Message);
            await es.DisposeAsync();
            return;
        }
        _es = es;
        Radar.FieldColor = EsFieldColor;
        Radar.ScreenObjectMouse += (o, kind, p, button) => _es?.ScreenObjectEvent(EsBridge.MainView, o, kind, (int)p.X, (int)p.Y, button);
        es.SendSector(Radar.Sector, _sectorPath ?? "");
        OpenEsView();
        foreach (var path in _profile.EsPlugins.ToList())
        {
            if (File.Exists(path)) es.LoadPlugin(path);
            else Error($"Плагин EuroScope не найден: {path}");
        }
    }

    // ---- plugins, fields, display ------------------------------------------------------------------

    private void OnEsChanged()
    {
        if (_es == null) return;
        foreach (var key in _esFields) _tagFields.Remove(key);
        _esFields.Clear();
        foreach (var item in _es.TagItems)
        {
            var it = item;
            _tagFields.Add(it.FieldKey, $"{it.PluginName}: {it.Name}", a => _es?.Value(a.Callsign, it.PluginId, it.Code)?.Text ?? "");
            _esFields.Add(it.FieldKey);
        }
        foreach (var id in _esListPanels.Keys.Where(id => _es.Lists.All(l => l.Id != id)).ToList())
        {
            WindowsLayer.Children.Remove(_esListPanels[id]);
            _esListPanels.Remove(id);
        }
        UpdateEsItemsInUse();
        OpenEsView();
        _dirty = true;
    }

    /// <summary>Tells the host which plugin items the tags and lists show, so only those are computed.</summary>
    private void UpdateEsItemsInUse()
    {
        if (_es == null) return;
        var keys = new[] { _profile.Tags.Untracked, _profile.Tags.Tracked, _profile.Tags.Detailed }
            .SelectMany(l => TagTemplate.Parse(l).FieldKeys)
            .Concat(_es.Lists.SelectMany(l => l.Columns.Where(c => c.ItemPlugin.Length > 0).Select(c => EsBridge.FieldKey(c.ItemPlugin, c.ItemCode))))
            .Where(k => k.StartsWith("es:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        _es.SetTagItemsInUse(keys);
    }

    /// <summary>Opens the radar screen in the host with the display type of the profile (when its plugin is loaded).</summary>
    private void OpenEsView()
    {
        if (_es is not { IsRunning: true }) return;
        var type = _es.DisplayTypes.FirstOrDefault(d => d.Name.Equals(_profile.EsDisplayType, StringComparison.OrdinalIgnoreCase));
        string wanted = type?.Name ?? EsBridge.StandardDisplay;
        if (_esOpenDisplay == wanted) return;
        if (_esOpenDisplay != null) _es.CloseView(EsBridge.MainView);
        _es.OpenView(EsBridge.MainView, wanted, _profile.EsDisplayData);
        _esOpenDisplay = wanted;
        Radar.HideOwnContent = type is { NeedRadarContent: false };
        Radar.EsDrawing = null;
        _esGeometryDirty = true;
    }

    private void SetEsDisplay(string name)
    {
        if (_es != null && _esOpenDisplay != null) _es.SaveView(EsBridge.MainView);
        _profile.EsDisplayType = name;
        OpenEsView();
        _dirty = true;
    }

    /// <summary>Called by the frame loop: the world every second, the screen when the view moved and every second.</summary>
    private void PumpEsPlugins()
    {
        if (_es is not { IsRunning: true } || _esOpenDisplay == null) return;
        bool second = _frameCount % 4 == 0;
        if (second) _es.SyncWorld();
        if (_esGeometryDirty)
        {
            _esGeometryDirty = false;
            int w = (int)Math.Max(1, Radar.ActualWidth), h = (int)Math.Max(1, Radar.ActualHeight);
            var bg = Paint.ToColor(_profile.Theme.RadarBackground);
            uint colorRef = bg.R | (uint)bg.G << 8 | (uint)bg.B << 16;
            _esKeyPixel = (uint)bg.R << 16 | (uint)bg.G << 8 | bg.B;
            var (center, cx, cy) = Radar.Geometry;
            _es.ViewGeometry(EsBridge.MainView, w, h, center, cx, cy, Radar.NmPerPixel, colorRef,
                new EsRect(0, 0, w, h), new EsRect(0, 0, w, 0), new EsRect(0, h, w, h));
            _es.RefreshView(EsBridge.MainView);
        }
        else if (second) _es.RefreshView(EsBridge.MainView);
        if (_frameCount % 120 == 0) _es.SaveView(EsBridge.MainView);
    }

    /// <summary>The host drew the screen into shared memory: the key color becomes transparent. Runs off the UI thread.</summary>
    private void OnEsViewDrawn(EsViewDrawn d)
    {
        if (d.ViewId != EsBridge.MainView) return;
        uint key = _esKeyPixel;
        var drawing = new PluginDrawing(ReadEsLayer(d.BackMapping, d.Width, d.Height, key), ReadEsLayer(d.FrontMapping, d.Width, d.Height, key), d.Objects);
        Dispatcher.BeginInvoke(() =>
        {
            Radar.EsDrawing = drawing;
            Radar.InvalidateVisual();
        });
    }

    private static BitmapSource? ReadEsLayer(string name, int width, int height, uint key)
    {
        if (name.Length == 0 || width <= 0 || height <= 0) return null;
        try
        {
            using var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, (long)width * height * 4, MemoryMappedFileAccess.Read);
            var pixels = new uint[width * height];
            view.ReadArray(0, pixels, 0, pixels.Length);
            bool any = false;
            for (int i = 0; i < pixels.Length; i++)
            {
                uint c = pixels[i] & 0x00FFFFFF;
                if (c == key) pixels[i] = 0;
                else
                {
                    pixels[i] = c | 0xFF000000;
                    any = true;
                }
            }
            if (!any) return null;
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A plugin asked for an area of the map (south-west and north-east corners).</summary>
    private void ShowArea(GeoPoint sw, GeoPoint ne)
    {
        var center = new GeoPoint((sw.Latitude + ne.Latitude) / 2, (sw.Longitude + ne.Longitude) / 2);
        double latNm = Math.Abs(ne.Latitude - sw.Latitude) * 60;
        double lonNm = Math.Abs(ne.Longitude - sw.Longitude) * 60 * Math.Cos(center.Latitude * Math.PI / 180);
        double nmPerPixel = Math.Max(latNm / Math.Max(1, Radar.ActualHeight), lonNm / Math.Max(1, Radar.ActualWidth));
        Radar.SetView(center, nmPerPixel > 0 ? nmPerPixel : Radar.NmPerPixel);
    }

    /// <summary>Tag color chosen by the plugin for its item.</summary>
    private string? EsFieldColor(string field, string callsign)
    {
        if (_es == null || EsBridge.ParseKey(field, "es:") is not { } key || _es.Value(callsign, key.Plugin, key.Code) is not { } v) return null;
        var theme = _profile.Theme;
        return v.ColorCode switch
        {
            1 => $"#{v.Rgb & 0xFF:X2}{(v.Rgb >> 8) & 0xFF:X2}{(v.Rgb >> 16) & 0xFF:X2}",
            2 => theme.TagText,
            3 => theme.TagConcerned,
            4 => theme.TagTextTracked,
            5 or 9 => theme.TagTransferToMe,
            6 => theme.TagRedundant,
            7 => theme.Accent,
            8 => theme.TagTransferFromMe,
            10 => theme.Success,
            11 => theme.Danger,
            12 => theme.Emergency,
            _ => null,
        };
    }

    // ---- functions and popups -------------------------------------------------------------------------

    /// <summary>"esfn:Plugin:code" on an aircraft; the item text is what the clicked field shows.</summary>
    private void CallEsFunction(string action, Track t, string? field, Point at)
    {
        if (EsBridge.ParseKey(action, EsFunctionPrefix) is not { } fn) return;
        if (fn.Plugin.Length == 0)
        {
            if (EsBuiltInFunctions.TryGetValue(fn.Code, out var own)) RunTagAction(t, own, at, false, field);
            return;
        }
        if (_es == null) return;
        string text = field != null ? _tagFields.Resolve(field, t) ?? "" : "";
        int x = (int)at.X, y = (int)at.Y;
        _es.CallFunction(fn.Plugin, fn.Code, t.Callsign, text, x, y, new EsRect(x - 20, y - 8, x + 20, y + 8));
    }

    /// <summary>A plugin started one of EuroScope's own functions (CFL list, handoff menu…): ours does the same.</summary>
    private void OnEsBuiltInFunction(EsTagFunctionRequest r)
    {
        var t = _session.Tracks.FirstOrDefault(x => x.Callsign.Equals(r.Callsign, StringComparison.OrdinalIgnoreCase)) ?? Radar.Selected;
        if (t == null || !EsBuiltInFunctions.TryGetValue(r.FunctionId, out var action)) return;
        Radar.Select(t);
        RunTagAction(t, action, new Point(r.X, r.Y), false, null);
    }

    private void ShowEsPopup(EsPopup p)
    {
        var at = new Point(p.Area.Left, p.Area.Bottom);
        int x = (int)at.X, y = (int)at.Y;
        if (p.IsEdit)
        {
            TagEditor.Show(Radar, at, p.Title.Length > 0 ? p.Title : "Ввод", [], -1, p.Initial, "Enter — ввод",
                v => _es?.SelectPopup(p, p.FunctionId, v, x, y));
            return;
        }
        var items = p.Elements.Select((e, i) =>
        {
            string box = e.Checked switch { 0 => "☐ ", 1 => "☑ ", _ => "" };
            string text = box + e.Text + (e.Text2.Length > 0 ? "  " + e.Text2 : "");
            return new EditorItem(e.Disabled ? $"({text})" : text, i.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Selected);
        }).ToList();
        int selected = p.Elements.ToList().FindIndex(e => e.Selected);
        TagEditor.Show(Radar, at, p.Title, items, selected, null, null, v =>
        {
            if (!int.TryParse(v, out int i) || i < 0 || i >= p.Elements.Count || p.Elements[i].Disabled) return;
            _es?.SelectPopup(p, p.Elements[i].FunctionId, p.Elements[i].Text, x, y);
        });
    }

    private void OnEsUserMessage(EsUserMessage m)
    {
        string chat = m.Handler.Length > 0 ? m.Handler : "Радио";
        AddLine(chat, m.Sender.Length > 0 ? m.Sender : chat, m.Text, m.Flash ? _profile.Theme.Warning : _profile.Theme.Text);
        if (m.NeedConfirmation || m.Flash) OpenChat(chat);
    }

    /// <summary>Plugin functions offered in the settings as tag click actions.</summary>
    private IEnumerable<(string Id, string Title)> EsFunctionActions() =>
        _es?.TagFunctions.Select(f => (EsFunctionPrefix + f.PluginName + ":" + f.Code, $"{f.PluginName}: {f.Name}")) ?? [];

    // ---- flight plan lists --------------------------------------------------------------------------------

    private void RenderEsList(EsFpList list)
    {
        if (_es == null) return;
        if (!_esListPanels.TryGetValue(list.Id, out var panel))
        {
            panel = new FloatingPanel { WindowId = "es:" + list.Name, Header = list.Name, MinWidth = 160, MinHeight = 80 };
            panel.LayoutChanged += (_, _) => SaveWindowLayout(panel);
            WindowsLayer.Children.Add(panel);
            _esListPanels[list.Id] = panel;
            var layout = _profile.Windows.GetValueOrDefault(panel.WindowId)
                         ?? new WindowLayout(80 + 30 * _esListPanels.Count, 90 + 30 * _esListPanels.Count, Math.Max(220, list.Columns.Sum(c => c.Width * 8 + 12)), 220, list.Visible);
            panel.Place(layout.X, layout.Y, layout.Width, layout.Height, WindowsLayer.ActualWidth, WindowsLayer.ActualHeight);
            panel.Visibility = layout.Visible ? Visibility.Visible : Visibility.Collapsed;
            _esListVisible[list.Id] = list.Visible;
        }
        // The plugin shows or hides its list; otherwise the user decides.
        if (_esListVisible.GetValueOrDefault(list.Id) != list.Visible)
        {
            _esListVisible[list.Id] = list.Visible;
            panel.Visibility = list.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        panel.Header = $"{list.Name} · {list.Callsigns.Count}";
        if (panel.Visibility != Visibility.Visible) return;

        var grid = new Grid { Margin = new Thickness(6, 2, 6, 4) };
        for (int c = 0; c < list.Columns.Count; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = Math.Max(24, list.Columns[c].Width * 8) });
        var mono = (FontFamily)FindResource("MonoFont");
        void Cell(int row, int col, string text, Brush? brush, bool header, EsFpListColumn column, Track? track)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = mono,
                Margin = new Thickness(0, 0, 10, 1),
                TextAlignment = column.Centered ? TextAlignment.Center : TextAlignment.Left,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
            };
            if (brush != null) tb.Foreground = brush;
            else if (header) tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            if (track != null)
            {
                tb.Cursor = Cursors.Hand;
                tb.MouseLeftButtonUp += (_, e) => EsListClick(track, column, false, e.GetPosition(Radar));
                tb.MouseRightButtonUp += (_, e) => { EsListClick(track, column, true, e.GetPosition(Radar)); e.Handled = true; };
            }
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < list.Columns.Count; c++) Cell(0, c, list.Columns[c].Title, null, true, list.Columns[c], null);
        int r = 1;
        foreach (var cs in list.Callsigns)
        {
            var track = _session.Tracks.FirstOrDefault(t => t.Callsign.Equals(cs, StringComparison.OrdinalIgnoreCase));
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < list.Columns.Count; c++)
            {
                var col = list.Columns[c];
                string text;
                Brush? brush = null;
                if (col.ItemPlugin.Length > 0)
                {
                    text = _es.Value(cs, col.ItemPlugin, col.ItemCode)?.Text ?? "";
                    if (EsFieldColor(EsBridge.FieldKey(col.ItemPlugin, col.ItemCode), cs) is { } color) brush = Paint.Brush(color);
                }
                else text = track != null && EsBuiltInItems.TryGetValue(col.ItemCode, out var f) ? _tagFields.Resolve(f, track) ?? "" : col.ItemCode == 9 ? cs : "";
                Cell(r, c, text, brush, false, col, track);
            }
            r++;
        }
        panel.Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void EsListClick(Track t, EsFpListColumn column, bool right, Point at)
    {
        string plugin = right ? column.RightPlugin : column.LeftPlugin;
        int function = right ? column.RightFunction : column.LeftFunction;
        Radar.Select(t);
        if (function == 0) return;
        string? field = column.ItemPlugin.Length > 0 ? EsBridge.FieldKey(column.ItemPlugin, column.ItemCode) : EsBuiltInItems.GetValueOrDefault(column.ItemCode);
        CallEsFunction(EsFunctionPrefix + plugin + ":" + function, t, field, at);
    }

    // ---- ПЛАГИНЫ menu ---------------------------------------------------------------------------------------

    private void AddEsPluginMenu(ContextMenu menu)
    {
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Плагины EuroScope", IsEnabled = false });
        if (_es == null)
            menu.Items.Add(new MenuItem { Header = "    не запущены (нет папки esbridge)", IsEnabled = false });
        foreach (var p in _es?.Plugins ?? [])
        {
            var item = new MenuItem { Header = $"    {p.Name}  {p.Version}" + (p.Author.Length > 0 ? $" · {p.Author}" : "") };
            var id = p.Id;
            var path = p.Path;
            item.Items.Add(MenuEntry("Выгрузить", () =>
            {
                _es?.UnloadPlugin(id);
                _profile.EsPlugins.RemoveAll(x => x.Equals(path, StringComparison.OrdinalIgnoreCase));
                SaveProfile();
            }));
            menu.Items.Add(item);
        }
        if (_es != null)
        {
            menu.Items.Add(MenuEntry("Загрузить плагин EuroScope (.dll)…", () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Плагин EuroScope", Filter = "Плагин EuroScope (*.dll)|*.dll" };
                if (dialog.ShowDialog(this) != true) return;
                if (!_profile.EsPlugins.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase)) _profile.EsPlugins.Add(dialog.FileName);
                SaveProfile();
                _es?.LoadPlugin(dialog.FileName);
            }));
            var display = new MenuItem { Header = "Тип экрана" };
            var standard = new MenuItem { Header = "Стандартный радар", IsCheckable = true, IsChecked = _profile.EsDisplayType.Length == 0 };
            standard.Click += (_, _) => SetEsDisplay("");
            display.Items.Add(standard);
            foreach (var d in _es.DisplayTypes.Where(d => d.CanBeCreated))
            {
                var name = d.Name;
                var item = new MenuItem { Header = $"{d.Name} · {d.PluginName}", IsCheckable = true, IsChecked = _profile.EsDisplayType.Equals(name, StringComparison.OrdinalIgnoreCase) };
                item.Click += (_, _) => SetEsDisplay(name);
                display.Items.Add(item);
            }
            menu.Items.Add(display);
            var lists = _es.Lists.ToList();
            if (lists.Count > 0)
            {
                var sub = new MenuItem { Header = "Списки плагинов" };
                foreach (var l in lists)
                {
                    var id = l.Id;
                    sub.Items.Add(MenuEntry(l.Name, () =>
                    {
                        if (!_esListPanels.TryGetValue(id, out var panel)) return;
                        panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                        if (panel.Visibility == Visibility.Visible && _es?.Lists.FirstOrDefault(x => x.Id == id) is { } current) RenderEsList(current);
                        SaveWindowLayout(panel);
                    }));
                }
                menu.Items.Add(sub);
            }
        }
    }

    /// <summary>Plugins of a just imported profile that are not running yet.</summary>
    private void LoadImportedEsPlugins()
    {
        if (_es == null) return;
        var running = _es.Plugins.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _profile.EsPlugins.Where(p => !running.Contains(p) && File.Exists(p))) _es.LoadPlugin(path);
        UpdateEsItemsInUse();
        OpenEsView();
    }

    /// <summary>Radio and private messages go to the plugins too (OnCompileFrequencyChat / OnCompilePrivateChat).</summary>
    private void ForwardChatToEsPlugins(AtcMessage m)
    {
        if (_es == null || m.Outgoing || m.IsError) return;
        if (m.Channel == MessageChannel.Private) _es.Chat(true, m.From, _session.LocalCallsign, 0, m.Text);
        else if (m.Channel == MessageChannel.Radio) _es.Chat(false, m.From, "", (m.FrequencyKhz ?? 0) / 1000.0, m.Text);
    }

    private void StopEsPlugins()
    {
        if (_es == null) return;
        _es.SaveView(EsBridge.MainView);
        var es = _es;
        _es = null;
        Task.Run(async () => await es.DisposeAsync()).Wait(TimeSpan.FromSeconds(3));
    }
}
