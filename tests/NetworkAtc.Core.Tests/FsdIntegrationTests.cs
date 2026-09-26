using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Plugins;
using NetworkAtc.Core.Session;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Tests;

/// <summary>
/// End-to-end test against a real SkyNetwork FSD server (repository Skynetwork-fsd).
/// Runs only when SKYNET_FSD_BUILD points at its build directory.
/// </summary>
public class FsdIntegrationTests
{
    private static readonly string? Build = Environment.GetEnvironmentVariable("SKYNET_FSD_BUILD");

    [Fact]
    public async Task ControllerSeesPilotFlightPlanAndMessages()
    {
        if (string.IsNullOrEmpty(Build)) return;
        var dir = Directory.CreateTempSubdirectory("natc");
        string db = Path.Combine(dir.FullName, "net.db");
        Run("skynet-admin", $"--db {db} adduser 1000001 \"Pilot One\" pw1");
        Run("skynet-admin", $"--db {db} adduser 1000002 \"Controller\" pw2 S3");
        int port = FreePort(), httpPort = FreePort();
        using var fsd = Process.Start(new ProcessStartInfo(Path.Combine(Build, "skynet-fsd"),
            $"--db {db} --host 127.0.0.1 --port {port} --http-port {httpPort}") { RedirectStandardError = true })!;
        try
        {
            await Task.Delay(300);
            var atc = new AtcSession();
            var messages = new List<AtcMessage>();
            atc.MessageReceived += (_, m) => { lock (messages) messages.Add(m); };
            await atc.ConnectAsync(new AtcConnectInfo("127.0.0.1", port, 1000002, "pw2", "Controller", 4,
                "UUEE_TWR", 131500, Facility.Tower, 50, new GeoPoint(55.97, 37.41)));
            Assert.True(atc.IsConnected);

            var pilot = new FsdClient();
            await pilot.ConnectAsync("127.0.0.1", port, "#APAFL123:SERVER:1000001:pw1:1:100:1:Pilot One");
            await pilot.SendAsync("$FPAFL123:*A:I:A20N:450:UUEE:1200:0:FL350:ULLI:1:10:3:0:ULLO:/V/:DCT");
            await pilot.SendAsync("@N:AFL123:4521:1:55.980000:37.400000:3000:180:0:0");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((atc.Find("AFL123") is not { HasFlightPlan: true, LastUpdate.Ticks: > 0 }) && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            var track = atc.Find("AFL123");
            Assert.NotNull(track);
            Assert.True(track!.HasFlightPlan);
            Assert.Equal(("UUEE", "ULLI", "A20N", "FL350"), (track.Departure, track.Destination, track.AircraftType, track.FiledAltitude));
            Assert.Equal(4521, track.Squawk);
            Assert.Equal(3000, track.Altitude);

            await pilot.SendAsync("#TMAFL123:@31500:request taxi");
            await atc.SendPrivateAsync("AFL123", "hello");
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (messages) if (messages.Any(m => m is { Channel: MessageChannel.Radio, Outgoing: false })) break;
                await Task.Delay(50);
            }
            lock (messages)
                Assert.Contains(messages, m => m is { Channel: MessageChannel.Radio, From: "AFL123", Text: "request taxi", FrequencyKhz: 131500 });

            await pilot.DisconnectAsync("#DPAFL123:1000001");
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (atc.Find("AFL123") != null && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Null(atc.Find("AFL123"));
            await atc.DisconnectAsync();
        }
        finally
        {
            fsd.Kill();
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task TwoControllersCoordinateThroughTheServer()
    {
        if (string.IsNullOrEmpty(Build)) return;
        var dir = Directory.CreateTempSubdirectory("natc");
        string db = Path.Combine(dir.FullName, "net.db");
        Run("skynet-admin", $"--db {db} adduser 1000001 \"Pilot One\" pw1");
        Run("skynet-admin", $"--db {db} adduser 1000002 \"Approach\" pw2 C1");
        Run("skynet-admin", $"--db {db} adduser 1000003 \"Centre\" pw3 C1");
        int port = FreePort(), httpPort = FreePort();
        using var fsd = Process.Start(new ProcessStartInfo(Path.Combine(Build, "skynet-fsd"),
            $"--db {db} --host 127.0.0.1 --port {port} --http-port {httpPort}") { RedirectStandardError = true })!;
        try
        {
            await Task.Delay(300);
            var app = new AtcSession();
            var ctr = new AtcSession();
            var ctrEvents = new List<CoordinationEvent>();
            var appEvents = new List<CoordinationEvent>();
            ctr.Coordination += (_, e) => { lock (ctrEvents) ctrEvents.Add(e); };
            app.Coordination += (_, e) => { lock (appEvents) appEvents.Add(e); };
            await app.ConnectAsync(new AtcConnectInfo("127.0.0.1", port, 1000002, "pw2", "Approach", 5,
                "UUEE_APP", 128000, Facility.Approach, 100, new GeoPoint(55.97, 37.41)));
            await ctr.ConnectAsync(new AtcConnectInfo("127.0.0.1", port, 1000003, "pw3", "Centre", 5,
                "DEMO_CTR", 132000, Facility.Centre, 300, new GeoPoint(55.97, 37.41)));
            var pilot = new FsdClient();
            await pilot.ConnectAsync("127.0.0.1", port, "#APAFL1:SERVER:1000001:pw1:1:100:1:Pilot One");
            await pilot.SendAsync("@N:AFL1:2000:1:56.000000:37.400000:8000:250:0:0");
            await WaitFor(() => app.Find("AFL1") is { LastUpdate.Ticks: > 0 } && ctr.Find("AFL1") is { LastUpdate.Ticks: > 0 }
                                && app.Controllers.Count == 1);

            var a = app.Find("AFL1")!;
            var c = ctr.Find("AFL1")!;
            Assert.Null(await app.AssumeAsync(a));
            await app.AnnotateAsync(a, Annotation.ClearedAltitude, "12000");
            await WaitFor(() => c.Owner == "UUEE_APP" && c.ClearedAltitude == 12000);

            Assert.Null(await app.HandoffAsync(a, "DEMO_CTR"));
            await WaitFor(() => c.HandoffPending);
            lock (ctrEvents) Assert.Contains(ctrEvents, e => e.Kind == CoordinationKind.HandoffRequested && e.Peer == "UUEE_APP");
            Assert.Null(await ctr.AcceptHandoffAsync(c));
            await WaitFor(() => a.Owner == "DEMO_CTR" && !a.HandoffPending);
            Assert.False(a.IsTracked);
            Assert.True(c.IsTracked);
            lock (appEvents) Assert.Contains(appEvents, e => e.Kind == CoordinationKind.HandoffAccepted);

            await pilot.DisconnectAsync("#DPAFL1:1000001");
            await app.DisconnectAsync();
            await ctr.DisconnectAsync();
        }
        finally
        {
            fsd.Kill();
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task SupervisorFindsAndDisconnectsAPilot()
    {
        if (string.IsNullOrEmpty(Build)) return;
        var dir = Directory.CreateTempSubdirectory("natc");
        string db = Path.Combine(dir.FullName, "net.db");
        Run("skynet-admin", $"--db {db} adduser 1000001 \"Pilot One\" pw1");
        Run("skynet-admin", $"--db {db} adduser 1000002 \"Network Sup\" pw2 C1");
        Run("skynet-admin", $"--db {db} staff 1000002 SUP");
        int port = FreePort(), httpPort = FreePort();
        using var fsd = Process.Start(new ProcessStartInfo(Path.Combine(Build, "skynet-fsd"),
            $"--db {db} --host 127.0.0.1 --port {port} --http-port {httpPort}") { RedirectStandardError = true })!;
        try
        {
            await Task.Delay(300);
            var sup = new AtcSession();
            var messages = new List<AtcMessage>();
            sup.MessageReceived += (_, m) => { lock (messages) messages.Add(m); };
            (string Callsign, GeoPoint Position)? found = null;
            sup.ServerFound += (_, f) => found = f;
            await sup.ConnectAsync(new AtcConnectInfo("127.0.0.1", port, 1000002, "pw2", "Network Sup", 11,
                "SKY_SUP", 122800, Facility.Supervisor, 600, new GeoPoint(55.97, 37.41)));
            var cmd = new CommandProcessor(sup, () => new Profile(), new PluginRegistry(), () => null);

            // Far out of range: only the server knows where it is.
            var pilot = new FsdClient();
            var pilotLines = new List<string>();
            string? pilotGone = null;
            pilot.PacketReceived += (_, p) => { lock (pilotLines) pilotLines.Add(p.ToString()); };
            pilot.Disconnected += (_, r) => pilotGone = r;
            await pilot.ConnectAsync("127.0.0.1", port, "#APAFL9:SERVER:1000001:pw1:1:100:1:Pilot One");
            await pilot.SendAsync("@N:AFL9:2000:1:43.440000:39.950000:3000:180:0:0");
            await Task.Delay(300);

            Assert.Contains("asking the server", await cmd.ExecuteAsync(".find afl9"));
            await WaitFor(() => found != null);
            Assert.Equal("AFL9", found!.Value.Callsign);
            Assert.Equal(43.44, found.Value.Position.Latitude, 3);

            Assert.Null(await cmd.ExecuteAsync(".whois AFL9"));
            await WaitFor(() => { lock (messages) return messages.Any(m => m.Text.Contains("CID 1000001")); });

            Assert.Null(await cmd.ExecuteAsync(".kill AFL9 ignoring ATC: repeatedly"));
            await WaitFor(() => pilotGone != null);
            lock (pilotLines) Assert.Contains(pilotLines, l => l.Contains("Reason: ignoring ATC  repeatedly"));
            await WaitFor(() => { lock (messages) return messages.Any(m => m.Text.Contains("AFL9 (CID 1000001) disconnected")); });
            await sup.DisconnectAsync();
        }
        finally
        {
            fsd.Kill();
            dir.Delete(true);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(condition());
    }

    private static void Run(string tool, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Build!, tool), args) { RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
