using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetworkAtc.App.Controls;
using NetworkAtc.App.Services;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.EsPlugins;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Plugins;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Tags;
using NetworkAtc.Core.Voice;
using NetworkAtc.Plugins;
using SkyNetwork.Voice;
using Track = NetworkAtc.Core.Radar.Track;
using TagClickEventArgs = NetworkAtc.App.Radar.TagClickEventArgs;

namespace NetworkAtc.App.Views;

public sealed record ChatLine(string Time, string From, string Text, Brush Brush);

public sealed record TrafficRow(string Callsign, string Level, string Info, Track Track);

public sealed record AtcRow(string Callsign, string Frequency, string Id);

public sealed record DepartureView(Track Track, string Callsign, string Type, string Destination, string Sid, string Runway, string Rfl,
    string Squawk, string Clearance, string Status);

public sealed record ArrivalView(Track Track, string Callsign, string Type, string Departure, string Star, string Runway, string Level,
    string Distance, string Eta);

public sealed record WeatherView(string Station, string Atis, string Summary, string Raw);

public sealed record ConflictView(Track Track, string Pair, string Separation);

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
        ("ARTCC", "Boundaries (ARTCC)"), ("ARTCC HIGH", "Upper boundaries"), ("ARTCC LOW", "Lower boundaries"),
        ("SECTORLINES", "Sector lines (.ese)"), ("SID", "SID"), ("STAR", "STAR"), ("LOW AIRWAY", "Low airways"),
        ("HIGH AIRWAY", "High airways"), ("GEO", "Geography (GEO)"), ("REGIONS", "Regions"), ("RUNWAYS", "Runways"),
        ("AIRPORTS", "Airports"), ("VOR", "VOR"), ("NDB", "NDB"), ("FIXES", "Fixes"), ("FIX NAMES", "Fix names"),
        ("LABELS", "Labels"), ("FREETEXT", "Free text (.ese)"), ("RANGE RINGS", "Range rings"),
        ("CENTERLINES", "Runway centerlines"),
    ];

    private static readonly (string Id, string Title)[] ListWindows =
    [
        ("departures", "Departures"), ("arrivals", "Arrivals"), ("traffic", "All traffic"), ("flightplan", "Flight plan"),
        ("atc", "Controllers"), ("conflicts", "Conflicts (STCA)"), ("messages", "Messages"),
        ("sil", "Incoming (SIL)"), ("sel", "Outgoing (SEL)"),
    ];

    private readonly string _profilePath;
    private readonly DpapiProtector _protector = new();
    private readonly AtcSession _session = new();
    private readonly TagFields _tagFields = new();
    private readonly PluginRegistry _registry = new();
    private readonly PluginManager _plugins;
    private readonly CommandProcessor _commands;
    private readonly Workspace _workspace;
    private readonly SoundService _sounds;
    private readonly VoiceService _voice;
    private readonly NetworkAtc.Core.Atis.AtisService _atisService;
    private readonly AtisManager _atis;
    private AtisWindow? _atisWindow;
    private string _atisLettersSeen = "";
    private bool _updatingPlan;
    /// <summary>Pairs in conflict at the last check: the alert sounds for every new pair.</summary>
    private HashSet<string> _conflictPairs = [];
    private bool _wasConnected;
    private readonly Stca _stca = new();
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, ObservableCollection<ChatLine>> _chats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FloatingPanel> _windows;
    private readonly List<string> _history = [];
    private Profile _profile;
    private string? _sectorPath;
    private string _activeChat = "Radio";
    private int _historyIndex;
    private bool _dirty = true;
    private int _frameCount;

    public MainWindow(Profile profile, string profilePath, string? sectorPath)
    {
        InitializeComponent();
        _profile = profile;
        _profilePath = profilePath;
        ThemeApplier.Apply(_profile.Theme);

        _windows = new FloatingPanel[] { DeparturesWindow, ArrivalsWindow, TrafficWindow, FlightPlanWindow, AtcWindow, ConflictsWindow, MessagesWindow, SilWindow, SelWindow }
            .ToDictionary(w => w.WindowId, StringComparer.OrdinalIgnoreCase);
        foreach (var w in _windows.Values) w.LayoutChanged += (_, _) => SaveWindowLayout((FloatingPanel)w);

        _plugins = new PluginManager(_session, _tagFields, _registry, () => Radar.Selected, Path.Combine(Profile.DefaultDirectory, "plugin-data"));
        _commands = new CommandProcessor(_session, () => _profile, _registry, () => Radar.Selected);
        _workspace = new Workspace(_session, () => _profile);
        _commands.Workspace = _workspace;
        _tagFields.Workspace = _workspace;
        Radar.Workspace = _workspace;
        _session.LocalCallsign = _profile.Station.Callsign;
        _session.ControllerInfo = () => _commands.ControllerInfoLines();
        _sounds = new SoundService(() => _profile.Sounds);
        _voice = new VoiceService(Dispatcher, () => _profile.Voice);
        _voice.Changed += OnVoiceChanged;
        _voice.Message += (text, error) => { if (error) Error(text); else Info(text); };
        _voice.ApplySettings();

        Radar.Profile = _profile;
        Radar.TagFields = _tagFields;
        Radar.Plugins = _registry;
        Radar.Tracks = () => _session.Tracks;
        Radar.IsHeard = cs => _voice.Heard.Contains(cs);
        Radar.SelectionChanged += (_, t) => { OnSelectionChanged(t); _es?.SetAsel(t?.Callsign ?? ""); };
        Radar.ViewChanged += (_, _) => { UpdateViewInfo(); _esGeometryDirty = true; };
        Radar.TargetMenuRequested += (_, t) => ShowTargetMenu(t);
        Radar.TagClicked += (_, e) => OnTagClicked(e);

        _session.TrackUpdated += (_, _) => _dirty = true;
        _session.TrackRemoved += (_, cs) => Dispatcher.BeginInvoke(() =>
        {
            if (Radar.Selected?.Callsign == cs) Radar.Select(null);
            _dirty = true;
        });
        _session.FlightPlanUpdated += (_, t) => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(t, Radar.Selected)) ShowPlan(t); });
        _session.MessageReceived += (_, m) => Dispatcher.BeginInvoke(() =>
        {
            OnMessage(m);
            ForwardChatToEsPlugins(m);
        });
        _session.ControllersChanged += (_, _) => Dispatcher.BeginInvoke(RefreshControllers);
        _session.ConnectionChanged += (_, c) => Dispatcher.BeginInvoke(() => UpdateConnectionState(c));
        _registry.Log += (_, text) => Dispatcher.BeginInvoke(() => AddLine("Radio", "plugin", text, _profile.Theme.MutedText));
        _registry.Changed += (_, _) => Dispatcher.BeginInvoke(BuildLayerList);
        _commands.CenterRequested += (_, p) => Radar.CenterOn(p);
        _commands.SelectRequested += (_, t) => Radar.Select(t);
        _commands.Changed += (_, _) => { _dirty = true; Dispatcher.BeginInvoke(RefreshWeather); };
        _session.Coordination += (_, e) => Dispatcher.BeginInvoke(() => OnCoordination(e));
        _workspace.Weather.Updated += (_, _) => Dispatcher.BeginInvoke(RefreshWeather);

        // ATIS stations: their own connections (UUEE_ATIS), the letter moves on with a new METAR, the voice follows.
        _atisService = new NetworkAtc.Core.Atis.AtisService(() => _profile, text => _commands.ExpandVariables(text), s => _workspace.Weather.Get(s));
        _atis = new AtisManager(_atisService, () => _profile, () => _session.IsConnected ? _session.Info : null,
            icao => Radar.Sector?.Airports.FirstOrDefault(a => a.Name.Equals(icao, StringComparison.OrdinalIgnoreCase))?.Position,
            (text, error) => Dispatcher.BeginInvoke(() => { if (error) Error(text); else Info(text); }));
        _atis.Changed += () => Dispatcher.BeginInvoke(UpdateAtisButton);
        _workspace.Weather.Updated += (_, m) =>
        {
            _es?.Metar(m.Station, m.Raw);
            _atisService.OnMetar(m);
            _atis.Refresh(m.Station);
        };
        _workspace.Weather.Enabled = _profile.FetchMetar;
        _workspace.Weather.Start();

        OpenChat("Radio");
        ApplyProfile();
        LoadSector(sectorPath);
        LoadPlugins();
        StartEsPlugins();
        BuildLayerList();
        UpdateConnectionState(false);

        Loaded += (_, _) => ApplyWindowLayouts();
        _frame.Tick += (_, _) => OnFrame();
        _frame.Start();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        RefreshWeather();
        Info("Network-ATC ready. .help: commands · .demo: demo traffic · RWY: airports and runways · F12: assume/accept · right-drag: measure distance");
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
        ShowFilter();
        _dirty = true;
    }

    private void ShowFilter()
    {
        FilterLowBox.Text = (Math.Max(0, _profile.Targets.FilterFloor) / 100).ToString("000");
        FilterHighBox.Text = (Math.Min(_profile.Targets.FilterCeiling, 99900) / 100).ToString("000");
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnFilterChanged(sender, e);
        Radar.Focus();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(FilterLowBox.Text.Trim().TrimStart('F', 'f', 'A', 'a'), out var low) && low >= 0) _profile.Targets.FilterFloor = low == 0 ? -1000 : low * 100;
        if (int.TryParse(FilterHighBox.Text.Trim().TrimStart('F', 'f', 'A', 'a'), out var high) && high > 0)
            _profile.Targets.FilterCeiling = high >= 999 ? 999999 : high * 100;
        ShowFilter();
        _dirty = true;
    }

    private void LoadSector(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "demo", "UUEE-demo.natc");
            if (!File.Exists(path))
            {
                Radar.SetSector(null);
                return;
            }
        }
        try
        {
            var sector = SectorLoader.Load(path);
            bool sameAsLastTime = string.Equals(_profile.SectorFile, path, StringComparison.OrdinalIgnoreCase);
            Radar.SetSector(sector);
            _workspace.SetSector(sector);
            _sectorPath = path;
            // Each sector has its own EuroScope plugins: those of the previous sector are unloaded.
            if (_profile.SwitchPluginsTo(path)) SyncEsPlugins();
            _es?.SendSector(sector, path);
            _commands.DemoCenter = sector.Center;
            if (sameAsLastTime && _profile.ViewCenterLatitude != 0)
                Radar.SetView(new GeoPoint(_profile.ViewCenterLatitude, _profile.ViewCenterLongitude), _profile.ViewNmPerPixel);
            _profile.SectorFile = path;
            _profile.RememberSector(path, sector.Name);
            if (_profile.ActiveAirports.Count == 0 && sector.DefaultAirport.Length == 4) _profile.ActiveAirports = [sector.DefaultAirport.ToUpperInvariant()];
            SectorText.Text = $"{sector.Name} · {SectorLoader.Describe(path)}";
            Title = $"Network-ATC — {sector.Name}";
            BuildLayerList();
            if (sector.Warnings.Count > 0)
                Info($"Sector loaded with warnings ({sector.Warnings.Count}): {string.Join("; ", sector.Warnings.Take(3))}");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Error("Could not open sector: " + e.Message);
        }
    }

    private void LoadPlugins()
    {
        _plugins.LoadFrom(PluginFolder);
        _plugins.LoadFrom(Path.Combine(AppContext.BaseDirectory, "plugins"));
        foreach (var p in _plugins.Plugins) Info($"Plugin: {p.Plugin.Name} {p.Plugin.Version}");
        foreach (var e in _plugins.Errors) Error($"Plugin {Path.GetFileName(e.File)}: {e.Reason}");
    }

    private void BuildLayerList()
    {
        var items = MapLayers.Select(l => new LayerItem(l.Key, l.Title, _profile.IsLayerVisible(l.Key))).ToList();
        if (Radar.Sector is { } s)
            items.AddRange(s.CustomLayers.Select(l => new LayerItem(l, l + " · map", _profile.IsLayerVisible(l))));
        lock (_registry.Overlays)
            items.AddRange(_registry.Overlays.Select(o => new LayerItem("plugin:" + o.Overlay.Name, o.Overlay.Name + " · " + o.Owner,
                _profile.IsLayerVisible("plugin:" + o.Overlay.Name))));
        LayerList.ItemsSource = items;
    }

    // ---- floating list windows ------------------------------------------------------------------

    private void ApplyWindowLayouts()
    {
        double w = WindowsLayer.ActualWidth, h = WindowsLayer.ActualHeight;
        foreach (var (id, panel) in _windows)
        {
            var layout = _profile.Windows.GetValueOrDefault(id) ?? Profile.DefaultWindows()[id];
            panel.Place(layout.X, layout.Y, layout.Width, layout.Height, w, h);
            panel.Visibility = layout.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SaveWindowLayout(FloatingPanel panel) =>
        _profile.Windows[panel.WindowId] = new WindowLayout(panel.Left, panel.Top, panel.ActualWidth > 0 ? panel.ActualWidth : panel.Width,
            panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height, panel.Visibility == Visibility.Visible);

    private void ShowWindow(string id, bool? visible = null)
    {
        var panel = _windows[id];
        bool show = visible ?? panel.Visibility != Visibility.Visible;
        panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) panel.BringToFront();
        SaveWindowLayout(panel);
    }

    private void OnListsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ListsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (id, title) in ListWindows)
        {
            var item = new MenuItem { Header = (_windows[id].Visibility == Visibility.Visible ? "✓  " : "    ") + title };
            item.Click += (_, _) => ShowWindow(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "    Reset window layout" };
        reset.Click += (_, _) =>
        {
            _profile.Windows = Profile.DefaultWindows();
            ApplyWindowLayouts();
        };
        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    // ---- frame loop --------------------------------------------------------------------------------

    private void OnFrame()
    {
        _frameCount++;
        PumpEsPlugins();
        if (Radar.NeedsAnimation) _dirty = true;
        if (_dirty)
        {
            _dirty = false;
            Radar.Conflicts = _profile.StcaEnabled ? _stca.Check(_session.Tracks) : [];
            var pairs = Radar.Conflicts.Select(c => c.PairKey).ToHashSet();
            if (!pairs.IsSubsetOf(_conflictPairs)) _sounds.Play(SoundEvent.ConflictAlert);
            _conflictPairs = pairs;
            Radar.InvalidateVisual();
        }
        if (_frameCount % 4 == 0)
        {
            ClockText.Text = DateTime.UtcNow.ToString("HH:mm:ss") + "Z";
            CheckAtisLetters();
            AselText.Text = Radar.Selected?.Callsign ?? "—";
            TxText.Text = _session.Info is { } i ? "TX " + Frequency.Format(i.FrequencyKhz) : "";
            UpdateVoiceRadios();
            RefreshLists();
            if (Radar.Selected is { } t && FlightPlanWindow.Visibility == Visibility.Visible) ShowPlanSummary(t);
        }
    }

    private void RefreshLists()
    {
        var tracks = _session.Tracks.Where(t => t.LastUpdate != default).ToList();
        var airports = _profile.ActiveAirports;
        string codes = airports.Count > 0 ? string.Join(" ", airports) : "set .airport";
        DeparturesWindow.Header = $"DEPARTURES · {codes}";
        ArrivalsWindow.Header = $"ARRIVALS · {codes}";

        Update(TrafficList, tracks.OrderBy(t => t.Callsign)
            .Select(t => new TrafficRow(t.Callsign, _tagFields.FlightLevel(t.Altitude),
                $"{t.AircraftType} {(t.Destination.Length > 0 ? "→ " + t.Destination : "")}".Trim(), t)).ToList());
        var proc = _workspace.Procedures;
        Update(DepartureList, TrafficLists.Departures(_session.Tracks, airports, Radar.Sector)
            .Select(d => new DepartureView(d.Track, d.Callsign, d.Type, d.Destination, proc.Sid(d.Track) ?? "", proc.DepartureRunway(d.Track) ?? "",
                d.Rfl, d.Squawk, d.Track.ClearanceReceived ? "✓" : "·",
                d.Track.GroundState.Length > 0 ? d.Track.GroundState : d.Status)).ToList());
        Update(ArrivalList, TrafficLists.Arrivals(tracks, airports, Radar.Sector)
            .Select(a => new ArrivalView(a.Track, a.Callsign, a.Type, a.Departure, proc.Star(a.Track) ?? "", proc.ArrivalRunway(a.Track) ?? "",
                _tagFields.FlightLevel(a.Track.Altitude),
                double.IsNaN(a.DistanceNm) ? "—" : a.DistanceNm.ToString("0"),
                a.EtaMinutes is { } m ? DateTime.UtcNow.AddMinutes(m).ToString("HH:mm") : "")).ToList());
        var sil = _workspace.InboundList(_tagFields.FlightLevel).ToList();
        Update(SilList, sil);
        SilWindow.Header = $"INCOMING (SIL) · {sil.Count}";
        var sel = _workspace.OutboundList(_tagFields.FlightLevel).ToList();
        Update(SelList, sel);
        SelWindow.Header = $"OUTGOING (SEL) · {sel.Count}";
        Update(ConflictList, Radar.Conflicts.Select(c => new ConflictView(c.A, $"{c.A.Callsign} / {c.B.Callsign}",
            $"{c.DistanceNm:0.0} NM {c.VerticalFeet} ft{(c.Predicted ? " ▸" : "")}")).ToList());
        ConflictsWindow.Header = $"CONFLICTS (STCA) · {Radar.Conflicts.Count}";
        if (Radar.Conflicts.Count > 0 && ConflictsWindow.Visibility != Visibility.Visible && _profile.StcaEnabled)
            ShowWindow("conflicts", true);
    }

    /// <summary>Replace a list's items only when they changed, keeping the selection on the ASEL aircraft.</summary>
    private void Update<T>(ListBox list, List<T> rows) where T : class
    {
        var old = list.ItemsSource as List<T>;
        if (old != null && old.Count == rows.Count && old.Select(r => r.ToString()).SequenceEqual(rows.Select(r => r.ToString())))
            return;
        list.SelectionChanged -= OnListSelected;
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(TrackOf(r), Radar.Selected));
        list.SelectionChanged += OnListSelected;
    }

    private static Track? TrackOf(object? row) => row switch
    {
        TrafficRow r => r.Track,
        DepartureRow d => d.Track,
        DepartureView d => d.Track,
        SectorListRow r => r.Track,
        ArrivalView a => a.Track,
        ConflictView c => c.Track,
        _ => null,
    };

    private void OnListSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && TrackOf(list.SelectedItem) is { } t) Radar.Select(t);
    }

    private void OnConflictSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ConflictList.SelectedItem is ConflictView c)
        {
            Radar.Select(c.Track);
            Radar.CenterOn(c.Track.Position);
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

    private void RefreshControllers()
    {
        _workspace.UpdateOwnership();
        AtcList.ItemsSource = _session.Controllers.Select(c => new AtcRow(c.Callsign, Frequency.Format(c.FrequencyKhz), _workspace.ShortName(c.Callsign)))
            .ToList();
    }

    private void RefreshWeather()
    {
        WeatherStrip.ItemsSource = _profile.ActiveAirports.Select(icao =>
        {
            var m = _workspace.Weather.Get(icao);
            return new WeatherView(icao, _profile.AtisLetters.GetValueOrDefault(icao, ""),
                m == null ? "—" : $"{m.Wind} Q{m.Qnh?.ToString() ?? "—"}", m?.Raw ?? "METAR not received yet");
        }).ToList();
    }

    // ---- selection and flight plan window -----------------------------------------------------------

    private void OnSelectionChanged(Track? t)
    {
        AselText.Text = t?.Callsign ?? "—";
        if (t == null)
        {
            PlanEmpty.Visibility = Visibility.Visible;
            PlanContent.Visibility = Visibility.Collapsed;
            return;
        }
        ShowPlan(t);
        if (_profile.Panels.ShowFlightPlan && FlightPlanWindow.Visibility != Visibility.Visible) ShowWindow("flightplan", true);
    }

    private void ShowPlan(Track t)
    {
        _updatingPlan = true;
        try
        {
            PlanEmpty.Visibility = Visibility.Collapsed;
            PlanContent.Visibility = Visibility.Visible;
            PlanCallsign.Text = t.Callsign;
            CflBox.Text = t.ClearedAltitude is { } c ? _tagFields.FlightLevel(c) : "";
            HdgBox.Text = t.AssignedHeading?.ToString("000") ?? "";
            SpdBox.Text = t.AssignedSpeed?.ToString() ?? "";
            SqBox.Text = t.AssignedSquawk?.ToString("0000") ?? "";
            ScratchBox.Text = t.Scratchpad;

            var proc = _workspace.Procedures;
            FillCombo(SidBox, proc.Sids(t.Departure, proc.DepartureRunway(t)).Select(p => p.Name), t.Sid, proc.Sid(t));
            FillCombo(StarBox, proc.Stars(t.Destination, proc.ArrivalRunway(t)).Select(p => p.Name), t.Star, proc.Star(t));
            FillCombo(DepRwyBox, RunwaysOf(t.Departure), t.DepartureRunway, proc.DepartureRunway(t));
            FillCombo(ArrRwyBox, RunwaysOf(t.Destination), t.ArrivalRunway, proc.ArrivalRunway(t));
            ClrBox.IsChecked = t.ClearanceReceived;

            FpRules.Text = t.Rules;
            FpType.Text = t.AircraftType;
            FpTas.Text = t.FiledSpeed > 0 ? t.FiledSpeed.ToString() : "";
            FpDep.Text = t.Departure;
            FpDest.Text = t.Destination;
            FpAltn.Text = t.Alternate;
            FpRfl.Text = t.FiledAltitude;
            FpTime.Text = t.Plan?.DepartureTime ?? "";
            FpRoute.Text = t.Route;
            FpRemarks.Text = t.Remarks;
            ShowPlanSummary(t);
            BuildCoordinationButtons(t);
        }
        finally
        {
            _updatingPlan = false;
        }
    }

    private IEnumerable<string> RunwaysOf(string airport) =>
        Radar.Sector?.Runways.Where(r => r.Airport.Equals(airport, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => new[] { r.Id1, r.Id2 }).Distinct().Order() ?? Enumerable.Empty<string>();

    /// <summary>Items: "auto (X)" for the automatic choice, then the values; selects the assigned one.</summary>
    private static void FillCombo(ComboBox box, IEnumerable<string> values, string assigned, string? effective)
    {
        var items = new List<ComboBoxItem> { new() { Content = $"auto{(effective != null && assigned.Length == 0 ? $" ({effective})" : "")}", Tag = "" } };
        items.AddRange(values.Distinct().Select(v => new ComboBoxItem { Content = v, Tag = v }));
        if (assigned.Length > 0 && items.All(i => (string)i.Tag != assigned)) items.Add(new ComboBoxItem { Content = assigned, Tag = assigned });
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == assigned) ?? items[0];
    }

    private void ShowPlanSummary(Track t)
    {
        var state = _workspace.StateOf(t);
        PlanSubtitle.Text = $"{(t.AircraftType.Length > 0 ? t.AircraftType : "type ?")}/{t.WakeCategory}   sqk {t.Squawk:0000}{(t.ModeC ? "" : " STBY")}" +
                            (t.CommType.Length > 0 ? $"   /{t.CommType.ToLowerInvariant()}" : "");
        PlanRoute.Text = t.HasFlightPlan ? $"{t.Departure}  →  {t.Destination}" : "No flight plan";
        OwnerText.Text = TrackStates.Title(state) + (t.Owner.Length > 0 && !t.IsTracked ? $" · {t.Owner}" : "");
        string warn = Warnings.Text(t, _workspace.WarningOf(t));
        PlanDetails.Text = $"{_tagFields.FlightLevel(t.Altitude)}  {t.GroundSpeed} kt  {t.Heading:000}°  {t.VerticalSpeed:+0;-0;0} ft/min" +
                           (warn.Length > 0 ? "   " + warn : "");
    }

    private void BuildCoordinationButtons(Track t)
    {
        CoordButtons.Children.Clear();
        void Add(string title, Action action, bool primary = false)
        {
            var b = new Button { Content = title, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(9, 3, 9, 3) };
            if (primary) b.Style = (Style)FindResource("Primary");
            b.Click += (_, _) => action();
            CoordButtons.Children.Add(b);
        }
        switch (_workspace.StateOf(t))
        {
            case TrackState.TransferToMe:
                Add($"Accept from {t.HandoffFrom}", () => Coord(() => _session.AcceptHandoffAsync(t)), primary: true);
                Add("Refuse", () => Coord(() => _session.RefuseHandoffAsync(t)));
                break;
            case TrackState.TransferFromMe:
                Add($"Cancel handoff to {t.HandoffTo}", () => Coord(() => _session.CancelHandoffAsync(t)));
                break;
            case TrackState.Assumed:
                if (_workspace.NextController(t) is { } next)
                    Add($"Hand off to {_workspace.ShortName(next)}", () => Coord(() => _session.HandoffAsync(t, next)), primary: true);
                Add("Hand off…", () => ShowControllerMenu(t, cs => Coord(() => _session.HandoffAsync(t, cs))));
                Add("Release", () => Coord(() => _session.ReleaseAsync(t)));
                break;
            case TrackState.Redundant:
                break;
            default:
                Add("Assume", () => Coord(() => _session.AssumeAsync(t)), primary: true);
                break;
        }
        Add(t.ShowRoute ? "Hide route" : "Route", () => { t.ShowRoute = !t.ShowRoute; _dirty = true; BuildCoordinationButtons(t); });
    }

    /// <summary>Runs a coordination action and reports its refusal reason, if any.</summary>
    private async void Coord(Func<Task<string?>> action)
    {
        try
        {
            if (await action() is { } problem) Error(problem);
        }
        catch (InvalidOperationException ex)
        {
            Error(ex.Message);
        }
        _dirty = true;
        if (Radar.Selected is { } s) ShowPlan(s);
    }

    private void AssumeOrRelease(Track t) => Coord(() => t.IsTracked ? _session.ReleaseAsync(t) : _session.AssumeAsync(t));

    /// <summary>Menu of the controllers online, the next one by sector first.</summary>
    private void ShowControllerMenu(Track t, Action<string> pick, string title = "Hand off")
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = title, IsEnabled = false });
        foreach (var item in ControllerItems(t, pick)) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private List<object> ControllerItems(Track t, Action<string> pick)
    {
        var items = new List<object>();
        string? next = _workspace.NextController(t);
        var online = _session.Controllers.OrderBy(c => c.Callsign != next).ThenBy(c => c.Callsign).ToList();
        foreach (var c in online)
        {
            var mi = new MenuItem
            {
                Header = $"{(c.Callsign == next ? "» " : "")}{c.Callsign}  {_workspace.ShortName(c.Callsign)}  {Frequency.Format(c.FrequencyKhz)}",
                FontWeight = c.Callsign == next ? FontWeights.SemiBold : FontWeights.Normal,
            };
            string cs = c.Callsign;
            mi.Click += (_, _) => pick(cs);
            items.Add(mi);
        }
        if (online.Count == 0) items.Add(new MenuItem { Header = "no controllers online (hand off: .ho CALLSIGN)", IsEnabled = false });
        return items;
    }

    private async void OnAnnotationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || Radar.Selected is not { } t) return;
        var feedback = await _commands.ExecuteAsync($".{box.Tag} {box.Text.Trim()}");
        if (feedback != null && !feedback.StartsWith(t.Callsign, StringComparison.Ordinal)) Error(feedback);
        ShowPlan(t);
        Radar.Focus();
    }

    private void OnProcedureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPlan || sender is not ComboBox { SelectedItem: ComboBoxItem item } box || Radar.Selected is not { } t) return;
        Annotate(t, $".{box.Tag} {item.Tag}");
    }

    private void OnClrBoxClick(object sender, RoutedEventArgs e)
    {
        if (Radar.Selected is { } t && ClrBox.IsChecked != t.ClearanceReceived) Annotate(t, ".clr");
    }

    private void OnClearanceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Track t }) Annotate(t, ".clr");
    }

    private async void OnAmendClick(object sender, RoutedEventArgs e)
    {
        if (Radar.Selected is not { } t) return;
        if (!int.TryParse(FpTas.Text.Trim(), out var tas) || tas is < 0 or > 9999)
        {
            Error("TAS must be a number of knots");
            return;
        }
        var old = t.Plan;
        var plan = new FiledPlan(t.Callsign, FpRules.Text.Trim().ToUpperInvariant(), FpType.Text.Trim().ToUpperInvariant(), tas,
            FpDep.Text.Trim().ToUpperInvariant(), FpTime.Text.Trim(), FpRfl.Text.Trim().ToUpperInvariant(), FpDest.Text.Trim().ToUpperInvariant(),
            FpAltn.Text.Trim().ToUpperInvariant(), FpRemarks.Text.Trim(), FpRoute.Text.Trim().ToUpperInvariant(),
            old?.EnrouteHours ?? "0", old?.EnrouteMinutes ?? "0", old?.FuelHours ?? "0", old?.FuelMinutes ?? "0");
        try
        {
            await _session.AmendFlightPlanAsync(t, plan);
            Info($"Flight plan {t.Callsign} amended" + (_session.IsConnected ? " and sent" : " (locally)"));
        }
        catch (InvalidOperationException ex)
        {
            Error(ex.Message);
        }
        _dirty = true;
        ShowPlan(t);
    }

    private async void OnAssignSquawk(object sender, RoutedEventArgs e)
    {
        if (Radar.Selected is not { } t) return;
        var feedback = await _commands.ExecuteAsync(".sq");
        if (feedback != null) Info(feedback);
        ShowPlan(t);
    }

    // ---- coordination events ------------------------------------------------------------------------

    private void OnCoordination(CoordinationEvent e)
    {
        var t = e.Track;
        switch (e.Kind)
        {
            case CoordinationKind.HandoffRequested:
                Info($"{e.Peer} is handing off {t.Callsign} to you — F12 accept, Ctrl+D refuse");
                _sounds.Play(SoundEvent.HandoffRequest);
                if (SilWindow.Visibility != Visibility.Visible) ShowWindow("sil", true);
                break;
            case CoordinationKind.HandoffAccepted:
                Info($"{e.Peer} accepted {t.Callsign}");
                _sounds.Play(SoundEvent.HandoffAccepted);
                break;
            case CoordinationKind.HandoffRefused:
                Error($"{e.Peer} refused handoff of {t.Callsign}");
                _sounds.Play(SoundEvent.HandoffRefused);
                break;
            case CoordinationKind.HandoffCancelled:
                Info($"{e.Peer} cancelled handoff of {t.Callsign}");
                break;
            case CoordinationKind.PointOut:
                Info($"{e.Peer} points out {t.Callsign} to you");
                _sounds.Play(SoundEvent.PointOut);
                break;
        }
        _dirty = true;
        if (ReferenceEquals(Radar.Selected, t)) ShowPlan(t);
    }

    private void OnSilDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SilList.SelectedItem is SectorListRow { Track: var t })
            Coord(() => _session.AssumeAsync(t));
    }

    private void OnSelDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelList.SelectedItem is not SectorListRow { Track: var t }) return;
        if (_workspace.NextController(t) is { } next) Coord(() => _session.HandoffAsync(t, next));
        else ShowControllerMenu(t, cs => Coord(() => _session.HandoffAsync(t, cs)));
    }

    private void OnAtcDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AtcList.SelectedItem is not AtcRow row) return;
        OpenChat(row.Callsign);
        ShowWindow("messages", true);
        CommandLine.Focus();
    }

    // ---- interactive tag ----------------------------------------------------------------------

    private void OnTagClicked(TagClickEventArgs e)
    {
        var t = e.Track;
        bool pluginHandles = e.Field != null && (_registry.TagClicks.ContainsKey(e.Field) || EsBridge.ParseKey(e.Field, "es:") != null);
        string action = _profile.ResolveTagClick(e.Field, e.Right, pluginHandles);
        if (action == TagActions.None) return;
        Radar.Select(t);
        RunTagAction(t, action, e.Position, e.Right, e.Field);
    }

    /// <summary>A tag action on an aircraft: from a tag click, a plugin list or a EuroScope plugin function.</summary>
    private void RunTagAction(Track t, string action, Point position, bool right, string? field)
    {
        var e = (Position: position, Right: right, Field: field);
        if (action.StartsWith(EsFunctionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            CallEsFunction(action, t, field, position);
            _dirty = true;
            return;
        }
        switch (action)
        {
            case TagActions.ToggleTrack:
                if (_workspace.StateOf(t) == TrackState.TransferToMe) Coord(() => _session.AcceptHandoffAsync(t));
                else AssumeOrRelease(t);
                break;
            case TagActions.AircraftMenu:
                ShowTargetMenu(t);
                break;
            case TagActions.Handoff:
                HandoffClick(t, e.Right);
                break;
            case TagActions.Procedure:
                ProcedureMenu(t);
                break;
            case TagActions.Runway:
                RunwayMenu(t);
                break;
            case TagActions.Clearance:
                Annotate(t, ".clr");
                break;
            case TagActions.Route:
                t.ShowRoute = !t.ShowRoute;
                break;
            case TagActions.ClearedLevel:
            {
                var levels = TagMenus.Levels(t.Altitude, t.ClearedAltitude, _profile.TransitionAltitude);
                var items = levels.Select(v => new EditorItem(_tagFields.FlightLevel(v), _tagFields.FlightLevel(v), v == t.ClearedAltitude)).ToList();
                items.Add(new EditorItem("clear", ""));
                int index = TagMenus.NearestIndex(levels, t.ClearedAltitude ?? t.Altitude);
                TagEditor.Show(Radar, e.Position, $"CFL · {t.Callsign}", items, index, "", "350, F350 or A045",
                    v => Annotate(t, $".cfl {v}"));
                break;
            }
            case TagActions.Heading:
            {
                var headings = TagMenus.Headings(t.AssignedHeading ?? t.Heading);
                var items = headings.Select(h => new EditorItem(h.ToString("000"), h.ToString(), h == t.AssignedHeading)).ToList();
                items.Insert(0, new EditorItem("clear", ""));
                TagEditor.Show(Radar, e.Position, $"Heading · {t.Callsign}", items, 1, "", "1–360",
                    v => Annotate(t, $".hdg {v}"));
                break;
            }
            case TagActions.Speed:
            {
                var speeds = TagMenus.Speeds();
                var items = speeds.Select(v => new EditorItem(v.ToString(), v.ToString(), v == t.AssignedSpeed)).ToList();
                items.Add(new EditorItem("clear", ""));
                TagEditor.Show(Radar, e.Position, $"Speed · {t.Callsign}", items,
                    TagMenus.NearestIndex(speeds, t.AssignedSpeed ?? t.GroundSpeed), "", "knots",
                    v => Annotate(t, $".spd {v}"));
                break;
            }
            case TagActions.Squawk:
            {
                var items = new List<EditorItem> { new("assign free code", "", true) };
                TagEditor.Show(Radar, e.Position, $"Squawk · {t.Callsign}", items, -1,
                    t.AssignedSquawk?.ToString("0000") ?? "", "4 digits 0–7", v => Annotate(t, $".sq {v}"));
                break;
            }
            case TagActions.Scratchpad:
                TagEditor.Show(Radar, e.Position, $"Scratchpad · {t.Callsign}", [], -1, t.Scratchpad, "Enter to save, empty to delete",
                    v => Annotate(t, $".scratch {v}"));
                break;
            case TagActions.FlightPlan:
                ShowWindow("flightplan", true);
                break;
            case TagActions.PrivateMessage:
                CommandLine.Text = $".msg {t.Callsign} ";
                CommandLine.Focus();
                CommandLine.CaretIndex = CommandLine.Text.Length;
                break;
            case TagActions.Plugin when e.Field != null && _registry.TagClicks.TryGetValue(e.Field, out var handler):
                try { handler.Handler(t, e.Right); }
                catch (Exception ex) { Error($"{handler.Owner}: {ex.Message}"); }
                break;
            case TagActions.Select:
                Radar.Select(t);
                break;
        }
        _dirty = true;
        ShowPlan(t);
    }

    /// <summary>Click on the owner/handoff field: accept or refuse an offer, offer our aircraft, assume a free one.</summary>
    private void HandoffClick(Track t, bool right)
    {
        switch (_workspace.StateOf(t))
        {
            case TrackState.TransferToMe:
                Coord(() => right ? _session.RefuseHandoffAsync(t) : _session.AcceptHandoffAsync(t));
                break;
            case TrackState.TransferFromMe:
                Coord(() => _session.CancelHandoffAsync(t));
                break;
            case TrackState.Assumed:
                if (!right && _workspace.NextController(t) is { } next) Coord(() => _session.HandoffAsync(t, next));
                else ShowControllerMenu(t, cs => Coord(() => _session.HandoffAsync(t, cs)));
                break;
            case TrackState.Redundant:
                Info($"{t.Callsign} is tracked by {t.Owner}");
                break;
            default:
                Coord(() => _session.AssumeAsync(t));
                break;
        }
    }

    private bool IsDepartureSide(Track t) =>
        _profile.ActiveAirports.Contains(t.Departure, StringComparer.OrdinalIgnoreCase) &&
        _workspace.ProcedureOf(t) is var p && (p == null || p == _workspace.Procedures.Sid(t));

    private void ProcedureMenu(Track t)
    {
        bool departure = IsDepartureSide(t);
        var proc = _workspace.Procedures;
        var names = departure
            ? proc.Sids(t.Departure, proc.DepartureRunway(t)).Select(p => p.Name)
            : proc.Stars(t.Destination, proc.ArrivalRunway(t)).Select(p => p.Name);
        string cmd = departure ? ".sid" : ".star";
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = departure ? "SID" : "STAR", IsEnabled = false });
        foreach (var n in names.Distinct())
        {
            var mi = new MenuItem { Header = n };
            mi.Click += (_, _) => Annotate(t, $"{cmd} {n}");
            menu.Items.Add(mi);
        }
        var auto = new MenuItem { Header = "automatic" };
        auto.Click += (_, _) => Annotate(t, cmd);
        menu.Items.Add(auto);
        menu.IsOpen = true;
    }

    private void RunwayMenu(Track t)
    {
        bool departure = IsDepartureSide(t);
        string cmd = departure ? ".drwy" : ".arwy";
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = departure ? "Departure runway" : "Arrival runway", IsEnabled = false });
        foreach (var r in RunwaysOf(departure ? t.Departure : t.Destination))
        {
            var mi = new MenuItem { Header = r };
            mi.Click += (_, _) => Annotate(t, $"{cmd} {r}");
            menu.Items.Add(mi);
        }
        var auto = new MenuItem { Header = "default" };
        auto.Click += (_, _) => Annotate(t, cmd);
        menu.Items.Add(auto);
        menu.IsOpen = true;
    }

    /// <summary>Runs an annotation command for the aircraft and reports problems.</summary>
    private async void Annotate(Track t, string command)
    {
        Radar.Select(t);
        var feedback = await _commands.ExecuteAsync(command.TrimEnd());
        if (feedback != null && !feedback.StartsWith(t.Callsign, StringComparison.Ordinal)) Error(feedback);
        _dirty = true;
        ShowPlan(t);
    }

    /// <summary>The aircraft menu, as EuroScope's right-click menu: coordination first, then procedures and tools.</summary>
    private void ShowTargetMenu(Track t)
    {
        var menu = new ContextMenu();
        MenuItem Item(string title, Action action, ItemsControl? parent = null)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => { action(); _dirty = true; if (ReferenceEquals(Radar.Selected, t)) ShowPlan(t); };
            (parent ?? menu).Items.Add(item);
            return item;
        }
        MenuItem Sub(string title)
        {
            var item = new MenuItem { Header = title };
            menu.Items.Add(item);
            return item;
        }

        menu.Items.Add(new MenuItem { Header = $"{t.Callsign} · {TrackStates.Title(_workspace.StateOf(t))}", IsEnabled = false });
        switch (_workspace.StateOf(t))
        {
            case TrackState.TransferToMe:
                Item($"Accept from {t.HandoffFrom}", () => Coord(() => _session.AcceptHandoffAsync(t)));
                Item("Refuse handoff", () => Coord(() => _session.RefuseHandoffAsync(t)));
                break;
            case TrackState.TransferFromMe:
                Item($"Cancel handoff to {t.HandoffTo}", () => Coord(() => _session.CancelHandoffAsync(t)));
                Item("Release", () => Coord(() => _session.ReleaseAsync(t)));
                break;
            case TrackState.Assumed:
                var transfer = Sub("Hand off");
                foreach (var i in ControllerItems(t, cs => Coord(() => _session.HandoffAsync(t, cs)))) transfer.Items.Add(i);
                Item("Release", () => Coord(() => _session.ReleaseAsync(t)));
                break;
            case TrackState.Redundant:
                menu.Items.Add(new MenuItem { Header = $"tracked by {t.Owner}", IsEnabled = false });
                break;
            default:
                Item("Assume", () => Coord(() => _session.AssumeAsync(t)));
                break;
        }
        var point = Sub("Point out");
        foreach (var i in ControllerItems(t, cs => Coord(() => _session.PointOutAsync(t, cs)))) point.Items.Add(i);
        menu.Items.Add(new Separator());

        var proc = _workspace.Procedures;
        var sid = Sub($"SID  {proc.Sid(t) ?? "—"}");
        foreach (var p in proc.Sids(t.Departure, proc.DepartureRunway(t)).Select(p => p.Name).Distinct())
            Item(p, () => Annotate(t, $".sid {p}"), sid);
        Item("automatic", () => Annotate(t, ".sid"), sid);
        var star = Sub($"STAR  {proc.Star(t) ?? "—"}");
        foreach (var p in proc.Stars(t.Destination, proc.ArrivalRunway(t)).Select(p => p.Name).Distinct())
            Item(p, () => Annotate(t, $".star {p}"), star);
        Item("automatic", () => Annotate(t, ".star"), star);
        var drwy = Sub($"Departure runway  {proc.DepartureRunway(t) ?? "—"}");
        foreach (var r in RunwaysOf(t.Departure)) Item(r, () => Annotate(t, $".drwy {r}"), drwy);
        var arwy = Sub($"Arrival runway  {proc.ArrivalRunway(t) ?? "—"}");
        foreach (var r in RunwaysOf(t.Destination)) Item(r, () => Annotate(t, $".arwy {r}"), arwy);
        var clr = Item("Clearance received", () => Annotate(t, ".clr"));
        clr.IsCheckable = true;
        clr.IsChecked = t.ClearanceReceived;
        var ground = Sub($"Ground state  {(t.GroundState.Length > 0 ? t.GroundState : "—")}");
        foreach (var gs in new[] { "STUP", "PUSH", "TAXI", "DEPA" }) Item(gs, () => Annotate(t, $".state {gs}"), ground);
        Item("clear", () => Annotate(t, ".state"), ground);
        Item("Assign squawk", () => OnAssignSquawk(this, new RoutedEventArgs()));
        menu.Items.Add(new Separator());

        var route = Item("Show route on radar", () => t.ShowRoute = !t.ShowRoute);
        route.IsCheckable = true;
        route.IsChecked = t.ShowRoute;
        var halo = Sub($"Halo around aircraft{(t.HaloNm is { } h ? $"  {h:0.#} NM" : "")}");
        foreach (var nm in new[] { 1, 3, 5, 10, 15 }) Item($"{nm} NM", () => t.HaloNm = nm, halo);
        Item("remove", () => t.HaloNm = null, halo);
        Item("Request flight plan", () => _ = _session.RequestFlightPlanAsync(t.Callsign));
        Item("Contact me", () => Annotate(t, $".contactme {t.Callsign}"));
        Item("Private message…", () =>
        {
            CommandLine.Text = $".msg {t.Callsign} ";
            CommandLine.Focus();
            CommandLine.CaretIndex = CommandLine.Text.Length;
        });
        List<AircraftAction> actions;
        lock (_registry.AircraftActions) actions = _registry.AircraftActions.ToList();
        if (actions.Count > 0) menu.Items.Add(new Separator());
        foreach (var a in actions)
            Item(a.Title, () =>
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
            Error("Could not connect: " + ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void UpdateConnectionState(bool connected)
    {
        ConnectText.Text = connected ? "ONLINE" : "CONNECT";
        ConnectDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, connected ? "SuccessBrush" : "DangerBrush");
        StationText.Text = connected && _session.Info is { } i ? $"{i.Callsign}  {Frequency.Format(i.FrequencyKhz)}" : "";
        StationBox.Visibility = StationText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!connected) AtcList.ItemsSource = null;
        // Voice follows the network connection; its failures never break the network session.
        if (connected && !_wasConnected && _session.Info is { } info)
        {
            UpdateVoiceRadios();
            _voice.Start(info);
        }
        else if (!connected)
        {
            _voice.Stop();
            _ = _atis.DisconnectAllAsync();
        }
        if (connected != _wasConnected) _sounds.Play(connected ? SoundEvent.Connected : SoundEvent.Disconnected);
        _wasConnected = connected;
        _workspace.UpdateOwnership();
        _dirty = true;
    }

    // ---- radio voice ---------------------------------------------------------------------------------------

    /// <summary>Radios from the primary and extra frequencies, antennas at the visibility centre (or the airport).</summary>
    private void UpdateVoiceRadios()
    {
        if (_session.Info is not { } info) return;
        var sites = VoicePlan.Sites([info.Center], VoicePlan.FallbackSite(Radar.Sector, _profile.ActiveAirports));
        _voice.Update(VoicePlan.Radios(info.FrequencyKhz, VoicePlan.CanTransmit(info), _profile.Voice, sites.Count), sites);
    }

    private void OnVoiceChanged()
    {
        VoiceDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _voice.State switch
        {
            VoiceState.Connected => "SuccessBrush",
            VoiceState.Connecting => "AccentBrush",
            _ => _voice.IsActive ? "DangerBrush" : "MutedBrush",
        });
        VoiceButton.ToolTip = _voice.State switch
        {
            VoiceState.Connected => $"Voice: connected to {_voice.Server}",
            VoiceState.Connecting => "Voice: connecting…",
            _ when _voice.IsActive => "Voice: no connection" + (_voice.LastError.Length > 0 ? " — " + _voice.LastError : ""),
            _ => "Voice: frequencies, push-to-talk, audio",
        };
        PttButton.Visibility = _voice.State == VoiceState.Connected ? Visibility.Visible : Visibility.Collapsed;
        if (_voice.Transmitting)
        {
            TxBadge.SetResourceReference(Border.BackgroundProperty, "DangerBrush");
            TxText.Foreground = Brushes.White;
            TxText.FontWeight = FontWeights.Bold;
        }
        else
        {
            TxBadge.Background = Brushes.Transparent;
            TxText.ClearValue(TextBlock.ForegroundProperty);
            TxText.ClearValue(TextBlock.FontWeightProperty);
        }
        RxText.Text = _voice.Heard.Describe();
        _dirty = true;
    }

    private void OnVoiceClick(object sender, RoutedEventArgs e)
    {
        int oldPort = _profile.Voice.Port;
        var dialog = new VoiceWindow(_profile, _voice, _session.Info) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        SaveProfile();
        _voice.ApplySettings();
        UpdateVoiceRadios();
        if (!_profile.Voice.Enabled) _voice.Stop();
        else if (_session.IsConnected && _session.Info is { } info && (!_voice.IsActive || _profile.Voice.Port != oldPort)) _voice.Start(info);
    }

    private void OnPttDown(object sender, MouseButtonEventArgs e)
    {
        _voice.SetManualPtt(true);
        PttButton.CaptureMouse();
        e.Handled = true;
    }

    private void OnPttUp(object sender, MouseButtonEventArgs e)
    {
        _voice.SetManualPtt(false);
        if (PttButton.IsMouseCaptured) PttButton.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnPttLost(object sender, MouseEventArgs e) => _voice.SetManualPtt(false);

    // ---- messages ----------------------------------------------------------------------------------------

    private void OnMessage(AtcMessage m)
    {
        var theme = _profile.Theme;
        string color = m.IsError ? theme.Danger : m.Outgoing ? theme.MutedText
            : m.Channel switch { MessageChannel.Private => theme.Accent, MessageChannel.Server => theme.Success, MessageChannel.Broadcast => theme.Danger, _ => theme.Text };
        if (m.Channel == MessageChannel.Private && m.Peer != null)
        {
            AddLine(m.Peer, m.From, m.Text, color);
            if (!m.Outgoing) _sounds.Play(SoundEvent.PrivateMessage);
            return;
        }
        if (m.Channel == MessageChannel.Radio && !m.Outgoing) _sounds.Play(SoundEvent.RadioMessage);
        if (m.Channel == MessageChannel.Broadcast && !m.Outgoing)
        {
            // A broadcast or a call for a supervisor (.wallop): it must not go unnoticed.
            _sounds.Play(SoundEvent.PrivateMessage);
            OpenChat("Radio");
        }
        string from = m.Channel == MessageChannel.Radio && m.FrequencyKhz is { } f && !m.Outgoing ? $"{m.From} [{Frequency.Format(f)}]" : m.From;
        AddLine("Radio", from, m.Text, color);
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

    private void Info(string text) => AddLine("Radio", "Network-ATC", text, _profile.Theme.MutedText);
    private void Error(string text) => AddLine("Radio", "Network-ATC", text, _profile.Theme.Danger);

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
            if (_activeChat != "Radio" && !line.StartsWith('.'))
            {
                await _session.SendPrivateAsync(_activeChat, _commands.ExpandAlias(line));
                return;
            }
            // EuroScope plugins see a command first, as in EuroScope.
            if (line.StartsWith('.') && _es != null && await _es.CommandAsync(line)) return;
            var feedback = await _commands.ExecuteAsync(line);
            if (feedback != null) Info(feedback);
        }
        catch (InvalidOperationException ex)
        {
            Error(ex.Message);
        }
        if (Radar.Selected is { } t) ShowPlan(t);
    }

    // ---- menu ---------------------------------------------------------------------------------------------

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

    private static string PluginFolder => Path.Combine(Profile.DefaultDirectory, "plugins");

    private static MenuItem MenuEntry(string header, Action click, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>FILE: sectors, the EuroScope profile import and the recent sectors.</summary>
    private void OnFileClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FileButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuEntry("Open sector…", () => OnOpenSectorClick(this, new RoutedEventArgs()), "Ctrl+O"));
        menu.Items.Add(MenuEntry("Import EuroScope profile (.prf)…", ImportEuroScopeProfile));
        var recent = _profile.RecentSectors.Where(r => File.Exists(r.Path)).Take(8).ToList();
        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var r in recent)
                menu.Items.Add(MenuEntry($"{r.Name}  ·  {Path.GetFileName(r.Path)}", () =>
                {
                    _profile.ViewCenterLatitude = 0;
                    LoadSector(r.Path);
                    SaveProfile();
                }));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Exit", Close, "Alt+F4"));
        menu.IsOpen = true;
    }

    /// <summary>PLUGINS: loaded Network-ATC plugins, their errors and the map layers taken from EuroScope plugins.</summary>
    /// <summary>ATIS: the window of the controller's ATIS stations (it stays open while working).</summary>
    private void OnAtisClick(object sender, RoutedEventArgs e)
    {
        if (_atisWindow is { IsLoaded: true })
        {
            _atisWindow.Activate();
            return;
        }
        _atisWindow = new AtisWindow(_profile, _atis, SaveProfile) { Owner = this };
        _atisWindow.Closed += (_, _) => _atisWindow = null;
        _atisWindow.Show();
    }

    private void UpdateAtisButton()
    {
        int n = _atis.ConnectedCount;
        AtisDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, n > 0 ? "SuccessBrush" : "MutedBrush");
        AtisLabel.Text = n > 1 ? $"ATIS ×{n}" : "ATIS";
        RefreshWeather();
    }

    /// <summary>Letters changed from anywhere (.atis, the runways window, a new METAR): the ATIS on the air speaks the new one.</summary>
    private void CheckAtisLetters()
    {
        string now = string.Join(",", _profile.AtisLetters.OrderBy(kv => kv.Key).Select(kv => kv.Key + kv.Value));
        if (now == _atisLettersSeen) return;
        var before = _atisLettersSeen.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        _atisLettersSeen = now;
        foreach (var entry in now.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(e => !before.Contains(e)))
            _atis.Refresh(entry[..^1]);
    }

    private void OnPluginsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PluginsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (_plugins.Plugins.Count == 0 && _plugins.Errors.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No Network-ATC plugins", IsEnabled = false });
        foreach (var p in _plugins.Plugins)
            menu.Items.Add(new MenuItem { Header = $"{p.Plugin.Name}  {p.Plugin.Version}", IsEnabled = false });
        foreach (var err in _plugins.Errors)
            menu.Items.Add(new MenuItem { Header = $"✕ {Path.GetFileName(err.File)}: {err.Reason}", IsEnabled = false });
        if (_profile.ImportedPlugins.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "From EuroScope profile", IsEnabled = false });
            foreach (var name in _profile.ImportedPlugins)
                menu.Items.Add(new MenuItem { Header = "    " + name, IsEnabled = false });
        }
        AddEsPluginMenu(menu);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Open plugins folder", () =>
        {
            Directory.CreateDirectory(PluginFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PluginFolder) { UseShellExecute = true });
        }));
        menu.IsOpen = true;
    }

    /// <summary>
    /// Imports a whole EuroScope profile: sector, symbology, tags, settings, aliases, the first ASR and the data
    /// of TopSky, Ground Radar and CCAMS. The sector with the plugin maps is saved as .natc so the maps stay.
    /// </summary>
    private void ImportEuroScopeProfile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "EuroScope profile", Filter = "EuroScope profile (*.prf)|*.prf|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        NetworkAtc.Core.Import.EuroScopeImportResult result;
        try
        {
            result = NetworkAtc.Core.Import.EuroScopeImport.Import(dialog.FileName, _profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error($"Profile import: {ex.Message}");
            return;
        }
        try
        {
            var folder = Path.Combine(Profile.DefaultDirectory, "sectors");
            Directory.CreateDirectory(folder);
            result.SaveNativeSector(Path.Combine(folder, result.Source.Name + ".natc"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Report.Warn($"Sector with plugin maps not saved: {ex.Message}");
        }
        // The plugins of the imported profile belong to its sector; the ones in use until now stay with theirs.
        result.Profile.AssignPluginsTo(result.Profile.SectorFile, _profile);
        _profile = result.Profile;
        ApplyProfile();
        LoadSector(_profile.SectorFile);
        SaveProfile();
        SyncEsPlugins();
        _dirty = true;
        Info($"EuroScope profile \"{result.Source.Name}\" imported: {result.Report.Imported.Count()} imported, " +
             $"{result.Report.Skipped.Count()} skipped, {result.Report.Warnings.Count()} warnings");
        ShowImportReport(result.Source.Name, result.Report);
    }

    private void ShowImportReport(string name, NetworkAtc.Core.Import.ImportReport report)
    {
        string Section(string title, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            return list.Count == 0 ? "" : $"{title}\n" + string.Join("\n", list.Select(l => "  • " + l)) + "\n\n";
        }
        var text = Section("IMPORTED", report.Imported) + Section("WARNINGS", report.Warnings) + Section("SKIPPED", report.Skipped);
        var window = new Window
        {
            Title = $"EuroScope profile import — {name}",
            Owner = this,
            Width = 720,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)FindResource("AtcWindow"),
            Content = new TextBox
            {
                Text = text.TrimEnd(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14),
            },
        };
        window.Show();
    }

    private void OnOpenSectorClick(object sender, RoutedEventArgs e)
    {
        SaveProfile();
        var dialog = new SectorSelectWindow(_profile, Radar.Sector) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPath == null) return;
        _profile.ViewCenterLatitude = 0;
        LoadSector(dialog.SelectedPath);
        SaveProfile();
    }

    private void OnRunwaysClick(object sender, RoutedEventArgs e)
    {
        var dialog = new RunwaysWindow(_profile, Radar.Sector) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        SaveProfile();
        _es?.SendRunways();
        RefreshWeather();
        _ = _workspace.Weather.RefreshAsync();
        _dirty = true;
        if (Radar.Selected is { } t) ShowPlan(t);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SaveProfile();
        var dialog = new SettingsWindow(_profile.Clone(), _tagFields, _plugins, _protector, EsFunctionActions().ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _profile = dialog.Result;
        Radar.Profile = _profile;
        ApplyProfile();
        BuildLayerList();
        ApplyWindowLayouts();
        if (!string.Equals(_profile.SectorFile, _sectorPath, StringComparison.OrdinalIgnoreCase)) LoadSector(_profile.SectorFile);
        _session.LocalCallsign = _profile.Station.Callsign;
        _workspace.Weather.Enabled = _profile.FetchMetar;
        _workspace.UpdateOwnership();
        RefreshWeather();
        UpdateEsItemsInUse();
        _esGeometryDirty = true;
        SaveProfile();
    }

    // ---- keyboard ---------------------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool inText = Keyboard.FocusedElement is TextBox or PasswordBox;
        // The push-to-talk key (polled by the voice library) runs no shortcut; in a text box a typing key still types.
        if (PttKeys.Matches(PttBinding.Parse(_profile.Voice.PushToTalk), key))
        {
            if (!(inText && PttKeys.TypesText(key))) e.Handled = true;
            return;
        }
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
            case "ToggleAircraftList": ShowWindow("traffic"); return true;
            case "ToggleMessages": ShowWindow("messages"); return true;
            case "ToggleFlightPlan": ShowWindow("flightplan"); return true;
            case "ToggleDepartures": ShowWindow("departures"); return true;
            case "ToggleArrivals": ShowWindow("arrivals"); return true;
            case "ToggleConflicts": ShowWindow("conflicts"); return true;
            case "ToggleAtc": ShowWindow("atc"); return true;
            case "ToggleLayers": LayersButton.IsChecked = !LayersPopup.IsOpen; LayersPopup.IsOpen = !LayersPopup.IsOpen; return true;
            case "OpenSector": OnOpenSectorClick(this, new RoutedEventArgs()); return true;
            case "OpenSettings": OnSettingsClick(this, new RoutedEventArgs()); return true;
            case "Connect": OnConnectClick(this, new RoutedEventArgs()); return true;
            case "TrackSelected": if (Radar.Selected is { } t) AssumeOrRelease(t); return true;
            case "AssumeOrAccept":
                if (Radar.Selected is { } a) Coord(() => _session.AssumeAsync(a));
                else if (_session.Tracks.FirstOrDefault(x => _workspace.StateOf(x) == TrackState.TransferToMe) is { } offered)
                {
                    Radar.Select(offered);
                    Coord(() => _session.AcceptHandoffAsync(offered));
                }
                return true;
            case "HandoffNext":
                if (Radar.Selected is { } h)
                {
                    if (_workspace.NextController(h) is { } next) Coord(() => _session.HandoffAsync(h, next));
                    else ShowControllerMenu(h, cs => Coord(() => _session.HandoffAsync(h, cs)));
                }
                return true;
            case "ReleaseOrRefuse":
                if (Radar.Selected is { } r)
                    Coord(() => _workspace.StateOf(r) == TrackState.TransferToMe ? _session.RefuseHandoffAsync(r) : _session.ReleaseAsync(r));
                return true;
            case "ToggleRoute": if (Radar.Selected is { } rt) { rt.ShowRoute = !rt.ShowRoute; _dirty = true; } return true;
            case "ActiveRunways": OnRunwaysClick(this, new RoutedEventArgs()); return true;
            case "ToggleSil": ShowWindow("sil"); return true;
            case "ToggleSel": ShowWindow("sel"); return true;
            case "ClearSelection":
                // Esc: first drop the selection, then the measuring lines.
                if (Radar.Selected == null) Radar.ClearTools();
                Radar.Select(null);
                Radar.Focus();
                return true;
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
        foreach (var w in _windows.Values) SaveWindowLayout(w);
        try { _profile.Save(_profilePath); }
        catch (IOException e) { Error("Could not save profile: " + e.Message); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveProfile();
        StopEsPlugins();
        _plugins.ShutdownAll();
        _workspace.Dispose();
        _voice.Dispose();
        // Off the UI thread: the ATIS logoffs must not wait for this (blocked) thread.
        Task.Run(() => _atis.DisconnectAllAsync()).Wait(TimeSpan.FromSeconds(2));
        _session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }
}
