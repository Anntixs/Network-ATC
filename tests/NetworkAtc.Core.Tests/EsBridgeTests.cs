using System.IO.Pipes;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.EsPlugins;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Session;

namespace NetworkAtc.Core.Tests;

/// <summary>
/// The bridge against a fake plugin host: the fake reads every frame field by field, in the order the
/// native host (native/EsBridge/src/Engine.cpp) reads it, so a field out of step shows up as leftover bytes.
/// </summary>
public class EsBridgeTests
{
    private sealed class FakeHost : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly NamedPipeClientStream _client;
        public List<(EsMsg Type, byte[] Frame)> Received { get; } = [];
        public EsHost Host { get; } = new();

        public FakeHost()
        {
            string name = "natc-test-" + Guid.NewGuid().ToString("N");
            _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var wait = _server.WaitForConnectionAsync();
            _client.Connect(5000);
            wait.Wait(5000);
            Host.Attach(_server);
            _ = Task.Run(ReadAsync);
        }

        private async Task ReadAsync()
        {
            var header = new byte[4];
            try
            {
                while (true)
                {
                    await _client.ReadExactlyAsync(header);
                    var frame = new byte[BitConverter.ToInt32(header)];
                    await _client.ReadExactlyAsync(frame);
                    lock (Received) Received.Add(((EsMsg)frame[0], frame));
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException) { }
        }

        public void Reply(EsWriter w)
        {
            _client.Write(w.Frame());
            _client.Flush();
        }

        public async Task<byte[]> WaitFor(EsMsg type, Func<byte[], bool>? match = null)
        {
            for (int i = 0; i < 200; i++)
            {
                lock (Received)
                    foreach (var (t, f) in Received)
                        if (t == type && (match == null || match(f))) return f;
                await Task.Delay(10);
            }
            throw new TimeoutException(type.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _server.DisposeAsync();
        }
    }

    private static (AtcSession Session, Workspace Workspace, Profile Profile) World()
    {
        var profile = new Profile();
        var session = new AtcSession { LocalCallsign = "UUEE_TWR" };
        var workspace = new Workspace(session, () => profile);
        Demo.Pilot(session, "AFL123", 55.97, 37.41, 3000);
        Demo.Plan(session, "AFL123", "UUEE", "UUDD", "DCT");
        return (session, workspace, profile);
    }

    /// <summary>Reads an aircraft record like Engine::ReadAircraft; fails if anything is left over.</summary>
    private static (string Callsign, int Cfl, string Route, int Points) ReadAircraft(byte[] frame)
    {
        var r = new EsReader(frame, 1);
        string cs = r.Str(); r.Str(); r.Str(); r.Bool(); r.Bool();
        r.F64(); r.F64(); r.I32(); r.I32(); r.I32(); r.I32(); r.Str(); r.Bool(); r.Bool(); r.I32(); r.F64(); r.I32(); r.I32();
        r.Bool(); r.Bool(); r.Str(); r.Str(); r.Str(); r.Str(); r.U8(); r.U8(); r.I32(); r.U8(); r.U8(); r.Bool(); r.I32();
        r.Str(); r.I32(); r.Str(); r.Str(); r.Str(); r.U8(); string route = r.Str(); r.Str(); r.Str(); r.Str(); r.Str(); r.Str(); r.Str();
        r.Str(); r.Str(); r.Str(); r.Str();
        r.Str(); r.I32(); int cfl = r.I32(); r.U8(); r.Str(); r.I32(); r.I32(); r.I32(); r.I32(); r.Str();
        int annotations = r.I32();
        for (int i = 0; i < annotations; i++) r.Str();
        r.I32(); r.I32(); r.Bool(); r.Str(); r.Str(); r.Bool(); r.Str(); r.Str(); r.F64(); r.F64(); r.Str(); r.Str(); r.I32(); r.I32();
        r.Bool(); r.Bool(); r.Str(); r.Bool(); r.Bool(); r.Str(); r.I32(); r.I32(); r.Str(); r.I32(); r.I32(); r.I32(); r.Str(); r.I32(); r.I32();
        r.I32(); r.I32();
        int points = r.I32();
        for (int i = 0; i < points; i++) { r.Str(); r.F64(); r.F64(); r.Str(); r.I32(); r.I32(); r.I32(); }
        int predictions = r.I32();
        for (int i = 0; i < predictions; i++) { r.F64(); r.F64(); r.I32(); r.Str(); }
        Assert.True(r.Ok, "the frame is shorter than the host reads");
        r.U8();
        Assert.False(r.Ok, "the frame has more fields than the host reads");
        return (cs, cfl, route, points);
    }

    [Fact]
    public async Task AircraftFrame_HasEveryFieldTheHostReads()
    {
        var (session, workspace, profile) = World();
        await using var fake = new FakeHost();
        await using var bridge = new EsBridge(session, workspace, () => profile);
        await bridge.AttachAsync(fake.Host, "settings.txt");
        session.Tracks.Single().ClearedAltitude = 5000;
        bridge.SyncWorld();

        var hello = new EsReader(await fake.WaitFor(EsMsg.Hello), 1);
        Assert.Equal("settings.txt", hello.Str());
        var aircraft = ReadAircraft(await fake.WaitFor(EsMsg.Aircraft));
        Assert.Equal(("AFL123", 5000, "DCT"), (aircraft.Callsign, aircraft.Cfl, aircraft.Route));

        // Nothing changed: nothing is sent again.
        int before;
        lock (fake.Received) before = fake.Received.Count(f => f.Type == EsMsg.Aircraft);
        bridge.SyncWorld();
        await Task.Delay(100);
        lock (fake.Received) Assert.Equal(before, fake.Received.Count(f => f.Type == EsMsg.Aircraft));
    }

    [Fact]
    public async Task PluginsItemsValuesCommandsAndActions()
    {
        var (session, workspace, profile) = World();
        await using var fake = new FakeHost();
        await using var bridge = new EsBridge(session, workspace, () => profile);
        var messages = new List<EsUserMessage>();
        var popups = new List<EsPopup>();
        bridge.UserMessage += messages.Add;
        bridge.Popup += popups.Add;
        await bridge.AttachAsync(fake.Host, "settings.txt");

        // The plugin registers in its constructor, before the host announces it.
        fake.Reply(new EsWriter(EsMsg.TagItemType).I32(1).Str("CFL plus").I32(1));
        fake.Reply(new EsWriter(EsMsg.TagItemFunction).I32(1).Str("Menu").I32(10));
        fake.Reply(new EsWriter(EsMsg.DisplayType).I32(1).Str("Test display").Bool(false).Bool(true).Bool(true).Bool(true));
        fake.Reply(new EsWriter(EsMsg.PluginLoaded).I32(1).Str(@"C:\p\Test.dll").Str("Test Plugin").Str("1.0").Str("A").Str("C"));
        await Until(() => bridge.Plugins.Count == 1 && bridge.TagItems.All(i => i.PluginName == "Test Plugin"));
        Assert.Equal("es:Test Plugin:1", Assert.Single(bridge.TagItems).FieldKey);
        Assert.Equal("Test Plugin", Assert.Single(bridge.DisplayTypes).PluginName);

        // Items in use go to the host; values come back.
        bridge.SetTagItemsInUse(["es:Test Plugin:1", "callsign", "es:Other:5"]);
        var inUse = new EsReader(await fake.WaitFor(EsMsg.TagItemsInUse), 1);
        Assert.Equal((1, 1, 1), (inUse.I32(), inUse.I32(), inUse.I32()));
        fake.Reply(new EsWriter(EsMsg.TagValues).I32(1).Str("AFL123").I32(1).I32(1).Str("+050").I32(1).U32(0x00C800).F64(1));
        await Until(() => bridge.Value("afl123", "Test Plugin", 1) != null);
        Assert.Equal("+050", bridge.Value("AFL123", 1, 1)!.Text);

        // A command offered to the plugins.
        var command = bridge.CommandAsync(".test");
        var request = new EsReader(await fake.WaitFor(EsMsg.Command), 1);
        int id = request.I32();
        Assert.Equal(".test", request.Str());
        fake.Reply(new EsWriter(EsMsg.CommandResult).I32(id).Bool(true));
        Assert.True(await command);

        // Messages and popups.
        fake.Reply(new EsWriter(EsMsg.UserMessage).Str("TEST").Str("plugin").Str("hello").Bool(true).Bool(true).Bool(false).Bool(false).Bool(false));
        fake.Reply(new EsWriter(EsMsg.PopupList).I32(7).I32(1).Str("CFL").I32(1).Rect(new EsRect(10, 10, 50, 20)).I32(1)
            .Str("100").Str("").I32(11).Bool(true).I32(2).Bool(false).Bool(false));
        await Until(() => messages.Count == 1 && popups.Count == 1);
        Assert.Equal("hello", messages[0].Text);
        Assert.Equal(("CFL", 11, "100"), (popups[0].Title, popups[0].Elements[0].FunctionId, popups[0].Elements[0].Text));
        bridge.SelectPopup(popups[0], 11, "100", 12, 15);
        var select = new EsReader(await fake.WaitFor(EsMsg.PopupSelect), 1);
        Assert.Equal((7, 11, "100"), (select.I32(), select.I32(), select.Str()));

        // The plugin sets a CFL: it becomes our shared annotation.
        fake.Reply(new EsWriter(EsMsg.Action).I32((int)EsActionKind.SetAssigned).Str("AFL123").Str("7000").Str("").I32(3));
        await Until(() => session.Tracks.Single().ClearedAltitude == 7000);
    }

    [Fact]
    public void Altitudes_StatesAndAnnotations()
    {
        Assert.Equal(35000, EsBridge.ParseAltitude("FL350"));
        Assert.Equal(35000, EsBridge.ParseAltitude("350"));
        Assert.Equal(4500, EsBridge.ParseAltitude("A045"));
        Assert.Equal(12000, EsBridge.ParseAltitude("12000"));
        Assert.Equal(5, EsBridge.StateCode(Radar.TrackState.Assumed));
        Assert.Equal(3, EsBridge.StateCode(Radar.TrackState.TransferToMe));
        Assert.Equal((Annotation.ClearedAltitude, ""), EsBridge.AssignedAnnotation(3, "1"));  // ILS clearance: no level
        Assert.Equal((Annotation.Scratchpad, "ABC"), EsBridge.AssignedAnnotation(5, "ABC"));
        Assert.Null(EsBridge.AssignedAnnotation(13, "DCT"));
        Assert.Equal(("TopSky", 12), EsBridge.ParseKey("es:TopSky:12", "es:"));
        Assert.Null(EsBridge.ParseKey("callsign", "es:"));
    }

    [Fact]
    public void SectorElements_CoverEuroScopeTypes()
    {
        var sector = Sectors.SectorParser.LoadFiles(Path.Combine(AppContext.BaseDirectory, "demo", "UUEE-demo.sct"));
        var profile = new Profile { ActiveAirports = ["UUEE"] };
        profile.ActiveRunways["UUEE"] = new Sectors.RunwayUse { Departure = ["24R"], Arrival = ["24L"] };
        var types = EsBridge.SectorElements(sector, profile).Select(w => BitConverter.ToInt32(w.Frame(), 5)).Distinct().ToList();
        Assert.Contains(3, types);  // airport
        Assert.Contains(4, types);  // runway
        Assert.Contains(5, types);  // fix
        Assert.Contains(7, types);  // SID
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
