using NetworkAtc.Core.Customization;

namespace NetworkAtc.Core.Tests;

public class TagLayoutTests
{
    [Fact]
    public void Version4Profile_GetsTheEuroScopeTags_ButKeepsItsOwnLayouts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"natc-{Guid.NewGuid():N}.json");
        try
        {
            string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
            File.WriteAllText(path, $$"""
                { "Version": 4, "Tags": { "Untracked": {{Json(TagLayouts.Version3Defaults[0])}}, "Tracked": "{callsign} mine",
                  "Detailed": {{Json(TagLayouts.Version3Defaults[2])}}, "ShowWarnings": false } }
                """);
            var p = Profile.Load(path);
            Assert.Equal(TagLayouts.DefaultUntracked, p.Tags.Untracked);
            Assert.Equal("{callsign} mine", p.Tags.Tracked);
            Assert.Equal(TagLayouts.DefaultDetailed, p.Tags.Detailed);
            Assert.True(p.Tags.ShowWarnings);
            Assert.Equal(Profile.CurrentVersion, p.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultLayouts_KeepTheFirstLineInPlace()
    {
        // The detailed lines are added under the tag, so the callsign line is the same whether the mouse is over it or not.
        Assert.StartsWith("{callsign}", TagLayouts.DefaultTracked);
        Assert.StartsWith("{callsign}", TagLayouts.DefaultUntracked);
        Assert.DoesNotContain("{callsign}", TagLayouts.DefaultDetailed);
        Assert.DoesNotContain("{warn}", TagLayouts.DefaultTracked);
    }
}

public class NativeSectorDefaultsTests
{
    [Fact]
    public void SectorWithoutCeiling_ReachesTheTop_AndProcedureNamesAreCapitals()
    {
        var s = NetworkAtc.Core.Sectors.NativeSector.Parse("""
            { "format": "network-atc-sector", "version": 1, "sectors": [ { "name": "CTR", "floor": 0, "owners": ["DC"], "borders": [] } ],
              "procedures": [ { "kind": "SID", "airport": "uuee", "runway": "24r", "name": "demo1a", "route": ["demo"] } ] }
            """);
        Assert.Equal(99999, Assert.Single(s.Sectors).Ceiling);
        var p = Assert.Single(s.Procedures);
        Assert.Equal(("UUEE", "24R", "DEMO1A", "DEMO"), (p.Airport, p.Runway, p.Name, p.Route[0]));
    }
}
