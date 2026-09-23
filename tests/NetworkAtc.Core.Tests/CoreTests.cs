using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Geo;
using NetworkAtc.Core.Plugins;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Tags;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Tests;

public class GeoTests
{
    [Fact]
    public void Projection_RoundTrips()
    {
        var p = new Projection(new GeoPoint(55.97, 37.41));
        var target = new GeoPoint(56.5, 38.2);
        var (x, y) = p.ToPlane(target);
        var back = p.FromPlane(x, y);
        Assert.Equal(target.Latitude, back.Latitude, 6);
        Assert.Equal(target.Longitude, back.Longitude, 6);
        // Distance from the center is preserved.
        Assert.Equal(GeoMath.DistanceNm(p.Center, target), Math.Sqrt(x * x + y * y), 1);
        Assert.True(x > 0 && y > 0);
    }

    [Fact]
    public void OffsetAndBearing_AreConsistent()
    {
        var a = new GeoPoint(55.97, 37.41);
        var b = GeoMath.Offset(a, 90, 30);
        Assert.Equal(30, GeoMath.DistanceNm(a, b), 1);
        Assert.Equal(90, GeoMath.BearingDeg(a, b), 0);
    }
}

public class SectorTests
{
    private static SectorFile Demo() => SectorParser.LoadFiles(Path.Combine(AppContext.BaseDirectory, "demo", "UUEE-demo.sct"));

    [Theory]
    [InlineData("N055.58.21.000", 55.9725)]
    [InlineData("E037.24.53.000", 37.41472)]
    [InlineData("S033.30.00.000", -33.5)]
    [InlineData("W000.30.00.000", -0.5)]
    [InlineData("55.5", 55.5)]
    public void ParsesCoordinates(string token, double expected)
    {
        Assert.True(SectorParser.TryParseCoordinate(token, out var d));
        Assert.Equal(expected, d, 4);
    }

    [Fact]
    public void ColorRef_IsBgr() => Assert.Equal("#FF8000", SectorParser.ColorFromColorRef(0x0080FF));

    [Fact]
    public void LoadsDemoSector()
    {
        var s = Demo();
        Assert.Equal("Network-ATC demo (UUEE)", s.Name);
        Assert.Equal("UUEE", s.DefaultAirport);
        Assert.Equal(55.9728, s.Center.Latitude, 3);
        Assert.Equal(2, s.Vors.Count);
        Assert.Equal(3, s.Airports.Count);
        Assert.Equal(5, s.Fixes.Count);
        Assert.Equal(3, s.Runways.Count);
        Assert.Equal("UUEE", s.Runways[0].Airport);
        Assert.Equal(4, s.Lines["ARTCC"].Count);
        Assert.All(s.Lines["ARTCC"], l => Assert.Equal("DEMO CTR", l.Name));   // continuation lines keep the name
        Assert.Equal(3, s.Lines["STAR"].Count);
        Assert.Equal("DEMO2A", s.Lines["STAR"][1].Name);
        // Named points ("SHR SHR") resolve to the VOR position.
        Assert.Equal(s.Vors[0].Position, s.Lines["SID"][0].From);
        Assert.NotNull(s.Lines["SID"][0].Color);
        Assert.Equal(8, s.Lines["GEO"].Count);
        Assert.Equal(6, s.Lines["GEO"].Count(l => l.Name == "Moscow ring"));
        Assert.Single(s.Regions);
        Assert.Equal(4, s.Regions[0].Points.Count);
        Assert.Equal("UUEE terminal", s.Regions[0].Name);
        Assert.Equal(2, s.Labels.Count);
        Assert.Empty(s.Warnings);

        // .ese
        Assert.Equal(5, s.Positions.Count);
        Assert.Equal(("UUEE_TWR", "131.500"), (s.Positions[2].Callsign, s.Positions[2].Frequency));
        Assert.Single(s.FreeTexts);
        Assert.Equal(2, s.SectorLines.Count);
        var tma = Assert.Single(s.Sectors);
        Assert.Equal(("UUEE_TMA", 0, 9500), (tma.Name, tma.Floor, tma.Ceiling));
        Assert.Equal(["EA", "DC"], tma.Owners);
    }

    [Fact]
    public void ReportsBrokenLines()
    {
        var s = SectorParser.ParseSct("[FIXES]\nBAD N055\n[SID]\nX UNKNOWN UNKNOWN N055.00.00.000 E037.00.00.000\n");
        Assert.Equal(2, s.Warnings.Count);
    }
}

public class TagTests
{
    private static Track Aircraft()
    {
        var t = new Track("AFL123") { AircraftType = "A20N" };
        t.Update(new PilotReport("AFL123", true, false, 4521, new GeoPoint(56, 37.5), 35000, 450, 271, false, 35000), DateTime.UtcNow);
        return t;
    }

    [Fact]
    public void RendersFieldsAndCollapsesEmptyOnes()
    {
        var fields = new TagFields();
        var t = Aircraft();
        var lines = TagTemplate.Parse("{callsign} {wtc}\n{fl}{vs} {cfl} {gs10}\n{scratch}").Render(t, fields);
        Assert.Equal(["AFL123 M", "F350 45"], lines);   // empty {cfl} collapsed, empty {scratch} line dropped

        t.ClearedAltitude = 5000;
        t.Scratchpad = "RWY24R";
        lines = TagTemplate.Parse("{fl} {cfl|---}\n{scratch}").Render(t, fields);
        Assert.Equal(["F350 A050", "RWY24R"], lines);
        t.ClearedAltitude = null;
        Assert.Equal(["F350 ---"], TagTemplate.Parse("{fl} {cfl|---}").Render(t, fields));
    }

    [Fact]
    public void PluginFieldsAndUnknownFields()
    {
        var fields = new TagFields();
        fields.Add("x", "test", _ => "42");
        fields.Add("boom", "broken", _ => throw new InvalidOperationException());
        Assert.Equal(["42 ? {nope}"], TagTemplate.Parse("{x} {boom} {nope}").Render(Aircraft(), fields));
    }
}

public class StcaTests
{
    private static Track At(string cs, double lat, double lon, int alt, double hdg, int gs = 250)
    {
        var t = new Track(cs);
        t.Update(new PilotReport(cs, true, false, 2000, new GeoPoint(lat, lon), alt, gs, hdg, false, alt), DateTime.UtcNow);
        return t;
    }

    [Fact]
    public void DetectsCurrentAndPredictedConflicts()
    {
        var stca = new Stca();
        var a = At("A", 56.0, 37.0, 10000, 90);
        var b = At("B", 56.0, 37.05, 10500, 270);        // ~1.7 NM apart, 500 ft
        var c = At("C", 56.0, 37.3, 10000, 270, 300);     // head-on with A, ~10 NM away
        var d = At("D", 56.0, 37.02, 15000, 90);          // close but vertically separated
        var conflicts = stca.Check([a, b, c, d]);
        Assert.Contains(conflicts, x => x.A == a && x.B == b && !x.Predicted);
        Assert.Contains(conflicts, x => (x.A == a && x.B == c || x.A == c && x.B == a) && x.Predicted);
        Assert.DoesNotContain(conflicts, x => x.A == d || x.B == d);
    }
}

public class CommandTests
{
    private static (CommandProcessor Cmd, AtcSession Session, Profile Profile, Func<Track?> Selected, List<Track> Sel) Create()
    {
        var session = new AtcSession();
        var profile = new Profile();
        var sel = new List<Track>();
        Func<Track?> selected = () => sel.FirstOrDefault();
        return (new CommandProcessor(session, () => profile, new PluginRegistry(), selected), session, profile, selected, sel);
    }

    [Fact]
    public async Task AnnotatesSelectedAircraft()
    {
        var (cmd, session, _, _, sel) = Create();
        Assert.Equal("Сначала выберите борт на радаре", await cmd.ExecuteAsync(".cfl 350"));
        session.OnPacket(null, FsdPacket.Parse("@N:AFL1:4101:1:55.9:37.4:5000:250:0:0")!);
        sel.Add(session.Find("AFL1")!);

        Assert.Equal("AFL1: CFL F350", await cmd.ExecuteAsync(".cfl 350"));
        Assert.Equal(35000, sel[0].ClearedAltitude);
        Assert.Equal("AFL1: CFL A045", await cmd.ExecuteAsync(".cfl A045"));
        Assert.Equal("AFL1: курс 270", await cmd.ExecuteAsync(".hdg 270"));
        // 4101 is in use by AFL1 itself, so the next free code is handed out.
        Assert.Equal("AFL1: код 4102", await cmd.ExecuteAsync(".sq"));
        Assert.Equal("Код — 4 цифры от 0 до 7", await cmd.ExecuteAsync(".sq 4181"));
        Assert.Equal("AFL1: на сопровождении", await cmd.ExecuteAsync(".track"));
        Assert.True(sel[0].IsTracked);
        Assert.StartsWith("Неизвестная команда", await cmd.ExecuteAsync(".nope"));
    }

    [Fact]
    public void ExpandsAliases()
    {
        var (cmd, _, _, _, _) = Create();
        Assert.Equal("contact UUEE_TWR on 118.1, good day", cmd.ExpandAlias(".ctc UUEE_TWR 118.1"));
        Assert.Equal("hello", cmd.ExpandAlias("hello"));
    }
}

public class ProfileTests
{
    [Fact]
    public void SavesAndLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"natc-{Guid.NewGuid():N}.json");
        var p = new Profile { Name = "Tower" };
        p.Theme = Theme.BuiltIn[2].Clone();
        p.Tags.Tracked = "{callsign}";
        p.Layers["SID"] = true;
        p.Station.Facility = Facility.Tower;
        p.Save(path);
        var q = Profile.Load(path);
        File.Delete(path);
        Assert.Equal("Tower", q.Name);
        Assert.Equal("Scope Green", q.Theme.Name);
        Assert.Equal("{callsign}", q.Tags.Tracked);
        Assert.True(q.IsLayerVisible("SID"));
        Assert.Equal(Facility.Tower, q.Station.Facility);
        Assert.Equal("Add", q.KeyBindings["ZoomIn"]);
    }

    [Fact]
    public void ThemeKeysAreEditable()
    {
        var t = new Theme();
        Assert.Contains("RadarBackground", Theme.ColorKeys);
        t.Set("Accent", "#123456");
        Assert.Equal("#123456", t.Get("Accent"));
    }
}

public class PluginTests
{
    [Fact]
    public async Task LoadsSamplePlugin_AndSkipsNativeDlls()
    {
        var root = Directory.CreateTempSubdirectory("natc-plugins").FullName;
        var pluginsDir = Path.Combine(root, "plugins");
        Directory.CreateDirectory(pluginsDir);
        var sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "examples", "SamplePlugin", "bin", Configuration, "net8.0", "SamplePlugin.dll"));
        Assert.True(File.Exists(sample), sample);
        File.Copy(sample, Path.Combine(pluginsDir, "SamplePlugin.dll"));
        File.WriteAllBytes(Path.Combine(pluginsDir, "EuroScopePlugin.dll"), [0x4D, 0x5A, 0, 0, 1, 2, 3]); // not .NET

        var session = new AtcSession();
        var fields = new TagFields();
        var registry = new PluginRegistry();
        var logs = new List<string>();
        registry.Log += (_, s) => logs.Add(s);
        Track? selected = null;
        var manager = new PluginManager(session, fields, registry, () => selected, Path.Combine(root, "data"));
        manager.LoadFrom(pluginsDir);

        var plugin = Assert.Single(manager.Plugins);
        Assert.Equal("Sample: distance", plugin.Plugin.Name);
        var error = Assert.Single(manager.Errors);
        Assert.EndsWith("EuroScopePlugin.dll", error.File);
        Assert.Contains(logs, l => l.Contains("загружен"));
        Assert.Single(registry.Overlays);
        Assert.Single(registry.AircraftActions);

        session.OnPacket(null, FsdPacket.Parse("@N:AFL1:2000:1:56.2728:37.4147:5000:250:0:0")!);
        selected = session.Find("AFL1");
        Assert.Equal(["18"], TagTemplate.Parse("{dist}").Render(selected!, fields));
        var cmd = new CommandProcessor(session, () => new Profile(), registry, () => selected);
        Assert.Equal("AFL1: 18.0 NM до UUEE", await cmd.ExecuteAsync(".dist"));

        registry.AircraftActions[0].Action(selected!);
        Assert.Equal("#F5A524", selected!.Highlight);
        try { Directory.Delete(root, true); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // On Windows the loaded plugin DLL stays locked until the process exits.
        }
    }

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif
}

public class DemoTests
{
    [Fact]
    public async Task DemoTraffic_ProducesTracksAndConflict()
    {
        var session = new AtcSession();
        var cmd = new CommandProcessor(session, () => new Profile(), new PluginRegistry(), () => null);
        Assert.StartsWith("Демо-трафик включён", await cmd.ExecuteAsync(".demo"));
        await Task.Delay(300);
        Assert.Equal(10, session.Tracks.Count);
        Assert.All(session.Tracks, t => Assert.True(t.HasFlightPlan));
        Assert.Contains(new Stca().Check(session.Tracks), c => c.A.Callsign.StartsWith("AFL900") || c.B.Callsign.StartsWith("AFL900"));
        Assert.Equal("Демо-трафик выключен", await cmd.ExecuteAsync(".demo"));
        Assert.Empty(session.Tracks);
    }
}
