namespace NetworkAtc.Core.Radar;

/// <summary>The EuroScope correlation states that decide a tag's color.</summary>
public enum TrackState
{
    /// <summary>Not tracked and not heading for our airspace.</summary>
    NotConcerned,
    /// <summary>Not tracked, but in or entering our airspace or on our airports.</summary>
    Concerned,
    /// <summary>We have it assumed.</summary>
    Assumed,
    /// <summary>Another controller offers it to us.</summary>
    TransferToMe,
    /// <summary>We offered it to another controller.</summary>
    TransferFromMe,
    /// <summary>Another controller has it assumed.</summary>
    Redundant,
}

public static class TrackStates
{
    public static TrackState Of(Track t, string me, bool concerned)
    {
        if (t.HandoffPending && t.HandoffTo.Equals(me, StringComparison.OrdinalIgnoreCase)) return TrackState.TransferToMe;
        if (t.HandoffPending && t.HandoffFrom.Equals(me, StringComparison.OrdinalIgnoreCase)) return TrackState.TransferFromMe;
        if (t.Owner.Length > 0)
            return t.Owner.Equals(me, StringComparison.OrdinalIgnoreCase) ? TrackState.Assumed : TrackState.Redundant;
        return concerned ? TrackState.Concerned : TrackState.NotConcerned;
    }

    public static string Title(TrackState s) => s switch
    {
        TrackState.NotConcerned => "not concerned",
        TrackState.Concerned => "concerned",
        TrackState.Assumed => "assumed",
        TrackState.TransferToMe => "transfer to me",
        TrackState.TransferFromMe => "transfer from me",
        TrackState.Redundant => "tracked by another controller",
        _ => "",
    };
}
