using System.IO;
using System.Media;
using NetworkAtc.Core.Customization;

namespace NetworkAtc.App.Services;

public enum SoundEvent
{
    RadioMessage,
    PrivateMessage,
    HandoffRequest,
    HandoffAccepted,
    HandoffRefused,
    PointOut,
    ConflictAlert,
    Connected,
    Disconnected,
}

/// <summary>
/// Event sounds. The tones are synthesized at start-up (short sine chimes), so the program ships no
/// audio files; which events sound is set in the profile.
/// </summary>
public sealed class SoundService
{
    private const int Rate = 22050;

    private static readonly Dictionary<SoundEvent, (int Hz, int Ms)[]> Patterns = new()
    {
        [SoundEvent.RadioMessage] = [(1200, 45)],
        [SoundEvent.PrivateMessage] = [(880, 90), (1320, 130)],
        [SoundEvent.HandoffRequest] = [(988, 110), (0, 50), (988, 110), (0, 50), (1319, 170)],
        [SoundEvent.HandoffAccepted] = [(784, 90), (1175, 150)],
        [SoundEvent.HandoffRefused] = [(660, 120), (440, 220)],
        [SoundEvent.PointOut] = [(1047, 80), (0, 40), (1047, 80)],
        [SoundEvent.ConflictAlert] = [(1400, 150), (0, 60), (1400, 150), (0, 60), (1400, 150)],
        [SoundEvent.Connected] = [(523, 90), (659, 90), (784, 150)],
        [SoundEvent.Disconnected] = [(784, 90), (659, 90), (523, 150)],
    };

    private readonly Func<SoundSettings> _settings;
    private readonly Dictionary<(SoundEvent, int), byte[]> _cache = [];
    private readonly Dictionary<SoundEvent, DateTime> _last = [];

    public SoundService(Func<SoundSettings> settings) => _settings = settings;

    public void Play(SoundEvent e)
    {
        var s = _settings();
        if (!s.Enabled || !IsOn(s, e)) return;
        // The same sound at most twice a second (a burst of radio messages is one chime).
        var now = DateTime.UtcNow;
        if (_last.TryGetValue(e, out var last) && now - last < TimeSpan.FromMilliseconds(500)) return;
        _last[e] = now;
        int volume = (int)Math.Round(Math.Clamp(s.Volume, 0, 1) * 20);
        if (volume == 0) return;
        if (!_cache.TryGetValue((e, volume), out var wav)) _cache[(e, volume)] = wav = Synthesize(Patterns[e], volume / 20.0);
        try { new SoundPlayer(new MemoryStream(wav)).Play(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // No audio device: stay silent.
        }
    }

    private static bool IsOn(SoundSettings s, SoundEvent e) => e switch
    {
        SoundEvent.RadioMessage => s.RadioMessage,
        SoundEvent.PrivateMessage => s.PrivateMessage,
        SoundEvent.HandoffRequest => s.HandoffRequest,
        SoundEvent.HandoffAccepted => s.HandoffAccepted,
        SoundEvent.HandoffRefused => s.HandoffRefused,
        SoundEvent.PointOut => s.PointOut,
        SoundEvent.ConflictAlert => s.ConflictAlert,
        _ => s.Connection,
    };

    /// <summary>16-bit mono PCM WAV of the tone sequence (0 Hz = pause), with 5 ms fades against clicks.</summary>
    public static byte[] Synthesize(IReadOnlyList<(int Hz, int Ms)> tones, double volume)
    {
        var samples = new List<short>();
        foreach (var (hz, ms) in tones)
        {
            int n = Rate * ms / 1000, fade = Math.Min(n / 2, Rate / 200);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - 1 - i) / (double)Math.Max(1, fade));
                double v = hz == 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / Rate) * env * volume * 0.6;
                samples.Add((short)(v * short.MaxValue));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        int dataBytes = samples.Count * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(Rate);
        w.Write(Rate * 2);      // byte rate
        w.Write((short)2);      // block align
        w.Write((short)16);     // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var sample in samples) w.Write(sample);
        w.Flush();
        return ms2.ToArray();
    }
}
