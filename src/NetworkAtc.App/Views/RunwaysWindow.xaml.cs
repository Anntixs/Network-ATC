using System.Windows;
using System.Windows.Controls;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Sectors;

namespace NetworkAtc.App.Views;

/// <summary>EuroScope's "active airport/runway" dialog: active airports, departure/arrival runways and ATIS letters.</summary>
public partial class RunwaysWindow : Window
{
    private sealed class AirportRow
    {
        public required string Icao { get; init; }
        public required CheckBox Active { get; init; }
        public required TextBox Atis { get; init; }
        public List<(string Runway, CheckBox Departure, CheckBox Arrival)> Runways { get; } = [];
    }

    private readonly Profile _profile;
    private readonly List<AirportRow> _rows = [];

    public RunwaysWindow(Profile profile, SectorFile? sector)
    {
        InitializeComponent();
        _profile = profile;
        var airports = (sector?.Runways.Select(r => r.Airport.ToUpperInvariant()) ?? [])
            .Concat(profile.ActiveAirports.Select(a => a.ToUpperInvariant()))
            .Where(a => a.Length > 0)
            .Distinct()
            .OrderBy(a => profile.ActiveAirports.Contains(a, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(a => a)
            .ToList();
        if (airports.Count == 0)
            Body.Children.Add(new TextBlock { Text = "В секторе нет ВПП. Аэродромы можно задать командой .airport UUEE", Style = (Style)FindResource("Muted") });
        foreach (var icao in airports) AddAirport(icao, sector);
    }

    private void AddAirport(string icao, SectorFile? sector)
    {
        var use = _profile.ActiveRunways.GetValueOrDefault(icao) ?? new RunwayUse();
        var active = new CheckBox
        {
            Content = icao, FontWeight = FontWeights.SemiBold, FontSize = 14,
            IsChecked = _profile.ActiveAirports.Contains(icao, StringComparer.OrdinalIgnoreCase),
        };
        var atis = new TextBox { Width = 36, MaxLength = 1, CharacterCasing = CharacterCasing.Upper, Text = _profile.AtisLetters.GetValueOrDefault(icao, ""), Padding = new Thickness(4, 1, 4, 1) };
        var header = new DockPanel { Margin = new Thickness(0, 12, 0, 4) };
        var atisPanel = new StackPanel { Orientation = Orientation.Horizontal };
        atisPanel.Children.Add(new TextBlock { Text = "ATIS", Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        atisPanel.Children.Add(atis);
        DockPanel.SetDock(atisPanel, Dock.Right);
        header.Children.Add(atisPanel);
        header.Children.Add(active);
        Body.Children.Add(header);

        var row = new AirportRow { Icao = icao, Active = active, Atis = atis };
        var ends = sector?.Runways.Where(r => r.Airport.Equals(icao, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => new[] { r.Id1, r.Id2 }).Select(r => r.ToUpperInvariant()).Distinct().Order().ToList() ?? [];
        var grid = new Grid { Margin = new Thickness(22, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        for (int i = 0; i < ends.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var id = ends[i];
            var name = new TextBlock { Text = id, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), VerticalAlignment = VerticalAlignment.Center };
            var dep = new CheckBox { Content = "вылет", IsChecked = use.Departure.Contains(id, StringComparer.OrdinalIgnoreCase) };
            var arr = new CheckBox { Content = "посадка", IsChecked = use.Arrival.Contains(id, StringComparer.OrdinalIgnoreCase) };
            // Choosing a runway makes the airport active.
            dep.Checked += (_, _) => active.IsChecked = true;
            arr.Checked += (_, _) => active.IsChecked = true;
            Grid.SetRow(name, i);
            Grid.SetRow(dep, i);
            Grid.SetRow(arr, i);
            Grid.SetColumn(dep, 1);
            Grid.SetColumn(arr, 2);
            grid.Children.Add(name);
            grid.Children.Add(dep);
            grid.Children.Add(arr);
            row.Runways.Add((id, dep, arr));
        }
        Body.Children.Add(grid);
        _rows.Add(row);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var active = new List<string>();
        foreach (var row in _rows)
        {
            if (row.Active.IsChecked == true) active.Add(row.Icao);
            var use = new RunwayUse
            {
                Departure = row.Runways.Where(r => r.Departure.IsChecked == true).Select(r => r.Runway).ToList(),
                Arrival = row.Runways.Where(r => r.Arrival.IsChecked == true).Select(r => r.Runway).ToList(),
            };
            if (use.Departure.Count + use.Arrival.Count > 0) _profile.ActiveRunways[row.Icao] = use;
            else _profile.ActiveRunways.Remove(row.Icao);
            string letter = row.Atis.Text.Trim().ToUpperInvariant();
            if (letter.Length == 1 && letter[0] is >= 'A' and <= 'Z') _profile.AtisLetters[row.Icao] = letter;
            else _profile.AtisLetters.Remove(row.Icao);
        }
        // Keep airports typed with .airport that have no runways in this sector.
        foreach (var a in _profile.ActiveAirports)
            if (_rows.All(r => !r.Icao.Equals(a, StringComparison.OrdinalIgnoreCase))) active.Add(a);
        _profile.ActiveAirports = active;
        DialogResult = true;
    }
}
