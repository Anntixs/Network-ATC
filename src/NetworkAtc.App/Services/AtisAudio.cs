using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using NAudio.Wave;
using SkyNetwork.Voice;

namespace NetworkAtc.App.Services;

/// <summary>Audio for the voice ATIS: Windows voices, recording from the microphone, WAV files and listening.</summary>
public static class AtisAudio
{
    private static readonly WaveFormat Format = new(AudioFormat.SampleRate, 16, 1);

    /// <summary>Installed Windows voices of a language ("ru", "en"), by name.</summary>
    public static IReadOnlyList<string> Voices(string language)
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            return synth.GetInstalledVoices()
                .Where(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals(language, StringComparison.OrdinalIgnoreCase))
                .Select(v => v.VoiceInfo.Name).ToList();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    /// <summary>The text spoken by a Windows voice as 48 kHz mono samples; empty when there is no voice for the language.</summary>
    public static float[] Speak(string text, string language, string voice, int rate)
    {
        using var synth = new SpeechSynthesizer();
        var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
        var chosen = voices.FirstOrDefault(v => v.Name.Equals(voice, StringComparison.OrdinalIgnoreCase))
                     ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName.Equals(language, StringComparison.OrdinalIgnoreCase));
        if (chosen == null) return [];
        synth.SelectVoice(chosen.Name);
        synth.Rate = Math.Clamp(rate, -10, 10);
        using var stream = new MemoryStream();
        synth.SetOutputToAudioStream(stream, new SpeechAudioFormatInfo(AudioFormat.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak(text);
        synth.SetOutputToNull();
        return ToSamples(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    private static float[] ToSamples(ReadOnlySpan<byte> pcm16)
    {
        var samples = new float[pcm16.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(pcm16[2 * i] | pcm16[2 * i + 1] << 8) / 32768f;
        return samples;
    }

    public static void SaveWav(string path, float[] samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new WaveFileWriter(path, Format);
        writer.WriteSamples(samples, 0, samples.Length);
    }

    /// <summary>A WAV recording as 48 kHz mono samples (other formats are converted); empty when it cannot be read.</summary>
    public static float[] LoadWav(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var reader = new WaveFileReader(path);
            ISampleProvider source = reader.ToSampleProvider();
            if (source.WaveFormat.Channels > 1) source = source.ToMono();
            if (source.WaveFormat.SampleRate != AudioFormat.SampleRate)
                source = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(source, AudioFormat.SampleRate);
            var all = new List<float>();
            var buffer = new float[AudioFormat.SampleRate];
            int n;
            while ((n = source.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.AsSpan(0, n).ToArray());
            return [.. all];
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Plays samples on the default speakers (to listen to the ATIS before it goes on the air).</summary>
    public static void Listen(float[] samples, int outputDevice = -1)
    {
        if (samples.Length == 0) return;
        var output = new WaveOutEvent { DeviceNumber = outputDevice };
        output.Init(new SamplesProvider(samples), convertTo16Bit: true);
        output.PlaybackStopped += (_, _) => output.Dispose();
        output.Play();
    }
}

/// <summary>Plays a fixed buffer of samples once.</summary>
internal sealed class SamplesProvider(float[] samples) : ISampleProvider
{
    private int _position;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        int n = Math.Min(count, samples.Length - _position);
        Array.Copy(samples, _position, buffer, offset, n);
        _position += n;
        return n;
    }
}

/// <summary>Records the controller's voice for the ATIS (48 kHz mono) until <see cref="Stop"/>.</summary>
public sealed class AtisRecorder : IDisposable
{
    private readonly List<float> _samples = [];
    private WaveInEvent? _mic;

    public bool Recording => _mic != null;
    public TimeSpan Length => TimeSpan.FromSeconds(_samples.Count / (double)AudioFormat.SampleRate);

    public void Start(int inputDevice = -1)
    {
        Stop();
        _samples.Clear();
        _mic = new WaveInEvent { DeviceNumber = inputDevice, WaveFormat = new WaveFormat(AudioFormat.SampleRate, 16, 1), BufferMilliseconds = 50 };
        _mic.DataAvailable += (_, e) =>
        {
            lock (_samples)
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2) _samples.Add((short)(e.Buffer[i] | e.Buffer[i + 1] << 8) / 32768f);
        };
        _mic.StartRecording();
    }

    public float[] Stop()
    {
        if (_mic != null)
        {
            try { _mic.StopRecording(); } catch (NAudio.MmException) { }
            _mic.Dispose();
            _mic = null;
        }
        lock (_samples) return [.. _samples];
    }

    public void Dispose() => Stop();
}

public static class AtisAudioInfo
{
    /// <summary>A recording shorter than a second is a slip of the button, not an ATIS.</summary>
    public const int MinSamples = AudioFormat.SampleRate;
}
