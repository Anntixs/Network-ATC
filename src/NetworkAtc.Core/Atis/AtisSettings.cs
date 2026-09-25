namespace NetworkAtc.Core.Atis;

/// <summary>How the ATIS is heard on its frequency.</summary>
public enum AtisVoiceMode
{
    /// <summary>Text only: pilots read it on request.</summary>
    None,
    /// <summary>A recording made by the controller, played in a loop.</summary>
    Recording,
    /// <summary>Spoken by a Windows voice from the METAR, runways and letter.</summary>
    Speech,
}

/// <summary>One ATIS station of the controller, e.g. UUEE_ATIS on 128.050.</summary>
public sealed class AtisSettings
{
    public string Airport { get; set; } = "";
    /// <summary>Optional part between the airport and _ATIS: "A" (arrival) makes UUEE_A_ATIS.</summary>
    public string Suffix { get; set; } = "";
    public string Frequency { get; set; } = "";
    /// <summary>The airport's spoken name, e.g. "Sheremetyevo" (the ICAO code when empty).</summary>
    public string SpokenName { get; set; } = "";
    /// <summary>Text lines sent to pilots on request; alias variables work, $airport is this ATIS's airport.</summary>
    public List<string> Text { get; set; } = [.. DefaultText];
    /// <summary>A remark added to the end of the text and of the spoken ATIS (NOTAM, works on the aerodrome).</summary>
    public string Remark { get; set; } = "";
    /// <summary>Next letter by itself when a new METAR comes in.</summary>
    public bool AutoLetter { get; set; } = true;
    public AtisVoiceMode Voice { get; set; } = AtisVoiceMode.Speech;
    /// <summary>"ru" or "en" for the spoken ATIS.</summary>
    public string Language { get; set; } = "en";
    /// <summary>Name of the Windows voice; empty — the first voice of the language.</summary>
    public string SpeechVoice { get; set; } = "";
    /// <summary>Speech rate, -10 (slow) … 10 (fast).</summary>
    public int SpeechRate { get; set; } = -1;
    /// <summary>The recording (WAV, 48 kHz mono) for <see cref="AtisVoiceMode.Recording"/>.</summary>
    public string RecordingFile { get; set; } = "";

    public static readonly string[] DefaultText =
    [
        "$airport ATIS INFORMATION $atiscode($airport) $time",
        "DEPARTURE RWY $deprwy($airport) ARRIVAL RWY $arrrwy($airport)",
        "$metar($airport)",
        "ADVISE ON INITIAL CONTACT YOU HAVE INFORMATION $atiscode($airport)",
    ];

    public string Callsign => AtisText.Callsign(Airport, Suffix);
}
