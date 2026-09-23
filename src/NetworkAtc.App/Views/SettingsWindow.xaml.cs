using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NetworkAtc.App.Services;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Plugins;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Tags;
using NetworkAtc.Plugins;

namespace NetworkAtc.App.Views;

public sealed class ColorEntry(string key, string value) : INotifyPropertyChanged
{
    private string _value = value;
    public string Key { get; } = key;
    public string Title { get; } = key;
    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));
        }
    }
    public Brush Brush => Paint.IsValid(Value) ? Paint.Brush(Value) : Brushes.Transparent;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class KeyEntry(string action, string title, string gesture) : INotifyPropertyChanged
{
    private string _gesture = gesture;
    public string Action { get; } = action;
    public string Title { get; } = title;
    public string Gesture
    {
        get => _gesture;
        set
        {
            _gesture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gesture)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record PluginEntry(string Title, string Details);

public partial class SettingsWindow : Window
{
    private static readonly Dictionary<string, string> ActionTitles = new()
    {
        ["ZoomIn"] = "Приблизить", ["ZoomOut"] = "Отдалить", ["CenterOnSector"] = "К центру сектора",
        ["FocusCommandLine"] = "Командная строка", ["ToggleAircraftList"] = "Боковая панель", ["ToggleMessages"] = "Сообщения",
        ["ToggleFlightPlan"] = "План полёта", ["ToggleLayers"] = "Слои", ["OpenSettings"] = "Настройки",
        ["Connect"] = "Подключение", ["TrackSelected"] = "Сопровождать выбранный", ["ClearSelection"] = "Снять выбор",
    };

    private readonly TagFields _fields;
    private readonly Track _sample;
    private List<ColorEntry> _colors = [];
    private readonly List<KeyEntry> _keys;

    public Profile Result { get; }

    public SettingsWindow(Profile profile, TagFields fields, PluginManager plugins, DpapiProtector protector)
    {
        InitializeComponent();
        Result = profile;
        _fields = fields;
        DataContext = profile;

        PresetBox.ItemsSource = Theme.BuiltIn;
        PresetBox.SelectedIndex = Math.Max(0, Theme.BuiltIn.ToList().FindIndex(t => t.Name == profile.Theme.Name));
        LoadColors(profile.Theme);

        FieldList.ItemsSource = fields.All.ToList();
        _sample = new Track("AFL1234") { AircraftType = "A20N", Scratchpad = "RWY24R", ClearedAltitude = 9000, AssignedHeading = 250 };
        _sample.Update(new PilotReport("AFL1234", true, false, 4101, new GeoPoint(56.1, 37.5), 12400, 285, 245, false, 12400), DateTime.UtcNow);
        _sample.ApplyFlightPlan(new FiledPlan("AFL1234", "I", "A20N", 450, "UUEE", "1200", "FL350", "ULLI", "ULLO", "/V/", "DCT"));
        UpdatePreview();

        _keys = ActionTitles.Select(a => new KeyEntry(a.Key, a.Value, profile.KeyBindings.GetValueOrDefault(a.Key, ""))).ToList();
        KeyList.ItemsSource = _keys;
        AliasBox.Text = string.Join(Environment.NewLine, profile.Aliases.Select(a => $"{a.Key} = {a.Value}"));

        var entries = plugins.Plugins.Select(p => new PluginEntry($"{p.Plugin.Name} {p.Plugin.Version}",
            $"{(p.Plugin.Author.Length > 0 ? p.Plugin.Author + " · " : "")}{Path.GetFileName(p.File)}")).ToList();
        entries.AddRange(plugins.Errors.Select(e => new PluginEntry("⚠ " + Path.GetFileName(e.File), e.Reason)));
        entries.AddRange(plugins.Registry.Commands.Values.Select(c => new PluginEntry("." + c.Name, $"{c.Description} · {c.Owner}")));
        if (entries.Count == 0) entries.Add(new PluginEntry("Плагинов нет", "Положите DLL плагина в папку plugins и перезапустите программу."));
        PluginList.ItemsSource = entries;
        Nav.SelectedIndex = 0;
    }

    private void LoadColors(Theme theme) =>
        ColorList.ItemsSource = _colors = Theme.ColorKeys.Select(k => new ColorEntry(k, theme.Get(k))).ToList();

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        var pages = new FrameworkElement[] { Page0, Page1, Page2, Page3, Page4, Page5, Page6, Page7 };
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnApplyPreset(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is Theme preset) LoadColors(preset);
    }

    private void OnImportTheme(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Тема (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { LoadColors(Profile.LoadTheme(dialog.FileName)); }
        catch (Exception ex) when (ex is IOException or JsonException) { ErrorText.Text = "Не удалось прочитать тему: " + ex.Message; }
    }

    private void OnExportTheme(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Тема (*.json)|*.json", FileName = Result.Theme.Name + ".json" };
        if (dialog.ShowDialog(this) != true) return;
        var theme = BuildTheme();
        if (theme != null) Profile.SaveTheme(theme, dialog.FileName);
    }

    private Theme? BuildTheme()
    {
        var bad = _colors.FirstOrDefault(c => !Paint.IsValid(c.Value));
        if (bad != null)
        {
            ErrorText.Text = $"Неверный цвет {bad.Key}: {bad.Value}. Формат #RRGGBB или #AARRGGBB.";
            return null;
        }
        var theme = Result.Theme.Clone();
        if (PresetBox.SelectedItem is Theme preset) theme.Name = preset.Name;
        foreach (var c in _colors) theme.Set(c.Key, c.Value.Trim());
        return theme;
    }

    private void OnTagChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (TagPreview == null || _sample == null) return;
        string Render(string template) => string.Join("\n", TagTemplate.Parse(template).Render(_sample, _fields));
        TagPreview.Text = $"{Render(Result.Tags.Untracked)}\n\n{Render(Result.Tags.Tracked)}\n\n{Render(Result.Tags.Detailed)}";
    }

    private void OnGestureKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: KeyEntry entry }) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key is Key.Tab) { e.Handled = false; return; }
        if (key is Key.Back or Key.Delete) { entry.Gesture = ""; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        entry.Gesture = KeyBindingParser.Format(key, Keyboard.Modifiers);
    }

    private void OnBrowseSector(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Сектор EuroScope (*.sct;*.sct2)|*.sct;*.sct2|Все файлы|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        Result.SectorFile = dialog.FileName;
        DataContext = null;
        DataContext = Result;
    }

    private void OnOpenPluginsFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Profile.DefaultDirectory, "plugins");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var theme = BuildTheme();
        if (theme == null) { Nav.SelectedIndex = 0; return; }
        if (Result.Panels.UiScale is < 0.5 or > 3) { ErrorText.Text = "Масштаб интерфейса 0.5–3"; Nav.SelectedIndex = 1; return; }
        if (Result.Tags.FontSize is < 6 or > 40) { ErrorText.Text = "Размер шрифта тегов 6–40"; Nav.SelectedIndex = 2; return; }

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in AliasBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0 || !line.StartsWith('.'))
            {
                ErrorText.Text = $"Алиас «{line}»: нужен формат .имя = текст";
                Nav.SelectedIndex = 6;
                return;
            }
            aliases[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        foreach (var k in _keys)
        {
            if (k.Gesture.Length > 0 && !KeyBindingParser.TryParse(k.Gesture, out _, out _))
            {
                ErrorText.Text = $"Клавиша для «{k.Title}» не распознана: {k.Gesture}";
                Nav.SelectedIndex = 5;
                return;
            }
        }

        Result.Theme = theme;
        Result.Aliases = aliases;
        Result.KeyBindings = _keys.Where(k => k.Gesture.Length > 0).ToDictionary(k => k.Action, k => k.Gesture, StringComparer.OrdinalIgnoreCase);
        DialogResult = true;
    }
}
