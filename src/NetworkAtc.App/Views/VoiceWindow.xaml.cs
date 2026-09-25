using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NetworkAtc.App.Services;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Voice;
using SkyNetwork.Voice;

namespace NetworkAtc.App.Views;

/// <summary>
/// Voice communication setup (EuroScope's voice dialog): the primary frequency and extra ones with
/// receive / transmit / volume, push-to-talk, audio devices and the voice server port.
/// </summary>
public partial class VoiceWindow : Window
{
    private sealed record DeviceItem(string Name, string Title);

    private sealed record FrequencyRow(Grid Row, TextBox Frequency, CheckBox Receive, CheckBox Transmit, Slider Volume);

    private readonly Profile _profile;
    private readonly VoiceService _voice;
    private readonly bool _canTransmit;
    private readonly List<FrequencyRow> _rows = [];
    private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private PttBinding _ptt;
    private CancellationTokenSource? _capture;

    public VoiceWindow(Profile profile, VoiceService voice, AtcConnectInfo? station)
    {
        InitializeComponent();
        _profile = profile;
        _voice = voice;
        var v = profile.Voice;

        int primary = station?.FrequencyKhz ?? (Frequency.TryParse(profile.Station.Frequency, out var khz) ? khz : 0);
        _canTransmit = station == null ? profile.Station.Facility != Facility.Observer : VoicePlan.CanTransmit(station);
        PrimaryText.Text = primary is >= 118000 and <= 136990 ? Frequency.Format(primary) : "—";
        PrimaryRx.IsChecked = v.PrimaryReceive;
        PrimaryTx.IsChecked = v.PrimaryTransmit;
        PrimaryTx.IsEnabled = _canTransmit;
        PrimaryVolume.Value = v.PrimaryVolume;
        if (!_canTransmit) ObserverText.Text = "Observer: receive only";
        foreach (var f in v.Frequencies) AddRow(f);

        var (inputs, outputs) = VoiceService.Devices();
        FillDevices(InputBox, inputs, v.InputDevice);
        FillDevices(OutputBox, outputs, v.OutputDevice);
        MicGainSlider.Value = v.MicGain;
        OutputVolumeSlider.Value = v.OutputVolume;
        _ptt = PttBinding.Parse(v.PushToTalk);
        ShowPtt();
        EnabledBox.IsChecked = v.Enabled;
        PortBox.Text = v.Port.ToString();

        _voice.Changed += ShowState;
        ShowState();
        _meter.Tick += (_, _) =>
            MicLevelBar.Width = Math.Clamp(_voice.MicLevel, 0, 1) * MicMeter.ActualWidth;
        _meter.Start();
        // While a push-to-talk key is being captured, keys must not press buttons (Enter = Apply).
        PreviewKeyDown += (_, e) => { if (_capture != null) e.Handled = true; };
        Closed += (_, _) =>
        {
            _capture?.Cancel();
            _meter.Stop();
            _voice.Changed -= ShowState;
        };
    }

    private void ShowState()
    {
        string server = _voice.Server;
        (StateText.Text, string brush) = _voice.State switch
        {
            VoiceState.Connected => ($"Connected to {server}", "SuccessBrush"),
            VoiceState.Connecting => ($"Connecting to {server}…", "AccentBrush"),
            _ when !_profile.Voice.Enabled => ("Off: enable \"Connect voice together with the network\" below", "MutedBrush"),
            _ when _voice.IsActive => ("No connection to the voice server" + (_voice.LastError.Length > 0 ? ": " + _voice.LastError : ""), "DangerBrush"),
            _ => ("Not connected: voice starts when you connect to the network", "MutedBrush"),
        };
        StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        ReconnectButton.IsEnabled = _voice.IsActive && _voice.State != VoiceState.Connecting;
    }

    private static void FillDevices(ComboBox box, IReadOnlyList<string> devices, string selected)
    {
        var items = new List<DeviceItem> { new("", "Default (Windows)") };
        items.AddRange(devices.Select(d => new DeviceItem(d, d)));
        // A saved device that is unplugged now: keep it, the default device is used meanwhile.
        if (selected.Length > 0 && VoicePlan.DeviceIndex(devices, selected) < 0) items.Add(new DeviceItem(selected, selected + " (not found)"));
        box.ItemsSource = items;
        box.SelectedItem = items.First(i => i.Name.Equals(selected, StringComparison.OrdinalIgnoreCase) || selected.Length == 0 && i.Name.Length == 0);
    }

    // ---- frequencies ------------------------------------------------------------------------------

    private void AddRow(VoiceFrequency f)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var w in new[] { 120.0, 46, 46, -1, 34 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        var freq = new TextBox { Text = f.Frequency, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 10, 0) };
        var rx = new CheckBox { IsChecked = f.Receive, VerticalAlignment = VerticalAlignment.Center };
        var tx = new CheckBox { IsChecked = f.Transmit && _canTransmit, IsEnabled = _canTransmit, VerticalAlignment = VerticalAlignment.Center };
        var volume = new Slider { Minimum = 0, Maximum = 1, Value = f.Volume, VerticalAlignment = VerticalAlignment.Center };
        var remove = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Remove frequency" };
        Grid.SetColumn(rx, 1);
        Grid.SetColumn(tx, 2);
        Grid.SetColumn(volume, 3);
        Grid.SetColumn(remove, 4);
        foreach (UIElement c in new UIElement[] { freq, rx, tx, volume, remove }) grid.Children.Add(c);
        var row = new FrequencyRow(grid, freq, rx, tx, volume);
        remove.Click += (_, _) =>
        {
            _rows.Remove(row);
            FrequencyRows.Children.Remove(grid);
            AddButton.IsEnabled = true;
        };
        _rows.Add(row);
        FrequencyRows.Children.Add(grid);
        AddButton.IsEnabled = _rows.Count < VoiceOptions.MaxExtraFrequencies;
    }

    private void OnAddFrequency(object sender, RoutedEventArgs e)
    {
        if (_rows.Count >= VoiceOptions.MaxExtraFrequencies) return;
        AddRow(new VoiceFrequency());
        _rows[^1].Frequency.Focus();
    }

    // ---- push-to-talk -------------------------------------------------------------------------------

    private void ShowPtt()
    {
        PttText.Text = _ptt.Describe();
        string? clash = PttKeys.ConflictingAction(_ptt, _profile.KeyBindings);
        PttWarning.Text = clash == null ? ""
            : $"This key is bound to the \"{clash}\" command: while it is the push-to-talk key, that command will not run. Reassign one of them.";
    }

    private async void OnCapturePtt(object sender, RoutedEventArgs e)
    {
        if (_capture != null)
        {
            _capture.Cancel();
            return;
        }
        using var cts = _capture = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CapturePttButton.Content = "Cancel";
        PttText.Text = "Press a key or joystick button… (Esc to cancel)";
        PttWarning.Text = "";
        try
        {
            var b = await PushToTalk.CaptureAsync(cts.Token);
            if (b.Kind != PttKind.None && !(b.Kind == PttKind.Keyboard && b.Code == 0x1B)) _ptt = b; // 0x1B: Esc
        }
        catch (OperationCanceledException)
        {
            // Cancelled or no key within 10 s: keep the old binding.
        }
        finally
        {
            _capture = null;
            CapturePttButton.Content = "Assign…";
            ShowPtt();
        }
    }

    private void OnClearPtt(object sender, RoutedEventArgs e)
    {
        _capture?.Cancel();
        _ptt = PttBinding.None;
        ShowPtt();
    }

    // ---- other --------------------------------------------------------------------------------------

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MicGainText != null && MicGainSlider != null) MicGainText.Text = $"{MicGainSlider.Value * 100:0} %";
        if (OutputVolumeText != null && OutputVolumeSlider != null) OutputVolumeText.Text = $"{OutputVolumeSlider.Value * 100:0} %";
    }

    private void OnReconnect(object sender, RoutedEventArgs e) => _voice.Reconnect();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ErrorText.Text = "Port 1–65535";
            return;
        }
        var frequencies = new List<VoiceFrequency>();
        var seen = new HashSet<int>();
        if (Frequency.TryParse(PrimaryText.Text, out var primary)) seen.Add(primary);
        foreach (var row in _rows)
        {
            string text = row.Frequency.Text.Trim();
            if (text.Length == 0) continue;
            if (!Frequency.TryParse(text, out var khz))
            {
                ErrorText.Text = $"Frequency \"{text}\": must be 118.000–136.975";
                row.Frequency.Focus();
                return;
            }
            if (!seen.Add(khz))
            {
                ErrorText.Text = $"Frequency {Frequency.Format(khz)} is already in the list";
                row.Frequency.Focus();
                return;
            }
            frequencies.Add(new VoiceFrequency
            {
                Frequency = Frequency.Format(khz),
                Receive = row.Receive.IsChecked == true,
                // An observer's TX boxes are disabled: keep what was saved for a later real position.
                Transmit = _canTransmit ? row.Transmit.IsChecked == true
                    : _profile.Voice.Frequencies.Any(f => f.Transmit && Frequency.TryParse(f.Frequency, out var k) && k == khz),
                Volume = Math.Round(row.Volume.Value, 2),
            });
        }

        var v = _profile.Voice;
        v.PrimaryReceive = PrimaryRx.IsChecked == true;
        if (_canTransmit) v.PrimaryTransmit = PrimaryTx.IsChecked == true;
        v.PrimaryVolume = Math.Round(PrimaryVolume.Value, 2);
        v.Frequencies = frequencies;
        v.InputDevice = (InputBox.SelectedItem as DeviceItem)?.Name ?? "";
        v.OutputDevice = (OutputBox.SelectedItem as DeviceItem)?.Name ?? "";
        v.MicGain = Math.Round(MicGainSlider.Value, 2);
        v.OutputVolume = Math.Round(OutputVolumeSlider.Value, 2);
        v.PushToTalk = _ptt.ToString();
        v.Enabled = EnabledBox.IsChecked == true;
        v.Port = port;
        DialogResult = true;
    }
}
