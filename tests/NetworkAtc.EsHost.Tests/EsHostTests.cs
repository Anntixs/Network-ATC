using System.IO.MemoryMappedFiles;
using System.Text;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.EsPlugins;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Session;
using NetworkAtc.Plugins;

namespace NetworkAtc.EsHost.Tests;

/// <summary>The real 32-bit host with the test plugin (native/EsBridge/testplugin), driven from the Network-ATC side.</summary>
public class EsHostTests
{
    private static string? Dir => Environment.GetEnvironmentVariable("NATC_ESBRIDGE_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void OurDll_ExportsEverythingEuroScopeDoes()
    {
        if (Dir is not { } dir) return;
        var ours = PeExports(Path.Combine(dir, "EuroScopePlugInDll.dll"));
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "exports.txt")).Where(l => l.Length > 0).ToList();
        var missing = expected.Where(e => !ours.Contains(e)).ToList();
        Assert.True(missing.Count == 0, "missing exports:\n" + string.Join("\n", missing));
        Assert.Contains("NatcEsHostRun", ours);
    }

    /// <summary>Names in the export table of a PE file.</summary>
    private static HashSet<string> PeExports(string path)
    {
        var b = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(b, 0x3C);
        int sections = BitConverter.ToUInt16(b, pe + 6);
        int optionalSize = BitConverter.ToUInt16(b, pe + 20);
        int optional = pe + 24;
        bool pe32Plus = BitConverter.ToUInt16(b, optional) == 0x20B;
        int exportRva = BitConverter.ToInt32(b, optional + (pe32Plus ? 112 : 96));
        int sectionTable = optional + optionalSize;
        int Offset(int rva)
        {
            for (int i = 0; i < sections; i++)
            {
                int s = sectionTable + i * 40;
                int va = BitConverter.ToInt32(b, s + 12), size = BitConverter.ToInt32(b, s + 16), raw = BitConverter.ToInt32(b, s + 20);
                if (rva >= va && rva < va + Math.Max(size, BitConverter.ToInt32(b, s + 8))) return rva - va + raw;
            }
            throw new InvalidDataException("rva outside sections");
        }
        int dirOffset = Offset(exportRva);
        int count = BitConverter.ToInt32(b, dirOffset + 24);
        int namesOffset = Offset(BitConverter.ToInt32(b, dirOffset + 32));
        var names = new HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            int nameOffset = Offset(BitConverter.ToInt32(b, namesOffset + i * 4));
            int end = Array.IndexOf(b, (byte)0, nameOffset);
            names.Add(Encoding.ASCII.GetString(b, nameOffset, end - nameOffset));
        }
        return names;
    }

    private static void Pilot(AtcSession s, string cs, double lat, double lon, int alt) =>
        s.OnPacket(null, FsdPacket.Parse(FormattableString.Invariant($"@N:{cs}:2000:1:{lat}:{lon}:{alt}:250:0:0"))!);

    private static void Plan(AtcSession s, string cs, string dep, string dest, string route) =>
        s.OnPacket(null, FsdPacket.Parse($"$FP{cs}:*A:I:A320:450:{dep}:1200:0:FL350:{dest}:1:10:3:0:ULLO:/V/:{route}")!);

    private static async Task Until(Func<bool> condition, string what, Func<string>? details = null)
    {
        for (int i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out: " + what + (details == null ? "" : " — " + details()));
    }

    [Fact]
    public async Task TestPlugin_RunsInTheHost()
    {
        if (Dir is not { } dir) return;
        var profile = new Profile();
        var session = new AtcSession { LocalCallsign = "UUEE_TWR" };
        var workspace = new Workspace(session, () => profile);
        Pilot(session, "AFL123", 55.97, 37.41, 5000);
        Plan(session, "AFL123", "UUEE", "UUDD", "DCT");
        var track = session.Tracks.Single();
        track.ClearedAltitude = 35000;

        string settings = Path.Combine(Path.GetTempPath(), $"natc-es-{Guid.NewGuid():N}.txt");
        await using var bridge = new EsBridge(session, workspace, () => profile);
        var messages = new List<EsUserMessage>();
        var popups = new List<EsPopup>();
        var drawn = new List<EsViewDrawn>();
        var logs = new List<string>();
        var viewData = new List<string>();
        bridge.UserMessage += m => { lock (messages) messages.Add(m); };
        bridge.Popup += p => { lock (popups) popups.Add(p); };
        bridge.ViewDrawn += d => { lock (drawn) drawn.Add(d); };
        bridge.Log += (t, _) => { lock (logs) logs.Add(t); };
        bridge.ViewData += (_, name, _, value) => { lock (viewData) viewData.Add($"{name}={value}"); };

        await bridge.StartAsync(Path.Combine(dir, "NetworkAtc.EsHost.exe"), settings);
        bridge.LoadPlugin(Path.Combine(dir, "TestPlugin.dll"));
        await Until(() => bridge.Plugins.Any(p => p.Name == "Test Plugin"), "plugin loaded", () => { lock (logs) return string.Join("; ", logs); });
        Assert.Equal(["es:Test Plugin:1", "es:Test Plugin:2"], bridge.TagItems.Select(i => i.FieldKey));
        Assert.Equal([10, 12], bridge.TagFunctions.Select(f => f.Code));
        Assert.Contains(bridge.DisplayTypes, d => d.Name == "Test display" && !d.NeedRadarContent);

        // Tag items computed by the plugin from our data.
        bridge.SyncWorld();
        bridge.SetTagItemsInUse(["es:Test Plugin:1", "es:Test Plugin:2"]);
        await Until(() => bridge.Value("AFL123", "Test Plugin", 1)?.Text == "+350", "CFL item");
        Assert.Equal(1u, bridge.Value("AFL123", "Test Plugin", 1)!.ColorCode == 1 ? 1u : 0u);
        Assert.EndsWith("R", bridge.Value("AFL123", "Test Plugin", 2)!.Text);

        // Commands, settings and our station.
        Assert.True(await bridge.CommandAsync(".test"));
        Assert.False(await bridge.CommandAsync(".other"));
        await Until(() => { lock (messages) return messages.Any(m => m.Text.Contains("me=UUEE_TWR") && m.Text.Contains("greeting=hello")); }, "command message");

        // A tag function opens a popup list; picking an element sets the CFL through Network-ATC.
        bridge.CallFunction("Test Plugin", 10, "AFL123", "+350", 100, 100, new EsRect(90, 90, 130, 110));
        await Until(() => { lock (popups) return popups.Any(p => p.Title == "CFL"); }, "popup list");
        EsPopup list;
        lock (popups) list = popups.Single(p => p.Title == "CFL");
        Assert.Equal(["100", "200"], list.Elements.Select(e => e.Text));
        bridge.SelectPopup(list, 11, "100", 100, 100);
        await Until(() => track.ClearedAltitude == 10000, "CFL set by the plugin");

        // A popup edit box: the scratch pad.
        bridge.CallFunction("Test Plugin", 12, "AFL123", "", 100, 100, new EsRect(90, 90, 130, 110));
        await Until(() => { lock (popups) return popups.Any(p => p.IsEdit); }, "popup edit");
        EsPopup edit;
        lock (popups) edit = popups.First(p => p.IsEdit);
        bridge.SelectPopup(edit, edit.FunctionId, "HELLO", 100, 100);
        await Until(() => track.Scratchpad == "HELLO", "scratch pad set by the plugin");

        // The radar screen draws into the shared bitmap and adds a clickable object.
        bridge.OpenView(EsBridge.MainView, EsBridge.StandardDisplay, new Dictionary<string, string>());
        bridge.ViewGeometry(EsBridge.MainView, 400, 300, new GeoPoint(55.97, 37.41), 0, 0, 0.05, 0x00000000,
            new EsRect(0, 0, 400, 300), new EsRect(0, 0, 400, 0), new EsRect(0, 300, 400, 300));
        bridge.RefreshView(EsBridge.MainView);
        await Until(() => { lock (drawn) return drawn.Count > 0; }, "view drawn");
        EsViewDrawn view;
        lock (drawn) view = drawn.Last();
        var box = Assert.Single(view.Objects);
        Assert.Equal(("AFL123", 7), (box.ObjectId, box.ObjectType));
        Assert.InRange(box.Area.Left, 190, 200);   // the aircraft is at the centre of the 400x300 view
        if (OperatingSystem.IsWindows())
        {
            using var map = MemoryMappedFile.OpenExisting(view.FrontMapping);
            using var acc = map.CreateViewAccessor(0, 400 * 300 * 4);
            int x = box.Area.Left, y = (box.Area.Top + box.Area.Bottom) / 2;
            uint pixel = acc.ReadUInt32((y * 400 + x) * 4);
            Assert.Equal(0x00FF0000u, pixel & 0x00FFFFFF);  // BGRA: red
        }

        // Clicking the object: the screen answers with its own popup and saves ASR data.
        bridge.ScreenObjectEvent(EsBridge.MainView, box, 3, box.Area.Left + 2, box.Area.Top + 2, 1);
        await Until(() => { lock (popups) return popups.Any(p => p.Title == "SCREEN"); }, "screen popup");
        await Until(() => { lock (viewData) return viewData.Contains("TestValue=AFL123"); }, "ASR data");
        EsPopup screenPopup;
        lock (popups) screenPopup = popups.Single(p => p.Title == "SCREEN");
        bridge.SelectPopup(screenPopup, 99, "A", 0, 0);
        await Until(() => { lock (messages) return messages.Any(m => m.Sender == "screen" && m.Text == "A"); }, "screen function");

        // The flight plan list is filled on the timer.
        await Until(() => bridge.Lists.Any(l => l.Name == "Test list" && l.Visible && l.Callsigns.Contains("AFL123")), "fp list");
        Assert.Equal(2, bridge.Lists.Single().Columns.Count);

        bridge.CloseView(EsBridge.MainView);
        bridge.UnloadPlugin(bridge.Plugins.Single().Id);
        await Until(() => bridge.Plugins.Count == 0, "plugin unloaded");
        lock (logs) Assert.DoesNotContain(logs, l => l.Contains("error"));
        File.Delete(settings);
    }
}
