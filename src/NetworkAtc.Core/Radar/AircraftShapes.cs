namespace NetworkAtc.Core.Radar;

/// <summary>
/// Aircraft drawn to scale on the ground radar (as the ground radar plugins of EuroScope do): the length and wingspan
/// of the type, and a silhouette built from them. Unknown types take the size of their wake category.
/// </summary>
public static class AircraftShapes
{
    // Length and wingspan, metres.
    private static readonly Dictionary<string, (double Length, double Span)> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A318"] = (31.4, 34.1), ["A319"] = (33.8, 35.8), ["A320"] = (37.6, 35.8), ["A321"] = (44.5, 35.8),
        ["A19N"] = (33.8, 35.8), ["A20N"] = (37.6, 35.8), ["A21N"] = (44.5, 35.8), ["BCS1"] = (35.0, 35.1), ["BCS3"] = (38.7, 35.1),
        ["A306"] = (54.1, 44.8), ["A310"] = (46.7, 43.9), ["A332"] = (58.8, 60.3), ["A333"] = (63.7, 60.3), ["A338"] = (58.8, 64.0),
        ["A339"] = (63.7, 64.0), ["A343"] = (63.7, 60.3), ["A346"] = (75.4, 63.5), ["A359"] = (66.8, 64.8), ["A35K"] = (73.8, 64.8),
        ["A388"] = (72.7, 79.8), ["B736"] = (31.2, 34.3), ["B737"] = (33.6, 35.8), ["B738"] = (39.5, 35.8), ["B739"] = (42.1, 35.8),
        ["B37M"] = (35.6, 35.9), ["B38M"] = (39.5, 35.9), ["B39M"] = (42.2, 35.9), ["B3XM"] = (43.8, 35.9),
        ["B733"] = (33.4, 28.9), ["B734"] = (36.4, 28.9), ["B735"] = (31.0, 28.9), ["B744"] = (70.7, 64.4), ["B748"] = (76.3, 68.4),
        ["B752"] = (47.3, 38.1), ["B753"] = (54.4, 38.1), ["B762"] = (48.5, 47.6), ["B763"] = (54.9, 47.6), ["B764"] = (61.4, 51.9),
        ["B772"] = (63.7, 60.9), ["B77L"] = (63.7, 64.8), ["B77W"] = (73.9, 64.8), ["B778"] = (70.9, 71.8), ["B779"] = (76.7, 71.8),
        ["B788"] = (56.7, 60.1), ["B789"] = (62.8, 60.1), ["B78X"] = (68.3, 60.1), ["MD11"] = (61.6, 51.7), ["MD82"] = (45.1, 32.9),
        ["E170"] = (29.9, 26.0), ["E175"] = (31.7, 26.0), ["E190"] = (36.2, 28.7), ["E195"] = (38.7, 28.7), ["E290"] = (36.2, 33.7),
        ["E295"] = (41.5, 35.1), ["CRJ2"] = (26.8, 21.2), ["CRJ7"] = (32.5, 23.2), ["CRJ9"] = (36.4, 24.9), ["CRJX"] = (39.1, 26.2),
        ["AT43"] = (22.7, 24.6), ["AT45"] = (22.7, 24.6), ["AT72"] = (27.2, 27.1), ["AT76"] = (27.2, 27.1), ["DH8D"] = (32.8, 28.4),
        ["SU95"] = (29.9, 27.8), ["IL96"] = (55.4, 57.7), ["IL76"] = (46.6, 50.5), ["IL62"] = (53.1, 43.2), ["IL18"] = (35.9, 37.4),
        ["T154"] = (47.9, 37.6), ["T134"] = (37.1, 29.0), ["T204"] = (46.1, 42.0), ["YK42"] = (36.4, 34.9), ["YK40"] = (20.4, 25.0),
        ["AN12"] = (33.1, 38.0), ["AN24"] = (23.5, 29.2), ["AN26"] = (23.8, 29.2), ["AN28"] = (13.1, 22.1), ["AN2"] = (12.4, 18.2),
        ["AN124"] = (68.9, 73.3), ["A124"] = (68.9, 73.3), ["AN225"] = (84.0, 88.4), ["A225"] = (84.0, 88.4), ["L410"] = (14.4, 19.5),
        ["C172"] = (8.3, 11.0), ["C152"] = (7.3, 10.1), ["C182"] = (8.8, 11.0), ["C208"] = (11.5, 15.9), ["P28A"] = (7.3, 10.7),
        ["PA28"] = (7.3, 10.7), ["DA40"] = (8.0, 11.9), ["DA42"] = (8.6, 13.4), ["SR22"] = (7.9, 11.7), ["BE20"] = (13.3, 16.6),
        ["PC12"] = (14.4, 16.3), ["TBM9"] = (10.7, 12.8), ["C25A"] = (14.4, 15.7), ["C56X"] = (15.8, 17.2), ["CL35"] = (20.9, 21.0),
        ["GLF6"] = (30.4, 30.4), ["GLEX"] = (30.3, 28.7), ["F2TH"] = (20.2, 19.3), ["F900"] = (20.2, 19.3), ["PC24"] = (16.9, 17.0),
    };

    /// <summary>Length and wingspan of the type in metres.</summary>
    public static (double Length, double Span) Size(string type, char wake)
    {
        type = type.Trim();
        if (Sizes.TryGetValue(type, out var size)) return size;
        return wake switch
        {
            'L' => (9, 11),
            'H' => (64, 62),
            'J' => (73, 80),
            _ => (38, 35),
        };
    }

    /// <summary>
    /// A top-down silhouette, nose first, as points (forward, right) in metres from the middle of the aircraft:
    /// fuselage, swept wings and tailplane.
    /// </summary>
    public static IReadOnlyList<(double Forward, double Right)> Outline(double length, double span)
    {
        double half = length / 2, width = Math.Clamp(length * 0.075, 1.2, 6.5) / 2;
        double wingRoot = half - length * 0.38;          // leading edge where the wing meets the fuselage
        double sweep = span * 0.16;                      // how far back the wing tip sits
        double chordRoot = length * 0.2, chordTip = length * 0.06;
        double tailRoot = -half + length * 0.16, tailSpan = Math.Max(span * 0.34, width * 3) / 2, tailSweep = tailSpan * 0.5;
        var right = new List<(double, double)>
        {
            (half, 0),                                           // nose
            (half - length * 0.06, width * 0.8),
            (half - length * 0.12, width),
            (wingRoot, width),                                   // wing leading edge root
            (wingRoot - sweep, span / 2),                        // tip leading edge
            (wingRoot - sweep - chordTip, span / 2),             // tip trailing edge
            (wingRoot - chordRoot, width),                       // wing trailing edge root
            (tailRoot, width * 0.7),                             // tailplane leading edge root
            (tailRoot - tailSweep, tailSpan),
            (tailRoot - tailSweep - length * 0.04, tailSpan),
            (-half + length * 0.02, width * 0.45),
            (-half, 0),                                          // tail
        };
        // Mirror for the left side, back to front.
        var outline = new List<(double Forward, double Right)>(right);
        for (int i = right.Count - 2; i >= 1; i--) outline.Add((right[i].Item1, -right[i].Item2));
        return outline;
    }
}
