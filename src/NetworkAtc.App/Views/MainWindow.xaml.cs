using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NetworkAtc.App.Services;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Plugins;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Tags;
using NetworkAtc.Plugins;
using Track = NetworkAtc.Core.Radar.Track;

namespace NetworkAtc.App.Views;

public sealed record ChatLine(string Time, string From, string Text, Brush Brush);

public sealed record TrafficRow(string Callsign, string Level, string Info, Track Track);

public sealed record AtcRow(string Callsign, string Frequency);

public sealed class LayerItem(string key, string title, bool visible) : INotifyPropertyChanged
{
    private bool _visible = visible;
    public string Key { get; } = key;
    public string Title { get; } = title;
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private static readonly (string Key, string Title)[] MapLayers =
    [
        ("ARTCC", "Границы (ARTCC)"), ("ARTCC HIGH", "Границы верхние"), ("ARTCC LOW", "Границы нижние"),
        ("SECTORLINES", "Линии секторов (.ese)"), ("SID", "SID"), ("STAR", "STAR"), ("LOW AIRWAY", "Нижние трассы"),
        ("HIGH AIRWAY", "Верхние трассы"), ("GEO", "Геометрия (GEO)"), ("REGIONS", "Регионы"), ("RUNWAYS", "ВПП"),
        ("AIRPORTS", "Аэродромы"), ("VOR", "VOR"), ("NDB", "NDB"), ("FIXES", "Точки"), ("FIX NAMES", "Имена точек"),
        ("LABELS", "Подписи"), ("FREETEXT", "Текст (.ese)"), ("RANGE RINGS", "Кольца дальности"),
    ];

    private readonly string _profilePath = Path.Combine(Profile.DefaultDirectory, "profiles", "default.json");
    private readonly DpapiProtector _protector = new();
    private readonly AtcSession _session = new();
    private readonly TagFields _tagFields = new();
    private readonly PluginRegistry _registry = new();
    private readonly PluginManager _plugins;
    private readonly CommandProcessor _commands;
    private readonly Stca _stca = new();
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, ObservableCollection<ChatLine>> _chats = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _history = [];
    private Profile _profile;
    private string _activeChat = "Радио";
    private int _historyIndex;
    private bool _dirty = true;
    private int _frameCount;

    public MainWindow()
    {
        InitializeComponent();
        _profile = Profile.Load(_profilePath);
        ThemeApplier.Apply(_profile.Theme);

        _plugins = new PluginManager(_session, _tagFields, _registry, () => Radar.Selected, Path.Combine(Profile.DefaultDirectory, "plugin-data"));
        _commands = new CommandProcessor(_session, () => _profile, _registry, () => Radar.Selected);

        Radar.Profile = _profile;
        Radar.TagFields = _tagFields;
        Radar.Plugins = _registry;
        Radar.Tracks = () => _session.Tracks;
        Radar.SelectionChanged += (_, t) => OnSelectionChanged(t);
        Radar.ViewChanged += (_, _) => UpdateViewInfo();
        Radar.TargetMenuRequested += (_, t) => ShowTargetMenu(t);

        _session.TrackUpdated += (_, _) => _dirty = true;
        _session.TrackRemoved += (_, cs) => Dispatcher.BeginInvoke(() =>
        {
            if (Radar.Selected?.Callsign == cs) Radar.Select(null);
            _dirty = true;
        });
        _session.FlightPlanUpdated += (_, t) => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(t, Radar.Selected)) ShowPlan(t); });
        _session.MessageReceived += (_, m) => Dispatcher.BeginInvoke(() => OnMessage(m));
        _session.ControllersChanged += (_, _) => Dispatcher.BeginInvoke(RefreshControllers);
        _session.ConnectionChanged += (_, c) => Dispatcher.BeginInvoke(() => UpdateConnectionState(c));
        _registry.Log += (_, text) => Dispatcher.BeginInvoke(() => AddLine("Радио", "плагин", text, _profile.Theme.MutedText));
        _registry.Changed += (_, _) => Dispatcher.BeginInvoke(BuildLayerList);
        _commands.CenterRequested += (_, p) => Radar.CenterOn(p);
        _commands.SelectRequested += (_, t) => Radar.Select(t);
        _commands.Changed += (_, _) => _dirty = true;

        OpenChat("Радио");
        ApplyProfile();
        LoadSector(_profile.SectorFile);
        LoadPlugins();
        BuildLayerList();
        UpdateConnectionState(false);

        _frame.Tick += (_, _) => OnFrame();
        _frame.Start();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Info("Добро пожаловать в Network-ATC. Команды: .help · .demo — демо-трафик · масштаб колесом мыши · теги перетаскиваются мышью");
    }

    // ---- setup ---------------------------------------------------------------------------------

    private void ApplyProfile()
    {
        ThemeApplier.Apply(_profile.Theme);
        Radar.Profile = _profile;
        _tagFields.TransitionAltitude = _profile.TransitionAltitude;
        _stca.HorizontalNm = _profile.StcaHorizontalNm;
        _stca.VerticalFeet = _profile.StcaVerticalFeet;
        FontSize = _profile.Panels.UiFontSize;
        Root.LayoutTransform = Math.Abs(_profile.Panels.UiScale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(_profile.Panels.UiScale, _profile.Panels.UiScale);
        SideToggle.IsChecked = _profile.Panels.ShowAircraftList;
        MessagesToggle.IsChecked = _profile.Panels.ShowMessages;
        ApplyPanels();
        _dirty = true;
    }

    private void ApplyPanels()
    {
        _profile.Panels.ShowAircraftList = SideToggle.IsChecked == true;
        _profile.Panels.ShowMessages = MessagesToggle.IsChecked == true;
        SideColumn.Width = _profile.Panels.ShowAircraftList ? new GridLength(_profile.Panels.SidePanelWidth) : new GridLength(0);
        MessagesRow.Height = _profile.Panels.ShowMessages ? new GridLength(_profile.Panels.MessagesHeight) : new GridLength(0);
    }

    private void LoadSector(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            // First start: show the bundled demo sector.
            path = Path.Combine(AppContext.BaseDirectory, "demo", "UUEE-demo.sct");
            if (!File.Exists(path))
            {
                Radar.SetSector(null);
                return;
            }
        }
        try
        {
            var sector = SectorParser.LoadFiles(path);
            Radar.SetSector(sector);
            _commands.DemoCenter = sector.Center;
            if (_profile.SectorFile == path && _profile.ViewCenterLatitude != 0)
                Radar.SetView(new GeoPoint(_profile.ViewCenterLatitude, _profile.ViewCenterLongitude), _profile.ViewNmPerPixel);
            SectorText.Text = sector.Name;
            if (sector.Warnings.Count > 0)
                Info($"Сектор загружен с предупреждениями ({sector.Warnings.Count}): {string.Join("; ", sector.Warnings.Take(3))}");
        }
        catch (IOException e)
        {
            Error("Не удалось открыть сектор: " + e.Message);
        }
    }

    private void LoadPlugins()
    {
        _plugins.LoadFrom(Path.Combine(Profile.DefaultDirectory, "plugins"));
        _plugins.LoadFrom(Path.Combine(AppContext.BaseDirectory, "plugins"));
        foreach (var p in _plugins.Plugins) Info($"Плагин: {p.Plugin.Name} {p.Plugin.Version}");
        foreach (var e in _plugins.Errors) Error($"Плагин {Path.GetFileName(e.File)}: {e.Reason}");
    }

    private void BuildLayerList()
    {
        var items = MapLayers.Select(l => new LayerItem(l.Key, l.Title, _profile.IsLayerVisible(l.Key))).ToList();
        lock (_registry.Overlays)
            items.AddRange(_registry.Overlays.Select(o => new LayerItem("plugin:" + o.Overlay.Name, o.Overlay.Name + " · " + o.Owner,
                _profile.IsLayerVisible("plugin:" + o.Overlay.Name))));
        LayerList.ItemsSource = items;
    }

    // ---- frame loop --------------------------------------------------------------------------------

    private void OnFrame()
    {
        _frameCount++;
        if (_dirty)
        {
            _dirty = false;
            Radar.Conflicts = _profile.StcaEnabled ? _stca.Check(_session.Tracks) : [];
            Radar.InvalidateVisual();
        }
        if (_frameCount % 4 == 0)
        {
            ClockText.Text = DateTime.UtcNow.ToString("HH:mm:ss") + "Z";
            RefreshTraffic();
            if (Radar.Selected is { } t && PlanTab.IsChecked == true) ShowPlanSummary(t);
        }
    }

    private void UpdateViewInfo()
    {
        RangeText.Text = $"{Radar.RangeNm:0} NM";
        var g = Radar.MouseGeo;
        CursorText.Text = $"{Dms(g.Latitude, 'N', 'S')}  {Dms(g.Longitude, 'E', 'W')}";
    }

    private static string Dms(double value, char pos, char neg)
    {
        char h = value >= 0 ? pos : neg;
        value = Math.Abs(value);
        int d = (int)value, m = (int)((value - d) * 60);
        double s = ((value - d) * 60 - m) * 60;
        return $"{h}{d:000}°{m:00}'{s:00}\"";
    }

    private void RefreshTraffic()
    {
        var selected = Radar.Selected;
        var rows = _session.Tracks.Where(t => t.LastUpdate != default).OrderBy(t => t.Callsign)
            .Select(t => new TrafficRow(t.Callsign, _tagFields.FlightLevel(t.Altitude),
                $"{t.AircraftType} {(t.Destination.Length > 0 ? "→ " + t.Destination : "")}".Trim(), t)).ToList();
        if (rows.Select(r => r.Callsign + r.Level + r.Info).SequenceEqual(
                (TrafficList.ItemsSource as IEnumerable<TrafficRow> ?? []).Select(r => r.Callsign + r.Level + r.Info)))
            return;
        TrafficList.SelectionChanged -= OnTrafficSelected;
        TrafficList.ItemsSource = rows;
        TrafficList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Track, selected));
        TrafficList.SelectionChanged += OnTrafficSelected;
    }

    private void RefreshControllers() =>
        AtcList.ItemsSource = _session.Controllers.Select(c => new AtcRow(c.Callsign, Frequency.Format(c.FrequencyKhz))).ToList();

    // ---- selection and flight plan panel ---------------------------------------------------------

    private void OnTrafficSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TrafficList.SelectedItem is TrafficRow row) Radar.Select(row.Track);
    }

    private void OnSelectionChanged(Track? t)
    {
        if (t == null)
        {
            PlanEmpty.Visibility = Visibility.Visible;
            PlanContent.Visibility = Visibility.Collapsed;
            return;
        }
        ShowPlan(t);
        if (_profile.Panels.ShowFlightPlan && SideToggle.IsChecked == true && TrafficTab.IsChecked != true) PlanTab.IsChecked = true;
    }

    private void ShowPlan(Track t)
    {
        PlanEmpty.Visibility = Visibility.Collapsed;
        PlanContent.Visibility = Visibility.Visible;
        PlanCallsign.Text = t.Callsign;
        CflBox.Text = t.ClearedAltitude is { } c ? _tagFields.FlightLevel(c) : "";
        HdgBox.Text = t.AssignedHeading?.ToString("000") ?? "";
        SpdBox.Text = t.AssignedSpeed?.ToString() ?? "";
        SqBox.Text = t.AssignedSquawk?.ToString("0000") ?? "";
        ScratchBox.Text = t.Scratchpad;
        TrackToggle.IsChecked = t.IsTracked;
        TrackToggle.Content = t.IsTracked ? "Отпустить" : "Сопровождать";
        PlanRouteText.Text = t.Route.Length > 0 ? t.Route : "—";
        PlanRemarks.Text = t.Remarks.Length > 0 ? t.Remarks : "—";
        ShowPlanSummary(t);
    }

    private void ShowPlanSummary(Track t)
    {
        PlanSubtitle.Text = $"{(t.AircraftType.Length > 0 ? t.AircraftType : "тип ?")}/{t.WakeCategory}   код {t.Squawk:0000}{(t.ModeC ? "" : " STBY")}";
        PlanRoute.Text = t.HasFlightPlan ? $"{t.Departure}  →  {t.Destination}" : "Нет плана полёта";
        PlanDetails.Text = t.HasFlightPlan
            ? $"Правила {t.Rules}   ·   RFL {t.FiledAltitude}   ·   TAS {t.FiledSpeed}\nЗапасной {(t.Alternate.Length > 0 ? t.Alternate : "—")}\n" +
              $"Сейчас {_tagFields.FlightLevel(t.Altitude)}  {t.GroundSpeed} уз  курс {t.Heading:000}  {t.VerticalSpeed:+0;-0;0} фт/мин"
            : $"Сейчас {_tagFields.FlightLevel(t.Altitude)}  {t.GroundSpeed} уз  курс {t.Heading:000}";
    }

    private async void OnAnnotationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || Radar.Selected is not { } t) return;
        var feedback = await _commands.ExecuteAsync($".{box.Tag} {box.Text.Trim()}");
        if (feedback != null && !feedback.StartsWith(t.Callsign, StringComparison.Ordinal)) Error(feedback);
        ShowPlan(t);
        Radar.Focus();
    }

    private async void OnAssignSquawk(object sender, RoutedEventArgs e)
    {
        if (Radar.Selected is not { } t) return;
        var feedback = await _commands.ExecuteAsync(".sq");
        if (feedback != null) Info(feedback);
        ShowPlan(t);
    }

    private void OnTrackToggle(object sender, RoutedEventArgs e)
    {
        if (Radar.Selected is not { } t) return;
        t.IsTracked = !t.IsTracked;
        ShowPlan(t);
        _dirty = true;
    }

    private void OnSideTabChanged(object sender, RoutedEventArgs e)
    {
        if (TrafficList == null) return;
        TrafficList.Visibility = TrafficTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PlanPanel.Visibility = PlanTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AtcList.Visibility = AtcTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTargetMenu(Track t)
    {
        var menu = new ContextMenu();
        void Add(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => { action(); _dirty = true; if (ReferenceEquals(Radar.Selected, t)) ShowPlan(t); };
            menu.Items.Add(item);
        }
        Add(t.IsTracked ? "Отпустить" : "Взять на сопровождение", () => t.IsTracked = !t.IsTracked);
        Add("Выдать код ответчика", () => OnAssignSquawk(this, new RoutedEventArgs()));
        Add("Запросить план полёта", () => _ = _session.RequestFlightPlanAsync(t.Callsign));
        Add("Личное сообщение…", () =>
        {
            CommandLine.Text = $".msg {t.Callsign} ";
            CommandLine.Focus();
            CommandLine.CaretIndex = CommandLine.Text.Length;
        });
        List<AircraftAction> actions;
        lock (_registry.AircraftActions) actions = _registry.AircraftActions.ToList();
        if (actions.Count > 0) menu.Items.Add(new Separator());
        foreach (var a in actions)
            Add(a.Title, () =>
            {
                try { a.Action(t); }
                catch (Exception e) { Error($"{a.Owner}: {e.Message}"); }
            });
        menu.IsOpen = true;
    }

    // ---- connection ---------------------------------------------------------------------------------

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_session.IsConnected)
        {
            await _session.DisconnectAsync();
            return;
        }
        var dialog = new ConnectWindow(_profile, _protector, Radar.Sector) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        SaveProfile();
        var st = _profile.Station;
        var cn = _profile.Connection;
        if (!Frequency.TryParse(st.Frequency, out var khz)) khz = 199998;
        ConnectButton.IsEnabled = false;
        try
        {
            await _session.ConnectAsync(new AtcConnectInfo(cn.Host, cn.Port, cn.Cid, _protector.Unprotect(cn.ProtectedPassword),
                cn.RealName, st.Rating, st.Callsign, khz, st.Facility, st.VisualRange, Radar.Sector?.Center ?? Radar.ViewCenter));
        }
        catch (FsdLoginException ex)
        {
            Error("Не удалось подключиться: " + ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void UpdateConnectionState(bool connected)
    {
        ConnectText.Text = connected ? "В сети" : "Подключиться";
        ConnectDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, connected ? "SuccessBrush" : "DangerBrush");
        StationText.Text = connected && _session.Info is { } i ? $"{i.Callsign} · {Frequency.Format(i.FrequencyKhz)}" : "Не в сети";
        if (!connected) AtcList.ItemsSource = null;
        _dirty = true;
    }

    // ---- messages ----------------------------------------------------------------------------------------

    private void OnMessage(AtcMessage m)
    {
        var theme = _profile.Theme;
        string color = m.IsError ? theme.Danger : m.Outgoing ? theme.MutedText
            : m.Channel switch { MessageChannel.Private => theme.Accent, MessageChannel.Server => theme.Success, MessageChannel.Broadcast => theme.Danger, _ => theme.Text };
        if (m.Channel == MessageChannel.Private && m.Peer != null)
        {
            AddLine(m.Peer, m.From, m.Text, color);
            if (!m.Outgoing) SystemSounds.Asterisk.Play();
            return;
        }
        string from = m.Channel == MessageChannel.Radio && m.FrequencyKhz is { } f && !m.Outgoing ? $"{m.From} [{Frequency.Format(f)}]" : m.From;
        AddLine("Радио", from, m.Text, color);
    }

    private void AddLine(string chat, string from, string text, string color)
    {
        OpenChat(chat, activate: false);
        var lines = _chats[chat];
        foreach (var part in text.Split('\n'))
        {
            lines.Add(new ChatLine(DateTime.UtcNow.ToString("HH:mm"), from + ":", part, Paint.Brush(color)));
            from = "";
        }
        while (lines.Count > 500) lines.RemoveAt(0);
        if (chat == _activeChat && ChatList.Items.Count > 0) ChatList.ScrollIntoView(ChatList.Items[^1]);
        else if (ChatTabs.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == chat) is { } tab)
            tab.FontWeight = FontWeights.Bold;
    }

    private void Info(string text) => AddLine("Радио", "Network-ATC", text, _profile.Theme.MutedText);
    private void Error(string text) => AddLine("Радио", "Network-ATC", text, _profile.Theme.Danger);

    private void OpenChat(string name, bool activate = true)
    {
        if (!_chats.ContainsKey(name))
        {
            _chats[name] = [];
            var tab = new RadioButton { Content = name, Tag = name, Style = (Style)FindResource("Segment"), GroupName = "chats" };
            tab.Checked += (_, _) =>
            {
                _activeChat = name;
                tab.FontWeight = FontWeights.Normal;
                ChatList.ItemsSource = _chats[name];
            };
            ChatTabs.Children.Add(tab);
            if (_chats.Count == 1) tab.IsChecked = true;
        }
        if (activate && ChatTabs.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == name) is { } t) t.IsChecked = true;
    }

    private async void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && _history.Count > 0)
        {
            _historyIndex = Math.Max(0, _historyIndex - 1);
            CommandLine.Text = _history[_historyIndex];
            CommandLine.CaretIndex = CommandLine.Text.Length;
            return;
        }
        if (e.Key == Key.Down && _history.Count > 0)
        {
            _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
            CommandLine.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
            return;
        }
        if (e.Key == Key.Escape)
        {
            CommandLine.Clear();
            Radar.Focus();
            return;
        }
        if (e.Key != Key.Enter) return;
        string line = CommandLine.Text.Trim();
        if (line.Length == 0) return;
        CommandLine.Clear();
        _history.Add(line);
        _historyIndex = _history.Count;
        try
        {
            if (_activeChat != "Радио" && !line.StartsWith('.'))
            {
                await _session.SendPrivateAsync(_activeChat, _commands.ExpandAlias(line));
                return;
            }
            var feedback = await _commands.ExecuteAsync(line);
            if (feedback != null) Info(feedback);
        }
        catch (InvalidOperationException ex)
        {
            Error(ex.Message);
        }
        if (Radar.Selected is { } t) ShowPlan(t);
    }

    // ---- toolbar ------------------------------------------------------------------------------------------

    private void OnLayersClick(object sender, RoutedEventArgs e) => LayersPopup.IsOpen = LayersButton.IsChecked == true;
    private void OnLayersPopupClosed(object? sender, EventArgs e) => LayersButton.IsChecked = false;

    private void OnLayerToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: LayerItem item })
        {
            _profile.Layers[item.Key] = item.Visible;
            _dirty = true;
        }
    }

    private void OnPanelsChanged(object sender, RoutedEventArgs e) => ApplyPanels();

    private void OnOpenSectorClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Сектор EuroScope (*.sct;*.sct2)|*.sct;*.sct2|Все файлы|*.*", Title = "Открыть сектор" };
        if (dialog.ShowDialog(this) != true) return;
        _profile.SectorFile = dialog.FileName;
        _profile.ViewCenterLatitude = 0;
        LoadSector(dialog.FileName);
        SaveProfile();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_profile.Clone(), _tagFields, _plugins, _protector) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var oldSector = _profile.SectorFile;
        _profile = dialog.Result;
        Radar.Profile = _profile;
        ApplyProfile();
        BuildLayerList();
        if (_profile.SectorFile != oldSector) LoadSector(_profile.SectorFile);
        SaveProfile();
    }

    // ---- keyboard ---------------------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool inText = Keyboard.FocusedElement is TextBox or PasswordBox;
        string? action = null;
        foreach (var (name, gesture) in _profile.KeyBindings)
        {
            if (KeyBindingParser.TryParse(gesture, out var k, out var mods) && k == key && mods == Keyboard.Modifiers)
            {
                action = name;
                break;
            }
        }
        // Plain keys belong to text boxes; only function keys, Escape and chords work while typing.
        if (action == null || inText && Keyboard.Modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24) && key != Key.Escape)
            return;
        e.Handled = RunAction(action);
    }

    private bool RunAction(string action)
    {
        switch (action)
        {
            case "ZoomIn": Radar.Zoom(1 / 1.25); return true;
            case "ZoomOut": Radar.Zoom(1.25); return true;
            case "CenterOnSector": if (Radar.Sector is { } s) Radar.CenterOn(s.Center); return true;
            case "FocusCommandLine":
                CommandLine.Focus();
                CommandLine.Text = ".";
                CommandLine.CaretIndex = 1;
                return true;
            case "ToggleAircraftList": SideToggle.IsChecked = SideToggle.IsChecked != true; ApplyPanels(); return true;
            case "ToggleMessages": MessagesToggle.IsChecked = MessagesToggle.IsChecked != true; ApplyPanels(); return true;
            case "ToggleFlightPlan": PlanTab.IsChecked = true; return true;
            case "ToggleLayers": LayersButton.IsChecked = !LayersPopup.IsOpen; LayersPopup.IsOpen = !LayersPopup.IsOpen; return true;
            case "OpenSettings": OnSettingsClick(this, new RoutedEventArgs()); return true;
            case "Connect": OnConnectClick(this, new RoutedEventArgs()); return true;
            case "TrackSelected": if (Radar.Selected is { } t) { t.IsTracked = !t.IsTracked; ShowPlan(t); _dirty = true; } return true;
            case "ClearSelection": Radar.Select(null); Radar.Focus(); return true;
        }
        return false;
    }

    // ---- shutdown ----------------------------------------------------------------------------------------------

    private void SaveProfile()
    {
        var c = Radar.ViewCenter;
        _profile.ViewCenterLatitude = c.Latitude;
        _profile.ViewCenterLongitude = c.Longitude;
        _profile.ViewNmPerPixel = Radar.NmPerPixel;
        try { _profile.Save(_profilePath); }
        catch (IOException e) { Error("Не удалось сохранить профиль: " + e.Message); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveProfile();
        _plugins.ShutdownAll();
        _session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }
}
