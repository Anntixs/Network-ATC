using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Sectors;

namespace NetworkAtc.App.Views;

public partial class SectorSelectWindow : Window
{
    private sealed record RecentItem(RecentSector Sector, bool Exists)
    {
        public string Name => string.IsNullOrWhiteSpace(Sector.Name) ? Path.GetFileNameWithoutExtension(Sector.Path) : Sector.Name;
        public string Format => Exists ? SectorLoader.Describe(Sector.Path).ToUpperInvariant() : "НЕТ ФАЙЛА";
        public string Details => $"{Sector.Path} · {Sector.LastUsed.ToLocalTime():dd.MM.yyyy HH:mm}";
    }

    private readonly Profile _profile;
    private readonly SectorFile? _current;

    public SectorSelectWindow(Profile profile, SectorFile? current)
    {
        InitializeComponent();
        _profile = profile;
        _current = current;
        ShowAtStartupBox.IsChecked = profile.ShowSectorSelection;
        ExportButton.IsEnabled = current != null;
        RefreshRecent();
        Closed += (_, _) => _profile.ShowSectorSelection = ShowAtStartupBox.IsChecked == true;
    }

    /// <summary>The chosen sector file, or null when the window was cancelled.</summary>
    public string? SelectedPath { get; private set; }

    public static string DemoPath => Path.Combine(AppContext.BaseDirectory, "demo", "UUEE-demo.natc");

    private void RefreshRecent()
    {
        var items = _profile.RecentSectors.Select(r => new RecentItem(r, File.Exists(r.Path))).ToList();
        RecentList.ItemsSource = items;
        RecentList.SelectedIndex = items.FindIndex(i => i.Exists);
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Choose(string path)
    {
        if (!File.Exists(path))
        {
            ErrorText.Text = "Файл не найден: " + path;
            return;
        }
        try
        {
            // Parse once up front so a broken file is reported here rather than as an empty radar.
            var sector = SectorLoader.Load(path);
            _profile.RememberSector(path, sector.Name);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ErrorText.Text = "Не удалось открыть сектор: " + e.Message;
            return;
        }
        SelectedPath = path;
        DialogResult = true;
    }

    private string? Browse(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        var last = _profile.RecentSectors.FirstOrDefault(r => File.Exists(r.Path));
        if (last != null) dialog.InitialDirectory = Path.GetDirectoryName(last.Path);
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OnOpenNative(object sender, RoutedEventArgs e)
    {
        if (Browse("Сектор Network-ATC", "Сектор Network-ATC (*.natc)|*.natc|Все файлы|*.*") is { } path) Choose(path);
    }

    private void OnOpenEuroScope(object sender, RoutedEventArgs e)
    {
        if (Browse("Сектор EuroScope", "Сектор EuroScope (*.sct;*.sct2)|*.sct;*.sct2|Все файлы|*.*") is { } path) Choose(path);
    }

    private void OnDemo(object sender, RoutedEventArgs e) => Choose(DemoPath);

    private void OnOpenRecent(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem item) Choose(item.Sector.Path);
        else ErrorText.Text = "Выберите сектор из списка или откройте файл.";
    }

    private void OnRecentDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem item) Choose(item.Sector.Path);
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item) return;
        _profile.RecentSectors.Remove(item.Sector);
        RefreshRecent();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт в формат Network-ATC",
            Filter = "Сектор Network-ATC (*.natc)|*.natc",
            FileName = string.Join("_", (_current.Name.Length > 0 ? _current.Name : "sector").Split(Path.GetInvalidFileNameChars())) + NativeSector.Extension,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            NativeSector.Save(_current, dialog.FileName);
            _profile.RememberSector(dialog.FileName, _current.Name);
            ErrorText.Text = "";
            RefreshRecent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = "Не удалось сохранить: " + ex.Message;
        }
    }
}
