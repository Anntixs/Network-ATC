using NetworkAtc.Core.Geo;

namespace NetworkAtc.Core.Radar;

public sealed record Conflict(Track A, Track B, double DistanceNm, int VerticalFeet, bool Predicted)
{
    /// <summary>The pair, the same whichever aircraft is A: "AFL1|SBI2".</summary>
    public string PairKey
    {
        get
        {
            string a = A.Callsign.ToUpperInvariant(), b = B.Callsign.ToUpperInvariant();
            return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
        }
    }
}

/// <summary>Short term conflict alert: current and predicted loss of separation between airborne aircraft.</summary>
public sealed class Stca
{
    public double HorizontalNm { get; set; } = 5;
    public int VerticalFeet { get; set; } = 1000;
    public double LookAheadMinutes { get; set; } = 2;
    /// <summary>Aircraft below this altitude (e.g. on final or taxiing) are ignored.</summary>
    public int MinimumAltitude { get; set; } = 1500;

    public IReadOnlyList<Conflict> Check(IEnumerable<Track> tracks)
    {
        var airborne = tracks.Where(t => !t.OnGround && t.Altitude >= MinimumAltitude).ToList();
        var result = new List<Conflict>();
        for (int i = 0; i < airborne.Count; i++)
        {
            for (int j = i + 1; j < airborne.Count; j++)
            {
                var a = airborne[i];
                var b = airborne[j];
                int dv = Math.Abs(a.Altitude - b.Altitude);
                double dh = GeoMath.DistanceNm(a.Position, b.Position);
                if (dh < HorizontalNm && dv < VerticalFeet)
                {
                    result.Add(new Conflict(a, b, dh, dv, Predicted: false));
                    continue;
                }
                // Linear prediction in 30 s steps.
                for (double m = 0.5; m <= LookAheadMinutes; m += 0.5)
                {
                    int pa = a.Altitude + (int)(a.VerticalSpeed * m), pb = b.Altitude + (int)(b.VerticalSpeed * m);
                    double ph = GeoMath.DistanceNm(a.Predict(m), b.Predict(m));
                    if (ph < HorizontalNm && Math.Abs(pa - pb) < VerticalFeet)
                    {
                        result.Add(new Conflict(a, b, ph, Math.Abs(pa - pb), Predicted: true));
                        break;
                    }
                }
            }
        }
        return result;
    }
}
