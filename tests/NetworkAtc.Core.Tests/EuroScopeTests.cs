using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Plugins;
using NetworkAtc.Core.Radar;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Tags;
using NetworkAtc.Core.Weather;
using NetworkAtc.Plugins;
using Xunit;

namespace NetworkAtc.Core.Tests;

internal static class Demo
{
    public static string Path(string file) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "demo", file));

    public static SectorFile Sector() => SectorParser.LoadFiles(Path("UUEE-demo.sct"));

    public static void Pilot(AtcSession s, string cs, double lat, double lon, int alt, int squawk = 2000, int gs = 250, uint pbh = 0) =>
        s.OnPacket(null, FsdPacket.Parse(FormattableString.Invariant($"@N:{cs}:{squawk:0000}:1:{lat}:{lon}:{alt}:{gs}:{pbh}:0"))!);

    public static void Plan(AtcSession s, string cs, string dep, string dest, string route) =>
        s.OnPacket(null, FsdPacket.Parse($"$FP{cs}:*A:I:A320:450:{dep}:1200:0:FL350:{dest}:1:10:3:0:ULLO:/V/:{route}")!);
}

public class EseTests
{
    [Fact]
    public void ParsesProceduresSquawkRangesAndSectors()
    {
        var s = Demo.Sector();
        Assert.Equal(8, s.Procedures.Count);
        var sid = s.Procedures.First(p => p.Name == "DEMO1A");
        Assert.Equal((ProcedureKind.Sid, "UUEE", "24R"), (sid.Kind, sid.Airport, sid.Runway));
        Assert.Equal(["SHR", "DEMO5"], sid.Route);
        var app = s.Positions.First(p => p.Callsign == "UUEE_APP");
        Assert.Equal((4201, 4277), (app.SquawkStart, app.SquawkEnd));
        Assert.Equal(2, s.Sectors.Count);
    }

    [Fact]
    public void NativeFormatKeepsProceduresAndSquawks()
    {
        var s = NativeSector.Load(Demo.Path("UUEE-demo.natc"));
        Assert.Equal(8, s.Procedures.Count);
        Assert.Contains(s.Procedures, p => p is { Kind: ProcedureKind.Star, Name: "DEMO2A" } && p.Route[0] == "DEMO2");
        Assert.Equal(4301, s.Positions.First(p => p.Callsign == "DEMO_CTR").SquawkStart);
    }
}

public class AirspaceTests
{
    [Fact]
    public void ChainsBordersIntoPolygon()
    {
        var s = Demo.Sector();
        var tma = Airspace.Areas(s).First(a => a.Sector.Name == "UUEE_TMA");
        Assert.Equal(4, tma.Polygon.Count);
        Assert.True(tma.Contains(new GeoPoint(55.97, 37.41), 5000));
        Assert.False(tma.Contains(new GeoPoint(55.97, 37.41), 12000)); // above the ceiling
        Assert.False(tma.Contains(new GeoPoint(57.5, 37.41), 5000));
    }

    [Fact]
    public void OwnerIsFirstOnlinePositionOfTheList()
    {
        var s = Demo.Sector();
        var own = new SectorOwnership(s);
        var inTma = new GeoPoint(55.97, 37.41);
        own.Update([new OnlineStation("DEMO_CTR", "132.000")]);
        Assert.Equal("DEMO_CTR", own.OwnerAt(inTma, 5000));
        // Approach comes online: it is listed first for the TMA, the centre keeps the rest.
        own.Update([new OnlineStation("DEMO_CTR", "132.000"), new OnlineStation("UUEE_APP", "128.000")]);
        Assert.Equal("UUEE_APP", own.OwnerAt(inTma, 5000));
        Assert.Equal("DEMO_CTR", own.OwnerAt(inTma, 20000));
        // Same prefix/suffix and frequency counts as the position.
        own.Update([new OnlineStation("UUEE_1_APP", "128.000")]);
        Assert.Equal("UUEE_1_APP", own.OwnerAt(inTma, 5000));
        Assert.Null(own.OwnerAt(inTma, 20000));
    }

    [Fact]
    public void FindsNextController()
    {
        var s = Demo.Sector();
        var own = new SectorOwnership(s);
        own.Update([new OnlineStation("DEMO_CTR", "132.000"), new OnlineStation("UUEE_APP", "128.000")]);
        var session = new AtcSession();
        // Climbing out of the TMA northbound at 6000 ft, cleared FL150: next is the centre.
        Demo.Pilot(session, "AFL1", 56.2, 37.4, 6000, gs: 300);
        var t = session.Find("AFL1")!;
        t.ClearedAltitude = 15000;
        Assert.Equal("DEMO_CTR", own.NextController(t, "UUEE_APP"));
        Assert.True(own.IsInOrEntering(t, "UUEE_APP"));
    }
}

public class ProcedureTests
{
    private static (ProcedureAssigner, AtcSession, Profile) Create()
    {
        var sector = Demo.Sector();
        var profile = new Profile();
        profile.ActiveRunways["UUEE"] = new RunwayUse { Departure = ["24R"], Arrival = ["24L"] };
        return (new ProcedureAssigner(() => sector, () => profile.ActiveRunways), new AtcSession(), profile);
    }

    [Fact]
    public void SuggestsSidAndStarFromRouteAndRunway()
    {
        var (proc, session, profile) = Create();
        Demo.Pilot(session, "AFL1", 55.97, 37.41, 0);
        Demo.Plan(session, "AFL1", "UUEE", "ULLI", "N0450F350 DEMO4 DM100");
        Demo.Pilot(session, "SBI2", 56.3, 37.7, 12000);
        Demo.Plan(session, "SBI2", "ULLI", "UUEE", "DM100 DEMO2");
        var dep = session.Find("AFL1")!;
        var arr = session.Find("SBI2")!;
        Assert.Equal("24R", proc.DepartureRunway(dep));
        Assert.Equal("DEMO4A", proc.Sid(dep));
        Assert.Equal("DEMO2A", proc.Star(arr));
        // Runway change switches to that runway's procedures.
        profile.ActiveRunways["UUEE"] = new RunwayUse { Departure = ["06L"], Arrival = ["06R"] };
        Assert.Equal("DEMO4B", proc.Sid(dep));
        Assert.Equal("DEMO2B", proc.Star(arr));
        // A manual assignment wins.
        dep.Sid = "DEMO1B";
        Assert.Equal("DEMO1B", proc.Sid(dep));
    }

    [Fact]
    public void ResolvesRouteThroughSidAndStar()
    {
        var (proc, session, _) = Create();
        Demo.Pilot(session, "AFL1", 55.97, 37.41, 0);
        Demo.Plan(session, "AFL1", "UUEE", "UUEE", "DEMO5/N0300A060 DCT DEMO2");
        var names = proc.ResolveRoute(session.Find("AFL1")!).Select(p => p.Name).ToList();
        Assert.Equal(["UUEE", "SHR", "DEMO5", "DEMO2", "DEMO1", "SHR", "UUEE"], names);
        Assert.Equal(["SHR", "DCT", "DEMO5", "UM100"], ProcedureAssigner.Tokens("N0450F350 SHR DCT DEMO5/N0440F360 UM100"));
    }
}

public class CoordinationTests
{
    private static AtcSession Session(out List<CoordinationEvent> events)
    {
        var s = new AtcSession { LocalCallsign = "UUEE_APP" };
        var list = new List<CoordinationEvent>();
        s.Coordination += (_, e) => list.Add(e);
        events = list;
        Demo.Pilot(s, "AFL1", 56.0, 37.4, 8000);
        return s;
    }

    [Fact]
    public async Task AssumeReleaseAndOwnershipFromOthers()
    {
        var s = Session(out var events);
        var t = s.Find("AFL1")!;
        Assert.Null(await s.AssumeAsync(t));
        Assert.True(t.IsTracked);
        Assert.Equal("UUEE_APP", t.Owner);
        Assert.Equal(TrackState.Assumed, TrackStates.Of(t, s.Me, false));
        Assert.Null(await s.ReleaseAsync(t));
        Assert.Equal("", t.Owner);

        // Another controller assumes it: we see the owner and cannot assume it ourselves.
        s.OnPacket(null, FsdPacket.Parse("$CQDEMO_CTR:@94835:IT:AFL1")!);
        Assert.Equal("DEMO_CTR", t.Owner);
        Assert.False(t.IsTracked);
        Assert.Equal(TrackState.Redundant, TrackStates.Of(t, s.Me, true));
        Assert.Equal("AFL1 на сопровождении у DEMO_CTR", await s.AssumeAsync(t));
        Assert.Contains(events, e => e.Kind == CoordinationKind.OwnerChanged && e.Peer == "DEMO_CTR");
        s.OnPacket(null, FsdPacket.Parse("$CQDEMO_CTR:@94835:DR:AFL1")!);
        Assert.Equal("", t.Owner);
    }

    [Fact]
    public async Task IncomingHandoffCanBeAcceptedOrRefused()
    {
        var s = Session(out var events);
        var t = s.Find("AFL1")!;
        s.OnPacket(null, FsdPacket.Parse("$CQDEMO_CTR:@94835:IT:AFL1")!);
        s.OnPacket(null, FsdPacket.Parse("$HODEMO_CTR:UUEE_APP:AFL1")!);
        Assert.Equal(TrackState.TransferToMe, TrackStates.Of(t, s.Me, true));
        Assert.Equal(CoordinationKind.HandoffRequested, events[^1].Kind);
        Assert.Null(await s.RefuseHandoffAsync(t));
        Assert.False(t.HandoffPending);
        Assert.Equal("DEMO_CTR", t.Owner);

        s.OnPacket(null, FsdPacket.Parse("$HODEMO_CTR:UUEE_APP:AFL1")!);
        // Assume on an offered aircraft accepts the offer.
        Assert.Null(await s.AssumeAsync(t));
        Assert.True(t.IsTracked);
        Assert.False(t.HandoffPending);
    }

    [Fact]
    public async Task OutgoingHandoffIsAcceptedOrRefusedByTheOtherSide()
    {
        var s = Session(out var events);
        var t = s.Find("AFL1")!;
        await s.AssumeAsync(t);
        Assert.Null(await s.HandoffAsync(t, "DEMO_CTR"));
        Assert.Equal(TrackState.TransferFromMe, TrackStates.Of(t, s.Me, true));
        s.OnPacket(null, FsdPacket.Parse("#PCDEMO_CTR:UUEE_APP:CCP:HC:AFL1")!);
        Assert.Equal(CoordinationKind.HandoffRefused, events[^1].Kind);
        Assert.True(t.IsTracked);

        await s.HandoffAsync(t, "DEMO_CTR");
        s.OnPacket(null, FsdPacket.Parse("$HADEMO_CTR:UUEE_APP:AFL1")!);
        Assert.Equal(CoordinationKind.HandoffAccepted, events[^1].Kind);
        Assert.Equal("DEMO_CTR", t.Owner);
        Assert.False(t.IsTracked);
        Assert.False(t.HandoffPending);
    }

    [Fact]
    public void SharedAnnotationsAndPointOuts()
    {
        var s = Session(out var events);
        var t = s.Find("AFL1")!;
        s.OnPacket(null, FsdPacket.Parse("$CQDEMO_CTR:@94835:TA:AFL1:12000")!);
        s.OnPacket(null, FsdPacket.Parse("$CQDEMO_CTR:@94835:BC:AFL1:4301")!);
        s.OnPacket(null, FsdPacket.Parse("$CQDEMO_CTR:@94835:SC:AFL1:direct DEMO5")!);
        s.OnPacket(null, FsdPacket.Parse("$CQDEMO_CTR:@94835:SID:AFL1:DEMO1A")!);
        s.OnPacket(null, FsdPacket.Parse("$CQDEMO_CTR:@94835:CLR:AFL1:1")!);
        Assert.Equal((12000, 4301, "direct DEMO5", "DEMO1A", true),
            (t.ClearedAltitude, t.AssignedSquawk, t.Scratchpad, t.Sid, t.ClearanceReceived));
        s.OnPacket(null, FsdPacket.Parse("#PCDEMO_CTR:UUEE_APP:CCP:PT:AFL1")!);
        Assert.Equal(new CoordinationEvent(CoordinationKind.PointOut, t, "DEMO_CTR"), events[^1]);
        // A controller leaving frees its aircraft.
        s.OnPacket(null, FsdPacket.Parse("$CQDEMO_CTR:@94835:IT:AFL1")!);
        s.OnPacket(null, FsdPacket.Parse("#DADEMO_CTR:1000004")!);
        Assert.Equal("", t.Owner);
    }

    [Fact]
    public void BuildsEuroScopePackets()
    {
        Assert.Equal("$CQUUEE_APP:@94835:TA:AFL1:12000", AtcPackets.Shared("UUEE_APP", "TA", "AFL1", "12000"));
        Assert.Equal("$HOUUEE_APP:DEMO_CTR:AFL1", AtcPackets.Handoff("UUEE_APP", "DEMO_CTR", "AFL1"));
        Assert.Equal("#PCUUEE_APP:DEMO_CTR:CCP:PT:AFL1", AtcPackets.Coordination("UUEE_APP", "DEMO_CTR", "PT", "AFL1"));
        var fp = new FiledPlan("AFL1", "I", "A320", 450, "UUEE", "1200", "FL350", "ULLI", "ULLO", "/V/", "DEMO5 DM100", "1", "10", "3", "0");
        Assert.Equal("$AMUUEE_APP:SERVER:AFL1:I:A320:450:UUEE:1200:0:FL350:ULLI:1:10:3:0:ULLO:/V/:DEMO5 DM100", AtcPackets.Amend("UUEE_APP", fp));
        Assert.Equal(["$CRUUEE_APP:AFL1:ATIS:T:line one", "$CRUUEE_APP:AFL1:ATIS:E:2"], AtcPackets.AtisReply("UUEE_APP", "AFL1", ["line one"]));
    }
}

public class WarningTests
{
    [Fact]
    public void DetectsDuplicateWrongSquawkClamAndEmergency()
    {
        var s = new AtcSession();
        Demo.Pilot(s, "AFL1", 56.0, 37.4, 8000, squawk: 4201);
        Demo.Pilot(s, "SBI2", 56.1, 37.4, 9000, squawk: 4201);
        Demo.Pilot(s, "UTA3", 56.2, 37.4, 9000, squawk: 7700);
        Demo.Pilot(s, "AFL4", 56.3, 37.4, 9000, squawk: 2000);
        Demo.Pilot(s, "AFL5", 56.4, 37.4, 9000, squawk: 2000);
        var a = s.Find("AFL1")!;
        Assert.True(Warnings.Of(a, s.Tracks).HasFlag(TrackWarning.Duplicate));
        Assert.Equal(TrackWarning.None, Warnings.Of(s.Find("AFL4")!, s.Tracks)); // 2000 is not discrete
        Assert.Equal(TrackWarning.Emergency, Warnings.Of(s.Find("UTA3")!, s.Tracks));
        a.AssignedSquawk = 4202;
        Assert.True(Warnings.Of(a, s.Tracks).HasFlag(TrackWarning.WrongSquawk));
        a.ClearedAltitude = 6000; // 2000 ft above the CFL and level
        Assert.True(Warnings.IsClam(a, 300));
        Assert.Equal("DUPE SQ CLAM", Warnings.Text(a, Warnings.Of(a, s.Tracks)));
        a.ClearedAltitude = 8200;
        Assert.False(Warnings.IsClam(a, 300));
    }
}

public class MetarTests
{
    [Fact]
    public void ParsesWindAndPressure()
    {
        var m = MetarParser.Parse("UUEE 240930Z 24012G22MPS 9999 BKN020 05/02 Q1013 R24L/290050 NOSIG")!;
        Assert.Equal(("UUEE", 240, 23, 43), (m.Station, m.WindDirection, m.WindSpeed, m.Gust));
        Assert.Equal(1013, m.Qnh);
        Assert.Equal("240/23G43", m.Wind);
        var us = MetarParser.Parse("METAR KJFK 240951Z VRB03KT 10SM FEW250 22/13 A2992 RMK AO2 SLP132")!;
        Assert.True(us.VariableWind);
        Assert.Equal("VRB03", us.Wind);
        Assert.Equal(1013, us.Qnh);
        Assert.Equal("29.92", us.Altimeter);
        Assert.Null(MetarParser.Parse("garbage"));
    }
}

public class EuroScopeCommandTests
{
    private static (CommandProcessor, AtcSession, Profile, Workspace, List<Track>) Create()
    {
        var session = new AtcSession { LocalCallsign = "UUEE_APP" };
        var profile = new Profile();
        profile.Station.Frequency = "128.000";
        var sel = new List<Track>();
        var ws = new Workspace(session, () => profile);
        ws.SetSector(Demo.Sector());
        var cmd = new CommandProcessor(session, () => profile, new PluginRegistry(), () => sel.FirstOrDefault()) { Workspace = ws };
        return (cmd, session, profile, ws, sel);
    }

    [Fact]
    public async Task RunwaysSidsAndSquawksFromPositionRange()
    {
        var (cmd, session, profile, ws, sel) = Create();
        Assert.Equal("UUEE: вылет 24R, посадка 24L", await cmd.ExecuteAsync(".rwy uuee 24R 24L"));
        Assert.Contains("UUEE", profile.ActiveAirports);
        Assert.StartsWith("UUEE: ВПП", await cmd.ExecuteAsync(".rwy UUEE 99X"));
        Demo.Pilot(session, "AFL1", 55.97, 37.41, 0, squawk: 2000);
        Demo.Plan(session, "AFL1", "UUEE", "ULLI", "DEMO5 DM100");
        sel.Add(session.Find("AFL1")!);
        Assert.Equal("DEMO1A", ws.ProcedureOf(sel[0]));
        Assert.Equal("24R", ws.RunwayOf(sel[0]));
        Assert.Equal("AFL1: SID DEMO4A", await cmd.ExecuteAsync(".sid demo4a"));
        Assert.Equal("SID NOPE нет в секторе", await cmd.ExecuteAsync(".sid NOPE"));
        Assert.Equal("AFL1: SID автоматически (DEMO1A)", await cmd.ExecuteAsync(".sid"));
        // UUEE_APP's own range from the .ese is 4201-4277.
        Assert.Equal("AFL1: код 4201", await cmd.ExecuteAsync(".sq"));
        Assert.Equal("AFL1: разрешение получено", await cmd.ExecuteAsync(".clr"));
        Assert.True(sel[0].ClearanceReceived);
        Assert.Equal("AFL1: на сопровождении", await cmd.ExecuteAsync(".assume"));
        Assert.Equal("AFL1: передача DEMO_CTR", await cmd.ExecuteAsync(".ho DEMO_CTR"));
        Assert.Equal("DEMO_CTR", sel[0].HandoffTo);
        Assert.Equal("AFL1: передача отменена", await cmd.ExecuteAsync(".hc"));
    }

    [Fact]
    public async Task AtisLettersAndAliasVariables()
    {
        var (cmd, session, profile, ws, sel) = Create();
        profile.ActiveAirports = ["UUEE"];
        Assert.Equal("UUEE: информация A", await cmd.ExecuteAsync(".atis UUEE +"));
        Assert.Equal("UUEE: информация B", await cmd.ExecuteAsync(".atis UUEE +"));
        ws.Weather.Set(MetarParser.Parse("UUEE 240930Z 24005KT CAVOK 10/02 Q1017")!);
        Demo.Pilot(session, "AFL1", 55.97, 37.41, 3000, squawk: 4201);
        Demo.Plan(session, "AFL1", "UUEE", "ULLI", "DEMO5 DM100");
        sel.Add(session.Find("AFL1")!);
        profile.Aliases[".tkof"] = "$aircraft wind $wind($myairport) runway $deprwy cleared for takeoff, QNH $qnh(UUEE) info $atiscode(UUEE)";
        profile.ActiveRunways["UUEE"] = new RunwayUse { Departure = ["24R"], Arrival = ["24L"] };
        Assert.Equal("AFL1 wind 240/05 runway 24R cleared for takeoff, QNH 1017 info B", cmd.ExpandAlias(".tkof"));
        Assert.Equal("$unknown stays", cmd.ExpandVariables("$unknown stays"));
        profile.ControllerInfo = ["$mycallsign $myfreq", "", "ATIS $atiscode(UUEE)"];
        Assert.Equal(["UUEE_APP 128.000", "ATIS B"], cmd.ControllerInfoLines());
    }

    [Fact]
    public async Task SeparationTool()
    {
        var (cmd, session, _, _, _) = Create();
        // Head-on at the same level, 10 NM apart, 300 kt each: they meet in a minute.
        Demo.Pilot(session, "AFL1", 56.0, 37.0, 10000, gs: 300, pbh: Pbh.Encode(0, 0, 90, false));
        Demo.Pilot(session, "SBI2", 56.0, 37.2985, 10000, gs: 300, pbh: Pbh.Encode(0, 0, 270, false));
        var result = await cmd.ExecuteAsync(".sep AFL1 SBI2");
        Assert.StartsWith("AFL1 → SBI2: 10.0 NM, пеленг 090°", result);
        Assert.Contains("минимум 0.", result);
    }
}

public class TagFieldEsTests
{
    [Fact]
    public async Task CoordinationFieldsRender()
    {
        var session = new AtcSession { LocalCallsign = "UUEE_APP" };
        var profile = new Profile { ActiveAirports = ["UUEE"] };
        profile.ActiveRunways["UUEE"] = new RunwayUse { Departure = ["24R"], Arrival = ["24L"] };
        var ws = new Workspace(session, () => profile);
        ws.SetSector(Demo.Sector());
        var fields = new TagFields { Workspace = ws };
        Demo.Pilot(session, "AFL1", 56.0, 37.4, 6000, squawk: 7700);
        Demo.Plan(session, "AFL1", "UUEE", "ULLI", "DEMO5 DM100");
        var t = session.Find("AFL1")!;
        await session.AssumeAsync(t);
        await session.HandoffAsync(t, "DEMO_CTR");
        Assert.Equal(["EMERG", "EA →DC DEMO1A 24R"], TagTemplate.Parse("{warn}\n{owner} {ho} {proc} {rwy}").Render(t, fields));
    }
}

public class ProfileV3Tests
{
    [Fact]
    public void UntouchedOldTagLayoutsGetCoordinationFields()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"natc-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{ "Version": 2, "Tags": { "Untracked": {{System.Text.Json.JsonSerializer.Serialize(TagLayouts.OldDefaults[0])}}, "Tracked": "{callsign} mine" } }""");
        var p = Profile.Load(path);
        File.Delete(path);
        Assert.Equal(TagLayouts.DefaultUntracked, p.Tags.Untracked);
        Assert.Equal("{callsign} mine", p.Tags.Tracked);
        Assert.Equal(3, p.Version);
        Assert.Equal(TagActions.Handoff, p.TagClicks["owner"].Left);
        Assert.Equal("F12", p.KeyBindings["AssumeOrAccept"]);
    }
}
