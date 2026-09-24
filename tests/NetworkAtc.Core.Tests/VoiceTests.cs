using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Voice;
using NetworkAtc.Plugins;
using SkyNetwork.Voice;

namespace NetworkAtc.Core.Tests;

public class VoicePlanTests
{
    private static AtcConnectInfo Station(string callsign, int khz, Facility facility, int rating = 3) =>
        new("127.0.0.1", 6809, 1000001, "pw", "Test", rating, callsign, khz, facility, 50, new GeoPoint(55.97, 37.41));

    [Fact]
    public void RealPositionTransmits_ObserverOnlyListens()
    {
        Assert.True(VoicePlan.CanTransmit(Station("UUEE_TWR", 118100, Facility.Tower)));
        Assert.False(VoicePlan.CanTransmit(Station("UUEE_OBS", 199998, Facility.Observer)));
        Assert.False(VoicePlan.CanTransmit(Station("UUEE_TWR", 118100, Facility.Tower, rating: 1)));   // OBS rating
        Assert.False(VoicePlan.CanTransmit(Station("UUEE_TWR", 199998, Facility.Tower)));             // no frequency
    }

    [Fact]
    public void RadiosArePrimaryThenExtras()
    {
        var options = new VoiceOptions
        {
            PrimaryVolume = 0.8,
            Frequencies =
            [
                new() { Frequency = "124.300", Receive = true, Transmit = true, Volume = 0.5 },
                new() { Frequency = "121,700", Receive = true },
                new() { Frequency = "118.100" },              // same as the primary: skipped
                new() { Frequency = "999" },                  // not a frequency: skipped
                new() { Frequency = "126.000", Receive = false, Transmit = false }, // switched off: skipped
            ],
        };
        var radios = VoicePlan.Radios(118100, canTransmit: true, options);
        Assert.Equal(
        [
            new Radio(118_100_000, true, true, 0.8f),
            new Radio(124_300_000, true, true, 0.5f),
            new Radio(121_700_000, true, false, 1f),
        ], radios);
    }

    [Fact]
    public void ObserverRadiosNeverTransmit()
    {
        var options = new VoiceOptions { Frequencies = [new() { Frequency = "124.300", Transmit = true }] };
        // 199.998 is the observer "frequency": no radio for it, the extra one is receive-only.
        var radios = VoicePlan.Radios(199998, canTransmit: false, options);
        Assert.Equal([new Radio(124_300_000, true, false, 1f)], radios);
    }

    [Fact]
    public void RadiosFitEightTransceivers()
    {
        var options = new VoiceOptions
        {
            Frequencies = Enumerable.Range(0, 9).Select(i => new VoiceFrequency { Frequency = Frequency.Format(120000 + i * 100) }).ToList(),
        };
        Assert.Equal(Protocol.MaxTransceivers, VoicePlan.Radios(118100, true, options).Count);
        Assert.Equal(Protocol.MaxTransceivers / 2, VoicePlan.Radios(118100, true, options, siteCount: 2).Count);
        Assert.Equal(118_100_000u, VoicePlan.Radios(118100, true, options, siteCount: 4)[0].FrequencyHz);
    }

    [Fact]
    public void SitesAtVisibilityCentres()
    {
        var sites = VoicePlan.Sites(
        [
            new GeoPoint(55.97, 37.41), new GeoPoint(55.9701, 37.4102), // near-duplicate: merged
            default,                                                     // unknown: skipped
            new GeoPoint(55.41, 37.90), new GeoPoint(56.0, 38.0), new GeoPoint(57.0, 39.0), new GeoPoint(58.0, 40.0),
        ], fallback: new GeoPoint(1, 1));
        Assert.Equal(VoicePlan.MaxSites, sites.Count);
        Assert.Equal(new AntennaSite(55.97, 37.41, VoicePlan.SiteAltitudeFeet), sites[0]);
        Assert.Equal(55.41, sites[1].Latitude);
        Assert.All(sites, s => Assert.Equal(100, s.AltitudeFeet));
    }

    [Fact]
    public void SiteFallsBackToAirportThenSectorCentre()
    {
        var sector = new SectorFile { Center = new GeoPoint(55.5, 37.5) };
        sector.Airports.Add(new NamedPoint("UUEE", new GeoPoint(55.97, 37.41)));
        sector.Airports.Add(new NamedPoint("UUDD", new GeoPoint(55.41, 37.90)));

        Assert.Equal(new GeoPoint(55.41, 37.90), VoicePlan.FallbackSite(sector, ["XXXX", "uudd"]));
        Assert.Equal(new GeoPoint(55.5, 37.5), VoicePlan.FallbackSite(sector, []));
        Assert.Null(VoicePlan.FallbackSite(null, ["UUEE"]));

        var sites = VoicePlan.Sites([default], VoicePlan.FallbackSite(sector, ["UUEE"]));
        Assert.Equal([new AntennaSite(55.97, 37.41, 100)], sites);
        Assert.Empty(VoicePlan.Sites([], null));
    }

    [Fact]
    public void SettingsFromProfile()
    {
        var options = new VoiceOptions { InputDevice = "USB Headset", OutputDevice = "Gone", MicGain = 9, OutputVolume = 0.5, PushToTalk = "key:163" };
        var s = VoicePlan.Settings(options, ["Built-in", "usb headset"], ["Speakers"]);
        Assert.Equal(1, s.InputDevice);
        Assert.Equal(-1, s.OutputDevice);   // unplugged: Windows default
        Assert.Equal(4f, s.MicGain);        // clamped
        Assert.Equal(0.5f, s.OutputVolume);
        Assert.Equal(new PttBinding(PttKind.Keyboard, 163), s.Ptt);
        Assert.Equal(-1, VoicePlan.DeviceIndex(["Built-in"], ""));
    }

    [Fact]
    public void HeardStationsTrackWhoIsTalking()
    {
        var heard = new HeardStations();
        Assert.True(heard.Set("AFL123", 118_100_000, true));
        Assert.False(heard.Set("AFL123", 118_100_000, true));
        Assert.True(heard.Set("SBI22", 124_300_000, true));
        Assert.Equal("RX 118.100 AFL123  RX 124.300 SBI22", heard.Describe());
        Assert.True(heard.Contains("afl123"));
        Assert.True(heard.Set("AFL123", 0, false));
        Assert.False(heard.Set("AFL123", 0, false));
        Assert.Equal("RX 124.300 SBI22", heard.Describe());
        heard.Clear();
        Assert.Equal("", heard.Describe());
    }
}

public class ProfileV4Tests
{
    [Fact]
    public void OldProfileGetsVoiceDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"natc-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "Version": 3, "Station": { "Callsign": "UUEE_TWR", "Frequency": "118.100" } }""");
        var p = Profile.Load(path);
        File.Delete(path);
        Assert.Equal(Profile.CurrentVersion, p.Version);
        Assert.True(p.Voice.Enabled);
        Assert.Equal(VoiceOptions.DefaultPort, p.Voice.Port);
        Assert.Equal(3782, p.Voice.Port);
        Assert.True(p.Voice.PrimaryReceive && p.Voice.PrimaryTransmit);
        Assert.Empty(p.Voice.Frequencies);
        Assert.Equal("", p.Voice.PushToTalk);
        Assert.Equal("UUEE_TWR", p.Station.Callsign);
    }

    [Fact]
    public void BrokenVoiceSectionIsRepaired()
    {
        var path = Path.Combine(Path.GetTempPath(), $"natc-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "Version": 4, "Voice": { "Port": 0, "Frequencies": null } }""");
        var p = Profile.Load(path);
        File.Delete(path);
        Assert.Equal(VoiceOptions.DefaultPort, p.Voice.Port);
        Assert.NotNull(p.Voice.Frequencies);
    }

    [Fact]
    public void VoiceSettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"natc-{Guid.NewGuid():N}.json");
        var p = new Profile();
        p.Voice.Port = 4000;
        p.Voice.PrimaryTransmit = false;
        p.Voice.Frequencies.Add(new VoiceFrequency { Frequency = "124.300", Receive = true, Transmit = true, Volume = 0.4 });
        p.Voice.InputDevice = "USB Headset";
        p.Voice.PushToTalk = new PttBinding(PttKind.Joystick, 4, 1).ToString();
        p.Save(path);
        var back = Profile.Load(path);
        File.Delete(path);
        Assert.Equal(4000, back.Voice.Port);
        Assert.False(back.Voice.PrimaryTransmit);
        var f = Assert.Single(back.Voice.Frequencies);
        Assert.Equal(("124.300", true, true, 0.4), (f.Frequency, f.Receive, f.Transmit, f.Volume));
        Assert.Equal("USB Headset", back.Voice.InputDevice);
        Assert.Equal(new PttBinding(PttKind.Joystick, 4, 1), PttBinding.Parse(back.Voice.PushToTalk));
    }
}
