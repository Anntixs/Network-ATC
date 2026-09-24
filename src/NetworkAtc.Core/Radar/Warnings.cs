namespace NetworkAtc.Core.Radar;

[Flags]
public enum TrackWarning
{
    None = 0,
    /// <summary>Emergency code 7500/7600/7700.</summary>
    Emergency = 1,
    /// <summary>Another aircraft squawks the same discrete code.</summary>
    Duplicate = 2,
    /// <summary>The aircraft does not squawk the code assigned to it.</summary>
    WrongSquawk = 4,
    /// <summary>Cleared level adherence: off the CFL and not moving towards it.</summary>
    Clam = 8,
}

public static class Warnings
{
    /// <summary>Codes many aircraft share legitimately.</summary>
    private static readonly HashSet<int> NonDiscrete = [0, 1200, 2000, 2200, 7000];

    public static TrackWarning Of(Track t, IReadOnlyCollection<Track> all, int clamFeet = 300)
    {
        var w = TrackWarning.None;
        if (t.Squawk is 7500 or 7600 or 7700) w |= TrackWarning.Emergency;
        if (t.AssignedSquawk is { } q && q != t.Squawk && t.LastUpdate != default) w |= TrackWarning.WrongSquawk;
        if (!NonDiscrete.Contains(t.Squawk) && t.LastUpdate != default &&
            all.Any(o => !ReferenceEquals(o, t) && o.Squawk == t.Squawk && o.LastUpdate != default))
            w |= TrackWarning.Duplicate;
        if (IsClam(t, clamFeet)) w |= TrackWarning.Clam;
        return w;
    }

    /// <summary>Off the cleared level by more than the tolerance and not climbing/descending towards it.</summary>
    public static bool IsClam(Track t, int clamFeet)
    {
        if (t.ClearedAltitude is not { } cfl || t.OnGround || t.LastUpdate == default) return false;
        int deviation = t.Altitude - cfl;
        if (Math.Abs(deviation) <= clamFeet) return false;
        bool converging = deviation > 0 ? t.VerticalSpeed < -300 : t.VerticalSpeed > 300;
        return !converging;
    }

    /// <summary>Short text for the tag: "7700 DUPE CLAM".</summary>
    public static string Text(Track t, TrackWarning w)
    {
        var parts = new List<string>();
        if (w.HasFlag(TrackWarning.Emergency)) parts.Add(t.Squawk switch { 7500 => "HIJACK", 7600 => "RADIO", _ => "EMERG" });
        if (w.HasFlag(TrackWarning.Duplicate)) parts.Add("DUPE");
        if (w.HasFlag(TrackWarning.WrongSquawk)) parts.Add("SQ");
        if (w.HasFlag(TrackWarning.Clam)) parts.Add("CLAM");
        return string.Join(' ', parts);
    }
}
