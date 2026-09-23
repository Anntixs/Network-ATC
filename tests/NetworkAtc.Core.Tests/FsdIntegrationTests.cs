using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetworkAtc.Core.Fsd;
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
