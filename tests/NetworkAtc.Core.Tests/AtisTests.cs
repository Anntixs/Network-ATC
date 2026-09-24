using NetworkAtc.Core.Atis;
using NetworkAtc.Core.Customization;
using NetworkAtc.Core.Fsd;
using NetworkAtc.Core.Sectors;
using NetworkAtc.Core.Weather;
using SkyNetwork.Voice;

namespace NetworkAtc.Core.Tests;

public class AtisTests
{
    private static readonly Metar Uuee = MetarParser.Parse("UUEE 241230Z 27008MPS 9999 -SHRA BKN020CB OVC100 15/10 Q1013 NOSIG")!;

    [Fact]
    public void LettersCallsignsAndRunways()
    {
        Assert.Equal("B", AtisText.Next("A"));
        Assert.Equal("A", AtisText.Next("Z"));
        Assert.Equal("A", AtisText.Next(""));
        Assert.Equal("UUEE_ATIS", AtisText.Callsign("uuee", ""));
        Assert.Equal("UUEE_A_ATIS", AtisText.Callsign("UUEE", "a"));
        Assert.Equal("Браво", AtisText.Phonetic("b", russian: true));
        Assert.Equal("X-ray", AtisText.Phonetic("X", russian: false));
        Assert.Equal("24 правая", AtisText.Runway("24R", russian: true));
        Assert.Equal("6 left", AtisText.Runway("06L", russian: false));
    }

    [Fact]
    public void SpeechFromTheMetar_InRussianAndEnglish()
    {
        var rwy = new RunwayUse { Departure = ["24R"], Arrival = ["24L"] };
        var ru = AtisText.Speech("Шереметьево", "B", new DateTime(2026, 9, 24, 12, 30, 0), rwy, Uuee, "Рулёжная дорожка B закрыта", russian: true);
        Assert.StartsWith("Шереметьево, информация Браво, время 1230.", ru);
        Assert.Contains("Посадка на полосу 24 левая. Взлёт с полосы 24 правая.", ru);
        Assert.Contains("Ветер 270 градусов 8 метра в секунду.", ru);
        Assert.Contains("Видимость более 10 километров.", ru);
        Assert.Contains("слабый ливневый дождь.", ru);
        Assert.Contains("Облачность значительная кучево-дождевая 610 метров, сплошная 3050 метров.", ru);
        Assert.Contains("Температура 15, точка росы 10.", ru);
        Assert.Contains("Давление QNH 1013 гектопаскалей.", ru);
        Assert.Contains("Рулёжная дорожка B закрыта.", ru);
        Assert.EndsWith("Сообщите диспетчеру о получении информации Браво.", ru);

        var en = AtisText.Speech("UUEE", "C", new DateTime(2026, 9, 24, 12, 30, 0), new RunwayUse { Departure = ["24"], Arrival = ["24"] },
            MetarParser.Parse("UUEE 241230Z 00000KT CAVOK M02/M05 Q1030")!, "", russian: false);
        Assert.Contains("UUEE information Charlie, time 1230.", en);
        Assert.Contains("Runway in use 24.", en);
        Assert.Contains("Wind calm.", en);
        Assert.Contains("CAVOK.", en);
        Assert.Contains("Temperature minus 2, dew point minus 5.", en);
        Assert.Contains("QNH 1030.", en);
    }

    [Fact]
    public void Service_ExpandsTheTextPerAirport_AndMovesTheLetterOnANewMetar()
    {
        var profile = new Profile();
        profile.Atis.Add(new AtisSettings { Airport = "UUEE", Frequency = "128.050", Remark = "ПТИЦЫ" });
        var metars = new Dictionary<string, Metar> { ["UUEE"] = Uuee };
        var service = new AtisService(() => profile,
            line => line.Replace("$atiscode(UUEE)", profile.AtisLetters.GetValueOrDefault("UUEE", "")).Replace("$metar(UUEE)", metars["UUEE"].Raw)
                        .Replace("$time", "1230").Replace("$deprwy(UUEE)", "24R").Replace("$arrrwy(UUEE)", "24L"),
            s => metars.GetValueOrDefault(s));
        var changed = new List<string>();
        service.LetterChanged += (_, a) => changed.Add(a);

        service.SetLetter("uuee", "A");
        var text = service.Text(profile.Atis[0]);
        Assert.Equal("UUEE ATIS ИНФОРМАЦИЯ A 1230", text[0]);
        Assert.Equal("ВПП ВЗЛЁТ 24R ПОСАДКА 24L", text[1]);
        Assert.Equal(Uuee.Raw, text[2]);
        Assert.Equal("ПТИЦЫ", text[^1]);

        service.OnMetar(Uuee);                  // the first METAR seen: no change
        service.OnMetar(Uuee);                  // the same again: no change
        Assert.Equal("A", service.Letter("UUEE"));
        service.OnMetar(MetarParser.Parse("UUEE 241300Z 27010MPS 9999 BKN020 15/10 Q1012")!);
        Assert.Equal("B", service.Letter("UUEE"));
        profile.Atis[0].AutoLetter = false;
        service.OnMetar(MetarParser.Parse("UUEE 241330Z 27010MPS 9999 BKN020 15/10 Q1011")!);
        Assert.Equal("B", service.Letter("UUEE"));
        Assert.Equal(["UUEE", "UUEE"], changed);
        Assert.Contains("информация Браво", service.Speech(profile.Atis[0]));
    }

    [Fact]
    public void Station_AnswersAtisRequestsAddressedToIt()
    {
        var station = new AtisStation("UUEE_ATIS", () => ["UUEE ATIS INFORMATION A", "QNH 1013"]);
        var reply = station.Reply(FsdPacket.Parse("$CQAFL123:UUEE_ATIS:ATIS")!).ToList();
        Assert.Equal(["$CRUUEE_ATIS:AFL123:ATIS:T:UUEE ATIS INFORMATION A", "$CRUUEE_ATIS:AFL123:ATIS:T:QNH 1013", "$CRUUEE_ATIS:AFL123:ATIS:E:3"], reply);
        Assert.Equal(["$POUUEE_ATIS:AFL123:42"], station.Reply(FsdPacket.Parse("$PIAFL123:UUEE_ATIS:42")!));
        Assert.Empty(station.Reply(FsdPacket.Parse("#TMAFL123:UUEE_ATIS:hello")!));
    }

    [Fact]
    public void ProfileKeepsAtisStations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"natc-{Guid.NewGuid():N}.json");
        try
        {
            var p = new Profile();
            p.Atis.Add(new AtisSettings { Airport = "UUEE", Frequency = "128.050", Voice = AtisVoiceMode.Recording, Language = "en" });
            p.Save(path);
            var q = Profile.Load(path);
            var a = Assert.Single(q.Atis);
            Assert.Equal(("UUEE_ATIS", "128.050", AtisVoiceMode.Recording, "en"), (a.Callsign, a.Frequency, a.Voice, a.Language));
            Assert.Equal(AtisSettings.DefaultText, a.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeSender : IAudioSender
    {
        public List<(uint Seq, bool Last)> Frames { get; } = [];
        public bool IsConnected => true;
        public void SendAudio(uint sequence, bool last, ReadOnlySpan<byte> transmitterIds, ReadOnlySpan<byte> opus) => Frames.Add((sequence, last));
    }

    [Fact]
    public void Broadcaster_PlaysTheRecordingInALoopInRealTime()
    {
        var sender = new FakeSender();
        var b = new AtisBroadcaster(sender, [0]) { Pause = TimeSpan.FromMilliseconds(100) };
        b.SetRecording(new float[AudioFormat.FrameSamples * 5]); // 100 ms
        b.Pump(TimeSpan.FromMilliseconds(80));                     // frames due at 0, 20, 40, 60, 80
        Assert.Equal(5, sender.Frames.Count(f => !f.Last));
        Assert.Single(sender.Frames, f => f.Last);                  // the recording ended: closing frame
        b.Pump(TimeSpan.FromMilliseconds(180));                    // the pause: nothing on the air
        Assert.Equal(6, sender.Frames.Count);
        b.Pump(TimeSpan.FromMilliseconds(200));                    // the next repeat starts
        Assert.Equal(7, sender.Frames.Count);
        Assert.True(b.OnAir);
        b.Stop();
        Assert.False(b.OnAir);
        Assert.True(sender.Frames[^1].Last);
    }
}
