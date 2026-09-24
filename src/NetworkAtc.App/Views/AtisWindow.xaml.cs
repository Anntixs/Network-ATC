using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetworkAtc.App.Services;
using NetworkAtc.Core.Atis;
using NetworkAtc.Core.Customization;

namespace NetworkAtc.App.Views;

/// <summary>
/// The controller's ATIS stations: airport, frequency, letter, text for pilots, voice (a Windows voice
/// reading the weather, or the controller's recording), connect and disconnect. Changes apply at once.
/// </summary>
public partial class AtisWindow : Window
{
    private readonly Profile _profile;
    private readonly AtisManager _atis;
    private readonly Action _save;
    private readonly AtisRecorder _recorder = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AtisSettings? _current;
    private TextBlock? _status, _preview, _speech, _letter, _recordInfo;
    private Button? _connect, _record;

    public AtisWindow(Profile profile, AtisManager atis, Action save)
    {
        InitializeComponent();
        (_profile, _atis, _save) = (profile, atis, save);
        _atis.Changed += OnAtisChanged;
        _atis.Service.LetterChanged += OnLetterChanged;
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _recorder.Dispose();
            _atis.Changed -= OnAtisChanged;
            _atis.Service.LetterChanged -= OnLetterChanged;
            _save();
        };
        RefreshList();
        if (StationList.Items.Count > 0) StationList.SelectedIndex = 0;
        else ShowEditor();
    }

    private void OnAtisChanged() => Dispatcher.BeginInvoke(() =>
    {
        RefreshList();
        UpdateStatus();
    });

    private void OnLetterChanged(object? sender, string airport) => Dispatcher.BeginInvoke(UpdateStatus);

    private void RefreshList()
    {
        var selected = _current;
        StationList.Items.Clear();
        foreach (var s in _profile.Atis)
            StationList.Items.Add(new ListBoxItem
            {
                Content = $"{(_atis.IsConnected(s) ? "● " : "○ ")}{s.Callsign}  {s.Frequency}",
                Tag = s,
            });
        if (selected != null)
            foreach (ListBoxItem item in StationList.Items)
                if (ReferenceEquals(item.Tag, selected)) StationList.SelectedItem = item;
        RemoveButton.IsEnabled = _current != null && !_atis.IsConnected(_current);
    }

    private void OnStationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (StationList.SelectedItem is not ListBoxItem { Tag: AtisSettings s } || ReferenceEquals(s, _current)) return;
        _current = s;
        ShowEditor();
        RemoveButton.IsEnabled = !_atis.IsConnected(s);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        string airport = _profile.ActiveAirports.FirstOrDefault(a => _profile.Atis.All(x => !x.Airport.Equals(a, StringComparison.OrdinalIgnoreCase)))
                         ?? _profile.ActiveAirports.FirstOrDefault() ?? "";
        var s = new AtisSettings { Airport = airport.ToUpperInvariant() };
        _profile.Atis.Add(s);
        _current = s;
        RefreshList();
        ShowEditor();
        _save();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_current == null || _atis.IsConnected(_current)) return;
        _profile.Atis.Remove(_current);
        _current = null;
        RefreshList();
        if (StationList.Items.Count > 0) StationList.SelectedIndex = 0;
        else ShowEditor();
        _save();
    }

    // ---- editor ---------------------------------------------------------------------------------

    private TextBlock Label(string text) => new() { Text = text, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 10, 0, 3) };

    private TextBox Field(string value, Action<string> changed, bool upper = false, bool mono = true, double width = double.NaN)
    {
        var box = new TextBox { Text = value, Width = width, Padding = new Thickness(6, 3, 6, 3), HorizontalAlignment = double.IsNaN(width) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
        if (upper) box.CharacterCasing = CharacterCasing.Upper;
        if (mono) box.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont");
        box.TextChanged += (_, _) =>
        {
            changed(box.Text);
            RefreshList();
            UpdatePreview();
        };
        box.LostFocus += (_, _) => _save();
        return box;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children)
        {
            if (c is FrameworkElement f) f.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(c);
        }
        return row;
    }

    private static StackPanel Column(params UIElement[] children)
    {
        var col = new StackPanel();
        foreach (var c in children) col.Children.Add(c);
        return col;
    }

    private void ShowEditor()
    {
        Editor.Children.Clear();
        if (_current is not { } s)
        {
            Editor.Children.Add(new TextBlock
            {
                Text = "Добавьте станцию ATIS: аэродром, частоту и голос. Подключается она после подключения к сети.",
                Style = (Style)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        // Station: airport, suffix, frequency, letter.
        _letter = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold, Width = 34, VerticalAlignment = VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush") };
        var next = new Button { Content = "Следующая", Margin = new Thickness(0, 0, 6, 0) };
        next.Click += (_, _) => { _atis.Service.SetLetter(s.Airport, "+"); _save(); };
        var letterBox = new TextBox { Width = 34, MaxLength = 1, CharacterCasing = CharacterCasing.Upper, Padding = new Thickness(6, 3, 6, 3), ToolTip = "Задать букву" };
        letterBox.TextChanged += (_, _) => { if (letterBox.Text.Length == 1) { _atis.Service.SetLetter(s.Airport, letterBox.Text); letterBox.Text = ""; _save(); } };
        var auto = new CheckBox { Content = "менять букву при новом METAR", IsChecked = s.AutoLetter, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        auto.Click += (_, _) => { s.AutoLetter = auto.IsChecked == true; _save(); };

        Editor.Children.Add(Row(
            Column(Label("Аэродром"), Field(s.Airport, v => s.Airport = v.Trim(), upper: true, width: 90)),
            Column(Label("Суффикс (A, D…)"), Field(s.Suffix, v => s.Suffix = v.Trim(), upper: true, width: 70)),
            Column(Label("Частота"), Field(s.Frequency, v => s.Frequency = v.Trim(), width: 100)),
            Column(Label("Название для голоса"), Field(s.SpokenName, v => s.SpokenName = v, mono: false, width: 200))));
        Editor.Children.Add(Label("Буква ATIS"));
        Editor.Children.Add(Row(_letter, next, letterBox, auto));

        // Text for pilots.
        Editor.Children.Add(Label("Текст для пилотов (переменные: $airport, $atiscode($airport), $metar($airport), $deprwy($airport), $arrrwy($airport), $time…)"));
        var text = new TextBox
        {
            Text = string.Join("\n", s.Text),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        text.TextChanged += (_, _) =>
        {
            s.Text = text.Text.Replace("\r", "").Split('\n').ToList();
            UpdatePreview();
        };
        text.LostFocus += (_, _) => _save();
        Editor.Children.Add(text);
        var reset = new Button { Content = "Текст по умолчанию", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        reset.Click += (_, _) => text.Text = string.Join("\n", AtisSettings.DefaultText);
        Editor.Children.Add(reset);
        Editor.Children.Add(Label("Дополнительно (NOTAM, работы на аэродроме) — в конце текста и голоса"));
        Editor.Children.Add(Field(s.Remark, v => s.Remark = v, mono: false));

        // Voice.
        Editor.Children.Add(Label("Голос в эфире"));
        var mode = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        mode.Items.Add("Без голоса (только текст)");
        mode.Items.Add("Моя запись");
        mode.Items.Add("Голос Windows читает погоду");
        mode.SelectedIndex = (int)s.Voice;
        var voicePanel = new StackPanel();
        mode.SelectionChanged += (_, _) =>
        {
            s.Voice = (AtisVoiceMode)mode.SelectedIndex;
            BuildVoicePanel(voicePanel, s);
            _atis.UpdateVoice(s);
            _save();
        };
        Editor.Children.Add(mode);
        Editor.Children.Add(voicePanel);
        BuildVoicePanel(voicePanel, s);

        // Preview and connection.
        Editor.Children.Add(Label("Пилоты получат"));
        _preview = new TextBlock { FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), TextWrapping = TextWrapping.Wrap };
        Editor.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(10, 8, 10, 8), Child = _preview });

        _connect = new Button { Style = (Style)FindResource("Primary"), MinWidth = 140 };
        _connect.Click += async (_, _) => await ToggleConnection(s);
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Muted") };
        Editor.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { _connect, new Border { Width = 12 }, _status } });

        UpdatePreview();
        UpdateStatus();
    }

    private void BuildVoicePanel(StackPanel panel, AtisSettings s)
    {
        panel.Children.Clear();
        _speech = null;
        _recordInfo = null;
        _record = null;
        if (s.Voice == AtisVoiceMode.Speech)
        {
            var language = new ComboBox { Width = 120 };
            language.Items.Add("Русский");
            language.Items.Add("English");
            language.SelectedIndex = s.Language == "en" ? 1 : 0;
            var voice = new ComboBox { Width = 260 };
            void FillVoices()
            {
                voice.Items.Clear();
                voice.Items.Add("по умолчанию");
                foreach (var v in AtisAudio.Voices(s.Language)) voice.Items.Add(v);
                voice.SelectedItem = voice.Items.Contains(s.SpeechVoice) ? s.SpeechVoice : "по умолчанию";
            }
            FillVoices();
            language.SelectionChanged += (_, _) =>
            {
                s.Language = language.SelectedIndex == 1 ? "en" : "ru";
                s.SpeechVoice = "";
                FillVoices();
                UpdatePreview();
                _atis.UpdateVoice(s);
                _save();
            };
            voice.SelectionChanged += (_, _) =>
            {
                s.SpeechVoice = voice.SelectedItem as string is { } name && name != "по умолчанию" ? name : "";
                _atis.UpdateVoice(s);
                _save();
            };
            var rate = new Slider { Minimum = -5, Maximum = 5, Value = s.SpeechRate, Width = 140, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            rate.ValueChanged += (_, _) => s.SpeechRate = (int)rate.Value;
            rate.LostMouseCapture += (_, _) => { _atis.UpdateVoice(s); _save(); };
            var listen = new Button { Content = "▶ Прослушать" };
            listen.Click += (_, _) =>
            {
                string text = _atis.Service.Speech(s);
                string lang = s.Language, name = s.SpeechVoice;
                int r = s.SpeechRate;
                Task.Run(() =>
                {
                    try { AtisAudio.Listen(AtisAudio.Speak(text, lang, name, r)); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                });
            };
            panel.Children.Add(Row(Column(Label("Язык"), language), Column(Label("Голос Windows"), voice), Column(Label("Скорость"), rate)));
            if (AtisAudio.Voices(s.Language).Count == 0)
                panel.Children.Add(new TextBlock
                {
                    Text = s.Language == "en"
                        ? "В Windows нет английского голоса: Параметры → Время и язык → Речь → Добавить голоса."
                        : "В Windows нет русского голоса: Параметры → Время и язык → Речь → Добавить голоса (Русский).",
                    Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });
            panel.Children.Add(Label("Голос скажет"));
            _speech = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(10, 8, 10, 8), Child = _speech });
            listen.Margin = new Thickness(0, 6, 0, 0);
            listen.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(listen);
        }
        else if (s.Voice == AtisVoiceMode.Recording)
        {
            _record = new Button { Content = "● Записать", MinWidth = 120 };
            _record.Click += (_, _) => ToggleRecording(s);
            var listen = new Button { Content = "▶ Прослушать" };
            listen.Click += (_, _) => AtisAudio.Listen(AtisAudio.LoadWav(s.RecordingFile));
            var open = new Button { Content = "Файл WAV…" };
            open.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Запись (*.wav)|*.wav" };
                if (dialog.ShowDialog(this) != true) return;
                s.RecordingFile = dialog.FileName;
                _atis.UpdateVoice(s);
                UpdateRecordInfo(s);
                _save();
            };
            _recordInfo = new TextBlock { Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = "Запишите ATIS своим голосом: «Шереметьево, информация Альфа…». Запись крутится в эфире по кругу. Перезапишите её при смене буквы или погоды.",
                Style = (Style)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 6),
            });
            panel.Children.Add(Row(_record, listen, open, _recordInfo));
            UpdateRecordInfo(s);
        }
    }

    private void ToggleRecording(AtisSettings s)
    {
        if (!_recorder.Recording)
        {
            try
            {
                _recorder.Start();
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
            {
                MessageBox.Show(this, "Микрофон недоступен: " + ex.Message, "ATIS");
                return;
            }
            _record!.Content = "■ Стоп";
            return;
        }
        var samples = _recorder.Stop();
        _record!.Content = "● Записать";
        if (samples.Length < AtisAudioInfo.MinSamples) return;
        s.RecordingFile = Path.Combine(Profile.DefaultDirectory, "atis", $"{s.Callsign}.wav");
        AtisAudio.SaveWav(s.RecordingFile, samples);
        _atis.UpdateVoice(s);
        UpdateRecordInfo(s);
        _save();
    }

    private void UpdateRecordInfo(AtisSettings s)
    {
        if (_recordInfo == null) return;
        var samples = AtisAudio.LoadWav(s.RecordingFile);
        _recordInfo.Text = samples.Length == 0 ? "записи нет" : $"запись {samples.Length / 48000.0:0} с · {Path.GetFileName(s.RecordingFile)}";
    }

    private async Task ToggleConnection(AtisSettings s)
    {
        _connect!.IsEnabled = false;
        try
        {
            if (_atis.IsConnected(s)) await _atis.DisconnectAsync(s);
            else await _atis.ConnectAsync(s);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NetworkAtc.Core.Fsd.FsdLoginException or IOException or System.Net.Sockets.SocketException)
        {
            MessageBox.Show(this, ex.Message, "ATIS");
        }
        finally
        {
            _connect.IsEnabled = true;
            _save();
            RefreshList();
            UpdateStatus();
        }
    }

    private void UpdatePreview()
    {
        if (_current is not { } s) return;
        if (_preview != null) _preview.Text = string.Join("\n", _atis.Service.Text(s));
        if (_speech != null) _speech.Text = _atis.Service.Speech(s);
    }

    private void UpdateStatus()
    {
        if (_current is not { } s) return;
        if (_letter != null)
        {
            string l = _atis.Service.Letter(s.Airport);
            _letter.Text = l.Length > 0 ? l : "—";
        }
        bool connected = _atis.IsConnected(s);
        if (_connect != null) _connect.Content = connected ? "Отключить ATIS" : "Подключить ATIS";
        if (_status != null) _status.Text = $"{s.Callsign}: {_atis.Status(s)}";
        UpdatePreview();
    }
}
