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
        public string Format => Exists ? SectorLoader.Describe(Sector.Path).ToUpperInvariant() : "NO FILE";
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

    /// <summary>A EuroScope display file (.asr) to open instead of a sector: it names its own sector.</summary>
    public string? SelectedAsr { get; private set; }

    /// <summary>The sector is opened as a ground sector: the ground radar view of its airport.</summary>
    public bool OpenAsGround { get; private set; }

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
            ErrorText.Text = "File not found: " + path;
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
            ErrorText.Text = "Could not open sector: " + e.Message;
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
        if (Browse("Network-ATC sector", "Network-ATC sector (*.natc)|*.natc|All files|*.*") is { } path) Choose(path);
    }

    private void OnOpenEuroScope(object sender, RoutedEventArgs e)
    {
        if (Browse("EuroScope sector", "EuroScope sector (*.sct;*.sct2)|*.sct;*.sct2|All files|*.*") is { } path) Choose(path);
    }

    private void OnOpenAsr(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "EuroScope display", Filter = "EuroScope display (*.asr)|*.asr|All files|*.*" };
        if (_profile.RecentAsr.FirstOrDefault(File.Exists) is { } last) dialog.InitialDirectory = Path.GetDirectoryName(last);
        if (dialog.ShowDialog(this) != true) return;
        SelectedAsr = dialog.FileName;
        DialogResult = true;
    }

    private void OnOpenGround(object sender, RoutedEventArgs e)
    {
        if (Browse("Ground sector", "Sector (*.sct;*.sct2;*.natc)|*.sct;*.sct2;*.natc|All files|*.*") is not { } path) return;
        OpenAsGround = true;
        Choose(path);
        if (DialogResult != true) OpenAsGround = false;
    }

    private void OnDemo(object sender, RoutedEventArgs e) => Choose(DemoPath);

    private void OnOpenRecent(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem item) Choose(item.Sector.Path);
        else ErrorText.Text = "Pick a sector from the list or open a file.";
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
            Title = "Export to Network-ATC format",
            Filter = "Network-ATC sector (*.natc)|*.natc",
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
            ErrorText.Text = "Could not save: " + ex.Message;
        }
    }
}
