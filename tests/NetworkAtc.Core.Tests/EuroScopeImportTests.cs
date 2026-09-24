using System.Text.Json;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Import;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Tags;

namespace NetworkAtc.Core.Tests;

/// <summary>A EuroScope profile package in a temporary folder, as a vACC would ship it.</summary>
internal sealed class FakeEuroScopePackage : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"natc-es-{Guid.NewGuid():N}");
    public string Prf => Path.Combine(Root, "UUWV.prf");

    public FakeEuroScopePackage()
    {
        Directory.CreateDirectory(Root);
        // The sector sits in "Sector/UUEE.sct" but the profile writes the path in lower case.
        Write("Sector/UUEE.sct", File.ReadAllText(Demo.Path("UUEE-demo.sct")));
        Write("Sector/UUEE.ese", File.ReadAllText(Demo.Path("UUEE-demo.ese")));

        Write("UUWV.prf",
            "Settings\tsector\t\\sector\\uuee.sct",
            "Settings\tSettingsFileSymbology\t\\Settings\\Symbology.txt",
            // An absolute path from the machine that made the package.
            "Settings\tsettingsfileTAGS\tC:\\Users\\Controller\\Documents\\EuroScope\\UUWV\\Settings\\Tags.txt",
            "Settings\tSettingsfileSCREEN\t\\Settings\\Screen.txt",
            "Settings\tSettingsfile\t\\ASR\\..\\Settings\\General.txt",
            "Settings\tSettingsfileVOICE\t\\Settings\\Voice.txt",
            "Settings\taliasfile\t\\Settings\\Alias.txt",
            "Settings\tairlines\t\\ICAO\\ICAO_Airlines.txt",
            "ASRFastKeys\t1\t\\ASR\\APP.asr",
            "ASRFastKeys\t2\t\\ASR\\TWR.asr",
            "ASRFastKeys\t3\t\\ASR\\Missing.asr",
            "Plugins\tPlugin0\t\\Plugins\\TopSky\\TopSky.dll",
            "Plugins\tPlugin0Display\tStandard ES radar screen",
            "Plugins\tPlugin1\t\\Plugins\\GroundRadar\\GRplugin.dll",
            "Plugins\tPlugin2\t\\Plugins\\Aman\\AmanPlugin.dll",
            "Plugins\tPlugin3\t\\Plugins\\CCAMS\\CCAMS.dll",
            "LastSession\tcallsign\tuuee_app",
            "LastSession\trealname\tIvan Petrov",
            "LastSession\tcertificate\t1234567",
            "LastSession\tpassword\tsecret123",
            "LastSession\trating\t5",
            "LastSession\tfacility\t5",
            "LastSession\tserver\tAUTOMATIC",
            "LastSession\tatis2\tSheremetyevo Approach",
            "LastSession\tatis3\tInformation $atiscode(UUEE)",
            "LastSession\tconnecttype\t0",
            "garbage line without tabs",
            "RecentFiles\tRecent1\t\\old.prf");

        Write("Settings/Symbology.txt",
            "SYMBOLOGY",
            "SYMBOLSIZE",
            "Other:background:3289650:3.5:0:0:7",
            "Datablock:Assumed:65280:3.5:0:0:7",
            "Datablock:Transfer to me initiated:255:3.5:0:0:7",
            "Datablock:Non-concerned:12632256:3.5:0:0:7",
            "Sids:line:32768:3.5:0:0:7",
            "Geo:line:8421504:3.5:0:0:7",
            "Controller:ATIS frequency:16777215:3.5:0:0:7",
            "SYMBOL:0",
            "SYMBOLITEM:MOVETO:-3:0",
            "not a symbology line");

        Write("Settings/Tags.txt",
            "TAGFAMILY:Other",
            "TAGTYPE:Tagged",
            "TAGITEM:Callsign:0:No action:No action",
            "TAGFAMILY:UUWV",
            "TAGTYPE:Untagged",
            "TAGITEM:Callsign:0:Assume:Aircraft functions popup",
            "TAGITEM:Altitude:1::",
            "TAGITEM:Ground speed:0::",
            "TAGTYPE:Tagged",
            "TAGITEM:Callsign:0:Assume/Release:Aircraft functions popup",
            "TAGITEM:Aircraft category:0::",
            "TAGITEM:Emergency:0::",
            "TAGITEM:Altitude:1:Cleared altitude popup:Cleared altitude popup",
            "TAGITEM:Vertical speed indicator:0::",
            "TAGITEM:Temporary altitude:0:Cleared altitude popup:Cleared altitude popup",
            "TAGITEM:Assigned heading:1:Assigned heading popup:Assigned heading popup",
            "TAGITEM:Assigned speed:0:Assigned speed popup:Assigned speed popup",
            "TAGITEM:TopSky plugin / SI:0:Some plugin function:",
            "TAGTYPE:Detailed",
            "TAGITEM:1:0:Accept handoff:Open FP dialog",
            "TAGITEM:Plane type:0:Open FP dialog:",
            "TAGITEM:Squawk:0:Squawk assign:Squawk assign",
            "TAGITEM:2:1::",
            "TAGITEM:3:0::",
            "TAGITEM:Destination:0:Route display:",
            "TAGITEM:Scratch pad string:1:Scratchpad edit:Scratchpad edit",
            "TAGTYPE:Primary radar only",
            "TAGITEM:Altitude:0::");

        Write("Settings/General.txt", "m_TransitionAltitude:5000", "m_AutoLoadFlightPlans:1");
        Write("Settings/Screen.txt", "m_HistoryDots:3", "m_PredictionLength:2", "m_ShowGrid:1");
        Write("Settings/Alias.txt",
            "; greetings",
            ".hi Good day, $callsign, radar contact",
            ".rc radar identified",
            "no dot line");

        Write("ASR/APP.asr",
            "DisplayTypeName:Standard ES radar screen",
            "DisplayTypeNeedRadarContent:1",
            "SECTORFILE:\\Sector\\UUEE.sct",
            "TAGFAMILY:UUWV",
            "Airports:UUEE:symbol",
            "Fixes:DEMO1:symbol",
            "Fixes:DEMO1:name",
            "Geo:UUEE GROUND:",
            "Runways:UUEE 06L-24R:centerline",
            "Vors:SHR:symbol",
            "HISTORY_DOTS:8",
            "ABOVE:450",
            "BELOW:0",
            "WINDOWAREA:55.500000:36.900000:56.400000:38.000000");
        Write("ASR/TWR.asr", "DisplayTypeName:Standard ES radar screen", "Airports:UUEE:symbol");

        Write("Plugins/TopSky/TopSky.dll", "MZ");
        Write("Plugins/TopSky/TopSkySettings.txt", "Color_Active_Map=160,160,160", "Setup_Something=1");
        Write("Plugins/TopSky/TopSkyMaps.txt",
            "// Sheremetyevo maps",
            "COLORDEF:Stands:0:128:255",
            "FOLDER:UUEE",
            "MAP:RWY",
            "COLOR:Active_Map",
            "ACTIVE:1",
            "LINE:N055.58.08.000:E037.23.12.000:N055.59.15.000:E037.27.24.000",
            "COORD:DEMO1",
            "COORD:SHR",
            "COORD:N056.00.00.000:E037.30.00.000",
            "COORDLINE",
            "TEXT:SHR:SHEREMETYEVO",
            "MAP:Holding",
            "COLOR:Stands",
            "ACTIVE:RWY:ARR:24L",
            "CIRCLE:DEMO2:5:10",
            "COORD:UNKNOWNFIX",
            "COORDLINE",
            "MAP:Off",
            "ACTIVE:0",
            "LINE:DEMO3:DEMO4",
            "SYMBOL:VOR:SHR:SHR VOR",
            "WEIRDKEY:1");
        Write("Plugins/TopSky/TopSkyAreas.txt",
            "CATEGORYDEF:DANGER:255:0:0",
            "AREA:UUD1:Test danger",
            "CATEGORY:DANGER",
            "LIMITS:0:100",
            "COORD:N055.40.00.000:E037.00.00.000",
            "COORD:N055.40.00.000:E037.10.00.000",
            "COORD:N055.45.00.000:E037.10.00.000",
            "AREA:UUR2",
            "CATEGORY:RESTRICTED",
            "LIMITS:0:UNL",
            "CIRCLE:MR:3",
            "AREA:EMPTY");

        Write("Plugins/GroundRadar/GRplugin.dll", "MZ");
        Write("Plugins/GroundRadar/GRpluginStands.txt",
            "STAND:UUEE:24:N055.58.20.000:E037.24.53.000:20",
            "WTC:LMH",
            "USE:A",
            "STAND:UUEE:25:N055.58.21.000:E037.24.58.000:25",
            "STAND:UUDD:1:N055.24.31.000:E037.54.22.000",
            "COORD:N055.24.31.000:E037.54.22.000",
            "COORD:N055.24.32.000:E037.54.22.000",
            "COORD:N055.24.32.000:E037.54.24.000",
            "STAND:BROKEN");

        Write("Plugins/Aman/AmanPlugin.dll", "MZ");
        Write("Plugins/CCAMS/CCAMS.dll", "MZ");
        Write("Plugins/CCAMS/CCAMS.txt", "APP_RANGE=4201-4277");
    }

    public void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
    }
}

public class EuroScopeImportTests
{
    private static EuroScopeImportResult Import(FakeEuroScopePackage package, Profile? current = null) =>
        EuroScopeImport.Import(package.Prf, current ?? new Profile());

    [Fact]
    public void ParsesProfileWithoutPassword()
    {
        using var package = new FakeEuroScopePackage();
        var prf = EuroScopeProfile.Load(package.Prf);
        Assert.Equal("UUWV", prf.Name);
        Assert.Equal(@"\sector\uuee.sct", prf.SectorFile);
        Assert.Equal(@"\Settings\Symbology.txt", prf.SymbologyFile);
        Assert.Equal(@"\Settings\Alias.txt", prf.AliasFile);
        Assert.Equal(new[] { 1, 2, 3 }, prf.AsrFastKeys.Keys);
        Assert.Equal(4, prf.Plugins.Count);
        Assert.Equal("Standard ES radar screen", prf.Plugins[0].Display);
        Assert.Equal("uuee_app", prf.LastSession["CALLSIGN"]);
        Assert.True(prf.HadPassword);
        Assert.DoesNotContain(prf.LastSession.Keys, k => k.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(prf.LastSession.Values, v => v.Contains("secret123"));
    }

    [Fact]
    public void ResolvesRelativeAbsoluteAndDotDotPathsIgnoringCase()
    {
        using var package = new FakeEuroScopePackage();
        string root = package.Root;
        Assert.Equal(Path.Combine(root, "Sector", "UUEE.sct"), EuroScopePaths.Resolve(root, @"\SECTOR\uuee.SCT"));
        Assert.Equal(Path.Combine(root, "Settings", "Tags.txt"), EuroScopePaths.Resolve(root, @"\ASR\..\settings\tags.txt"));
        Assert.Equal(Path.Combine(root, "Settings", "Tags.txt"), EuroScopePaths.Resolve(root, @"D:\EuroScope\UUWV\Settings\Tags.txt"));
        Assert.Equal(Path.Combine(root, "Plugins", "TopSky"), EuroScopePaths.Resolve(root, @"\plugins\topsky\", directory: true));
        Assert.Null(EuroScopePaths.Resolve(root, @"\Settings\Nothing.txt"));
        Assert.Null(EuroScopePaths.Resolve(root, ""));
    }

    [Fact]
    public void ImportsSectorSessionAndKeepsInputProfile()
    {
        using var package = new FakeEuroScopePackage();
        var current = new Profile();
        var result = Import(package, current);
        var p = result.Profile;

        Assert.NotSame(current, p);
        Assert.Equal("Default", current.Name);
        Assert.Equal("SkyNetwork", current.Theme.Name);
        Assert.Equal("", current.Station.Callsign);

        Assert.Equal("UUWV", p.Name);
        Assert.Equal(Path.Combine(package.Root, "Sector", "UUEE.sct"), result.SectorPath);
        Assert.Equal(Path.Combine(package.Root, "Sector", "UUEE.ese"), result.EsePath);
        Assert.Equal(result.SectorPath, p.SectorFile);
        Assert.NotNull(result.Sector);
        Assert.Equal(8, result.Sector!.Procedures.Count);

        Assert.Equal("UUEE_APP", p.Station.Callsign);
        Assert.Equal(Fsd.Facility.Approach, p.Station.Facility);
        Assert.Equal(5, p.Station.Rating);
        Assert.Equal(1234567, p.Connection.Cid);
        Assert.Equal("Ivan Petrov", p.Connection.RealName);
        Assert.Equal("127.0.0.1", p.Connection.Host); // "AUTOMATIC" is not a server address
        Assert.Equal(["Sheremetyevo Approach", "Information $atiscode(UUEE)"], p.ControllerInfo);
    }

    [Fact]
    public void NeverImportsThePassword()
    {
        using var package = new FakeEuroScopePackage();
        var result = Import(package);
        Assert.Equal("", result.Profile.Connection.ProtectedPassword);
        Assert.DoesNotContain("secret123", JsonSerializer.Serialize(result.Profile));
        Assert.DoesNotContain(result.Report.Notes, n => n.Text.Contains("secret123"));
        Assert.Contains(result.Report.Skipped, s => s.Contains("Пароль"));
    }

    [Fact]
    public void BuildsThemeFromSymbology()
    {
        using var package = new FakeEuroScopePackage();
        var result = Import(package);
        var theme = result.Theme!;
        Assert.Same(theme, result.Profile.Theme);
        Assert.Equal("UUWV", theme.Name);
        Assert.Equal("#323232", theme.RadarBackground);
        Assert.Equal("#00FF00", theme.TagTextTracked);
        Assert.Equal("#00FF00", theme.TargetTracked);
        Assert.Equal("#FF0000", theme.TagTransferToMe);
        Assert.Equal("#C0C0C0", theme.TagText);
        Assert.Equal("#008000", theme.Sid);
        Assert.Equal("#808080", theme.Geo);
        Assert.Equal(new Theme().Runway, theme.Runway); // not in the file: unchanged
        Assert.Contains(result.Report.Skipped, s => s.Contains("Controller:ATIS frequency"));
    }

    [Fact]
    public void MapsTagFamilyOfTheAsrToLayoutsAndClicks()
    {
        using var package = new FakeEuroScopePackage();
        var result = Import(package);
        var tags = result.Profile.Tags;
        Assert.Equal("{callsign}\n{fl} {gs}", tags.Untracked);
        Assert.Equal("{callsign} {wtc}\n{fl} {vs} {cfl}\n{ahdg} {aspd}", tags.Tracked);
        // Detailed lines go under the tag: only what the tracked tag does not show yet.
        Assert.Equal("{type} {squawk}\n{dest}\n{scratch}", tags.Detailed);
        Assert.True(tags.ShowWarnings);

        var clicks = result.Profile.TagClicks;
        // The tracked tag's functions win over the detailed tag's ones.
        Assert.Equal((TagActions.ToggleTrack, TagActions.AircraftMenu), (clicks["callsign"].Left, clicks["callsign"].Right));
        Assert.Equal((TagActions.ClearedLevel, TagActions.ClearedLevel), (clicks["cfl"].Left, clicks["cfl"].Right));
        Assert.Equal(TagActions.Heading, clicks["ahdg"].Left);
        Assert.Equal(TagActions.Speed, clicks["aspd"].Right);
        Assert.Equal(TagActions.Squawk, clicks["squawk"].Left);
        Assert.Equal(TagActions.Route, clicks["dest"].Left);
        Assert.Equal(TagActions.AircraftMenu, clicks["dest"].Right); // no right function: default kept
        Assert.Equal(TagActions.FlightPlan, clicks["type"].Left);
        Assert.Equal(TagActions.Scratchpad, clicks["scratch"].Right);

        Assert.Contains(result.Report.Skipped, s => s.Contains("TopSky plugin / SI") && s.Contains("Some plugin function"));
        Assert.Contains(result.Report.Skipped, s => s.Contains("Primary radar only"));
        Assert.Contains(result.Report.Skipped, s => s.Contains("Other"));
    }

    [Fact]
    public void ImportsSettingsAliasesAndAsr()
    {
        using var package = new FakeEuroScopePackage();
        var result = Import(package);
        var p = result.Profile;
        Assert.Equal(5000, p.TransitionAltitude);
        Assert.Equal(8, p.Targets.HistoryDots); // the ASR overrides Screen.txt
        Assert.Equal(2, p.Targets.PredictionMinutes);
        Assert.Equal(45000, p.Targets.FilterCeiling);
        Assert.Equal(new TargetSettings().FilterFloor, p.Targets.FilterFloor); // BELOW:0 = no filter
        Assert.Contains("Screen.txt: m_ShowGrid", result.SkippedSettings);
        Assert.Contains("General.txt: m_AutoLoadFlightPlans", result.SkippedSettings);

        Assert.Equal("Good day, $callsign, radar contact", p.Aliases[".HI"]);
        Assert.Equal("radar identified", p.Aliases[".rc"]);
        Assert.Equal("contact $1 on $2, good day", p.Aliases[".ctc"]); // existing aliases kept

        Assert.Equal(2, result.AsrFiles.Count);
        Assert.True(p.Layers["FIXES"]);
        Assert.True(p.Layers["FIX NAMES"]);
        Assert.True(p.Layers["GEO"]);
        Assert.True(p.Layers["VOR"]);
        Assert.False(p.Layers["NDB"]);
        Assert.False(p.Layers["SID"]);
        Assert.False(p.Layers["ARTCC"]);
        Assert.True(p.Layers["RANGE RINGS"]); // not an ASR category: unchanged
        Assert.Equal(55.95, p.ViewCenterLatitude, 3);
        Assert.Equal(37.45, p.ViewCenterLongitude, 3);
        Assert.InRange(p.ViewNmPerPixel, 0.03, 0.1);
        Assert.Contains(result.Report.Skipped, s => s.Contains("TWR.asr"));
        Assert.Contains(result.Report.Warnings, s => s.Contains("Missing.asr"));
        Assert.Contains(result.Report.Skipped, s => s.Contains("голосовой"));
        Assert.Contains(result.Report.Skipped, s => s.Contains("airlines"));
    }

    [Fact]
    public void ConvertsTopSkyMapsAndAreas()
    {
        using var package = new FakeEuroScopePackage();
        var result = Import(package);
        var maps = result.Maps;
        var p = result.Profile;

        const string rwy = "TopSky · UUEE · RWY";
        Assert.Equal(3, maps.Lines[rwy].Count); // LINE + two COORDLINE segments through DEMO1 and SHR
        Assert.Equal("#A0A0A0", maps.LayerColors[rwy]);
        Assert.True(p.Layers[rwy]);
        Assert.Equal(56.1667, maps.Lines[rwy][1].From.Latitude, 3); // DEMO1 from the sector file
        Assert.Contains(maps.Labels, l => l is { Text: "SHEREMETYEVO", Group: rwy });

        const string holding = "TopSky · UUEE · Holding";
        Assert.Equal(36, maps.Lines[holding].Count);
        Assert.Equal("#0080FF", maps.LayerColors[holding]);
        Assert.False(p.Layers[holding]); // runway condition: switched off
        Assert.False(p.Layers["TopSky · UUEE · Off"]);
        Assert.Contains(maps.Labels, l => l.Text == "SHR VOR");

        const string danger = "TopSky · Зоны · DANGER";
        Assert.Equal(3, maps.Lines[danger].Count);
        Assert.Equal("#FF0000", maps.LayerColors[danger]);
        Assert.Contains(maps.Labels, l => l is { Text: "UUD1 0–100", Group: danger });
        Assert.Equal(36, maps.Lines["TopSky · Зоны · RESTRICTED"].Count);
        Assert.False(p.Layers[danger]);

        Assert.Contains(result.Report.Imported, s => s.StartsWith("TopSky: 3 карт (включено 1), 2 зон"));
        Assert.Contains(result.Report.Skipped, s => s.Contains("WEIRDKEY"));
        Assert.Contains(result.Report.Skipped, s => s.Contains("условия включения"));
        Assert.Contains(result.Report.Warnings, s => s.Contains("UNKNOWNFIX"));
    }

    [Fact]
    public void ConvertsGroundRadarStandsCcamsAndReportsUnknownPlugins()
    {
        using var package = new FakeEuroScopePackage();
        var result = Import(package);
        var maps = result.Maps;

        Assert.Equal(24, maps.Lines["Стоянки UUEE"].Count); // two 12-segment circles
        Assert.Equal(["24", "25"], maps.Labels.Where(l => l.Group == "Стоянки UUEE").Select(l => l.Text));
        Assert.Equal(3, maps.Lines["Стоянки UUDD"].Count); // outline from COORD lines
        Assert.True(result.Profile.Layers["Стоянки UUEE"]);
        Assert.Contains(result.Report.Imported, s => s.Contains("UUEE (2)") && s.Contains("UUDD (1)"));

        Assert.Equal("4201-4277", result.Profile.SquawkRange);
        Assert.Contains(result.Report.Skipped, s => s == "Плагин AmanPlugin.dll не поддерживается");
    }

    [Fact]
    public void MergesPluginMapsIntoSectorAndSavesNativeSector()
    {
        using var package = new FakeEuroScopePackage();
        var result = Import(package);
        Assert.Contains("TopSky · UUEE · RWY", result.Sector!.CustomLayers);
        Assert.Contains(result.Sector.Labels, l => l.Text == "SHEREMETYEVO");
        Assert.Equal(result.Sector.Lines["Стоянки UUEE"].Count, result.Maps.Lines["Стоянки UUEE"].Count);

        var path = Path.Combine(package.Root, "UUWV" + NativeSector.Extension);
        result.SaveNativeSector(path);
        Assert.Equal(path, result.Profile.SectorFile);
        var loaded = NativeSector.Load(path);
        Assert.Equal(8, loaded.Procedures.Count);
        Assert.Contains("TopSky · Зоны · DANGER", loaded.CustomLayers);
        Assert.Equal("#FF0000", loaded.LayerColors["TopSky · Зоны · DANGER"]);
        Assert.Contains(loaded.Labels, l => l is { Text: "24", Group: "Стоянки UUEE" });
    }

    [Fact]
    public void BrokenPackageIsReportedNotThrown()
    {
        using var package = new FakeEuroScopePackage();
        package.Write("Broken.prf",
            "Settings\tsector\t\\nowhere\\X.sct",
            "Settings\tSettingsfileTAGS\t\\Settings\\Screen.txt",
            "Settings\tSettingsfileSYMBOLOGY\t\\Settings\\Alias.txt",
            "Settings\taliasfile\t\\Settings\\Symbology.txt",
            "Plugins\tPlugin0\t\\Missing\\TopSky.dll",
            "Plugins\tPluginX\tnonsense",
            "ASRFastKeys\tone\t\\ASR\\APP.asr",
            "\t\t\t",
            "LastSession\tcertificate\tnot a number");
        var result = EuroScopeImport.Import(Path.Combine(package.Root, "Broken.prf"), new Profile());
        Assert.Null(result.SectorPath);
        Assert.Null(result.Sector);
        Assert.Null(result.Theme);
        Assert.Equal(0, result.Profile.Connection.Cid);
        Assert.Empty(result.Maps.Lines);
        Assert.Contains(result.Report.Warnings, s => s.Contains("X.sct"));
        Assert.Contains(result.Report.Warnings, s => s.Contains("TopSkyMaps.txt"));
        Assert.Equal(TagLayouts.DefaultTracked, result.Profile.Tags.Tracked);

        var path = Path.Combine(package.Root, "MapsOnly" + NativeSector.Extension);
        result.SaveNativeSector(path);
        Assert.Equal("Broken", NativeSector.Load(path).Name);
    }

    [Fact]
    public void MissingPrfThrows()
    {
        Assert.Throws<FileNotFoundException>(() => EuroScopeImport.Import(Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}.prf"), new Profile()));
    }

    [Fact]
    public void ReadsNumericTagCodesAndTagLines()
    {
        var families = EuroScopeTags.Parse("TAGTYPE:2\nTAGITEM:1:0:0:0\nTAGLINE\nTAGITEM:2:0:0:0\nTAGITEM:unknown thing:0:0:0\ngarbage");
        var profile = new Profile();
        var skipped = new List<string>();
        var states = EuroScopeTags.ApplyTo(Assert.Single(families), profile, skipped, out int clicks);
        Assert.Equal([EsTagState.Tracked], states);
        Assert.Equal("{callsign}\n{fl}", profile.Tags.Tracked);
        Assert.Equal(0, clicks);
        Assert.Equal(Profile.DefaultTagClicks()["callsign"].Left, profile.TagClicks["callsign"].Left);
        Assert.Contains("элемент тега «unknown thing»", skipped);
    }
}
