using System.Globalization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Geo;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Session;

/// <summary>
/// Local simulated traffic for trying the radar without a server (command ".demo").
/// Aircraft fly straight lines around a center point; two of them converge so STCA can be seen.
/// </summary>
public sealed class DemoTraffic : IDisposable
{
    private sealed class Flight
    {
        public required string Callsign { get; init; }
        public required string Type { get; init; }
        public required string Dep { get; init; }
        public required string Dest { get; init; }
        public GeoPoint Position;
        public double Heading;
        public int Speed;
        public double Altitude;
        public double VerticalFpm;
        public int Squawk;
    }

    private readonly AtcSession _session;
    private readonly List<Flight> _flights = [];
    private readonly Timer _timer;
    private DateTime _last = DateTime.UtcNow;

    public DemoTraffic(AtcSession session, GeoPoint center)
    {
        _session = session;
        var rnd = new Random(42);
        (string Cs, string Type, string Dep, string Dest)[] plan =
        [
            ("AFL1234", "A20N", "UUEE", "ULLI"), ("SBI2150", "B738", "UUDD", "URSS"), ("SDM6012", "SU95", "UUEE", "USSS"),
            ("AFL037", "B77W", "UUEE", "ZBAA"), ("UTA403", "B737", "UUWW", "UWGG"), ("RA67221", "C172", "UUMO", "UUMO"),
            ("DLH1447", "A321", "EDDF", "UUEE"), ("AFL1521", "A333", "LFPG", "UUEE"),
        ];
        for (int i = 0; i < plan.Length; i++)
        {
            double brg = i * 360.0 / plan.Length + rnd.NextDouble() * 20;
            var p = plan[i];
            _flights.Add(new Flight
            {
                Callsign = p.Cs, Type = p.Type, Dep = p.Dep, Dest = p.Dest,
                Position = GeoMath.Offset(center, brg, 15 + rnd.NextDouble() * 35),
                Heading = (brg + 150 + rnd.NextDouble() * 60) % 360,
                Speed = p.Type == "C172" ? 105 : 240 + rnd.Next(0, 200),
                Altitude = p.Type == "C172" ? 2500 : 6000 + rnd.Next(0, 28) * 1000,
                VerticalFpm = rnd.Next(-1, 2) * 1500,
                Squawk = 4101 + i,
            });
        }
        // A converging pair for STCA.
        _flights.Add(new Flight { Callsign = "AFL900", Type = "A320", Dep = "UUEE", Dest = "UUYY", Position = GeoMath.Offset(center, 90, 22), Heading = 270, Speed = 300, Altitude = 12000, Squawk = 4121 });
        _flights.Add(new Flight { Callsign = "SBI901", Type = "A319", Dep = "UUDD", Dest = "ULMM", Position = GeoMath.Offset(center, 90, 8), Heading = 90, Speed = 280, Altitude = 12400, Squawk = 4122 });

        foreach (var f in _flights)
            _session.OnPacket(this, FsdPacket.Parse(
                $"$FP{f.Callsign}:*A:I:{f.Type}:{f.Speed + 30}:{f.Dep}:1200:0:FL{(int)Math.Max(f.Altitude + 10000, 20000) / 100}:{f.Dest}:1:30:3:0::/V/ demo:DCT")!);
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        double dt = (now - _last).TotalHours;
        _last = now;
        foreach (var f in _flights)
        {
            f.Position = GeoMath.Offset(f.Position, f.Heading, f.Speed * dt);
            f.Altitude = Math.Clamp(f.Altitude + f.VerticalFpm * dt * 60, 1500, 39000);
            if (f.Altitude is <= 1500 or >= 39000) f.VerticalFpm = 0;
            uint pbh = Pbh.Encode(f.VerticalFpm > 0 ? 3 : f.VerticalFpm < 0 ? -2 : 0, 0, f.Heading, false);
            _session.OnPacket(this, FsdPacket.Parse(string.Create(CultureInfo.InvariantCulture,
                $"@N:{f.Callsign}:{f.Squawk:0000}:1:{f.Position.Latitude:0.000000}:{f.Position.Longitude:0.000000}:{(int)f.Altitude}:{f.Speed}:{pbh}:0"))!);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var f in _flights) _session.OnPacket(this, FsdPacket.Parse($"#DP{f.Callsign}:0")!);
    }
}
