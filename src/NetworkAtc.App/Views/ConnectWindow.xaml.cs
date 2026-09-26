using System.Windows;
using System.Windows.Controls;
using NetworkAtc.App.Services;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Session;

namespace NetworkAtc.App.Views;

public partial class ConnectWindow : Window
{
    private sealed record PositionItem(AtcPosition Position)
    {
        public string Display => $"{Position.Callsign} — {Position.RadioName} {Position.Frequency}";
    }

    private static readonly string[] RatingNames = ["OBS", "S1", "S2", "S3", "C1", "C2", "C3", "I1", "I2", "I3", "SUP", "ADM"];
    private static readonly (Facility Value, string Title)[] Facilities =
    [
        (Facility.Observer, "Observer"), (Facility.Delivery, "DEL"), (Facility.Ground, "GND"), (Facility.Tower, "TWR"),
        (Facility.Approach, "APP/DEP"), (Facility.Centre, "CTR"), (Facility.FlightService, "FSS"),
        (Facility.Supervisor, "SUP"), (Facility.Administrator, "ADM"),
    ];

    private readonly Profile _profile;
    private readonly DpapiProtector _protector;

    public ConnectWindow(Profile profile, DpapiProtector protector, SectorFile? sector)
    {
        InitializeComponent();
        _profile = profile;
        _protector = protector;
        PositionBox.ItemsSource = sector?.Positions.Select(p => new PositionItem(p)).ToList();
        FacilityBox.ItemsSource = Facilities.Select(f => f.Title).ToList();
        RatingBox.ItemsSource = RatingNames;

        var st = profile.Station;
        CallsignBox.Text = st.Callsign;
        FrequencyBox.Text = st.Frequency;
        FacilityBox.SelectedIndex = Math.Max(0, Array.FindIndex(Facilities, f => f.Value == st.Facility));
        RatingBox.SelectedIndex = Math.Clamp(st.Rating - 1, 0, RatingNames.Length - 1);
        FacilityBox.SelectionChanged += OnFacilityChanged;
        RangeBox.Text = st.VisualRange.ToString();
        var cn = profile.Connection;
        ServerBox.Text = $"{cn.Host}:{cn.Port}";
        CidBox.Text = cn.Cid > 0 ? cn.Cid.ToString() : "";
        PasswordBox.Password = protector.Unprotect(cn.ProtectedPassword);
        NameBox.Text = cn.RealName;
        Loaded += (_, _) => CallsignBox.Focus();
    }

    private void OnPositionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PositionBox.SelectedItem is not PositionItem item) return;
        CallsignBox.Text = item.Position.Callsign;
        FrequencyBox.Text = item.Position.Frequency;
    }

    // SUP and ADM positions go with the matching rating; they see the whole network.
    private void OnFacilityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FacilityBox.SelectedIndex < 0) return;
        var f = Facilities[FacilityBox.SelectedIndex].Value;
        int min = f.MinimumRating();
        if (RatingBox.SelectedIndex + 1 < min) RatingBox.SelectedIndex = min - 1;
        if (min > 1) RangeBox.Text = "600";
    }

    private void OnCallsignChanged(object sender, TextChangedEventArgs e)
    {
        var f = AtcSession.FacilityFromCallsign(CallsignBox.Text);
        if (f != Facility.Observer) FacilityBox.SelectedIndex = Array.FindIndex(Facilities, x => x.Value == f);
        // Typical visibility ranges by facility.
        RangeBox.Text = f switch { Facility.Delivery or Facility.Ground => "20", Facility.Tower => "50", Facility.Approach => "150", Facility.Centre => "400", _ => RangeBox.Text };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string callsign = CallsignBox.Text.Trim().ToUpperInvariant();
        if (!AtcSession.IsValidCallsign(callsign)) { ErrorText.Text = "Callsign: 2–12 Latin letters, digits and _"; return; }
        if (!Frequency.TryParse(FrequencyBox.Text, out var khz)) { ErrorText.Text = "Frequency 118.000–136.975"; return; }
        if (!int.TryParse(RangeBox.Text, out var range) || range is < 1 or > 600) { ErrorText.Text = "Range 1–600 NM"; return; }
        var server = ServerBox.Text.Trim().Split(':');
        int port = 6809;
        if (server[0].Length == 0 || server.Length > 2 || server.Length == 2 && !int.TryParse(server[1], out port))
        {
            ErrorText.Text = "Server: host or host:port";
            return;
        }
        if (!int.TryParse(CidBox.Text.Trim(), out var cid) || cid <= 0) { ErrorText.Text = "Enter your CID"; return; }
        var facility = Facilities[Math.Max(0, FacilityBox.SelectedIndex)].Value;
        if (RatingBox.SelectedIndex + 1 < facility.MinimumRating())
        {
            ErrorText.Text = facility == Facility.Administrator ? "The ADM position needs the ADM rating" : "The SUP position needs the SUP or ADM rating";
            return;
        }

        var st = _profile.Station;
        st.Callsign = callsign;
        st.Frequency = Frequency.Format(khz);
        st.Facility = facility;
        st.Rating = RatingBox.SelectedIndex + 1;
        st.VisualRange = range;
        var cn = _profile.Connection;
        cn.Host = server[0];
        cn.Port = port;
        cn.Cid = cid;
        cn.ProtectedPassword = _protector.Protect(PasswordBox.Password);
        cn.RealName = NameBox.Text.Trim();
        DialogResult = true;
    }
}
