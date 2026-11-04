using System.IO;
using System.Media;

namespace Diktat.Windows.Platform;

/// The soft "plop" when a recording starts (and a lower one when it stops), so you hear that
/// Diktat listens before you speak. Uses Assets\start.wav next to the exe when present (16-bit
/// PCM is attenuated to 60 % like on the Mac), otherwise a generated tone.
///
/// Plays asynchronously (PlaySound); any thread. Play the start sound before muting the output
/// with <see cref="SystemAudioMuter"/>, and mute after <see cref="StartDuration"/>, or the
/// sound is cut off.
public static class RecordingSound
{
    private const float Volume = 0.6f;
    private static readonly object Gate = new();
    private static SoundPlayer? _start;
    private static SoundPlayer? _stop;

    public static TimeSpan StartDuration { get; private set; } = TimeSpan.FromMilliseconds(120);

    public static void PlayStart() => Play(ref _start, LoadStart);

    public static void PlayStop() => Play(ref _stop, () => Tone(frequencyFrom: 520, frequencyTo: 380, milliseconds: 90));

    private static void Play(ref SoundPlayer? player, Func<byte[]> load)
    {
        try
        {
            lock (Gate)
            {
                if (player is null)
                {
                    player = new SoundPlayer(new MemoryStream(load()));
                    player.Load();
                }
                player.Play();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Playing the recording sound failed", ex);
        }
    }

    private static byte[] LoadStart()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "start.wav");
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            var scaled = Attenuate(bytes, Volume, out var duration);
            if (duration > TimeSpan.Zero) StartDuration = duration;
            return scaled;
        }
        StartDuration = TimeSpan.FromMilliseconds(110);
        return Tone(frequencyFrom: 420, frequencyTo: 640, milliseconds: 110);
    }

    /// Scales the samples of a 16-bit PCM WAV; returns other formats unchanged.
    internal static byte[] Attenuate(byte[] wav, float factor, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        try
        {
            if (wav.Length < 44 || BitConverter.ToUInt32(wav, 0) != 0x46464952 || BitConverter.ToUInt32(wav, 8) != 0x45564157) return wav;
            int position = 12;
            int bits = 0, channels = 0, rate = 0, format = 0;
            var result = (byte[])wav.Clone();
            while (position + 8 <= wav.Length)
            {
                uint id = BitConverter.ToUInt32(wav, position);
                int size = BitConverter.ToInt32(wav, position + 4);
                int body = position + 8;
                if (size < 0 || body + size > wav.Length) size = wav.Length - body;
                if (id == 0x20746D66) // "fmt "
                {
                    format = BitConverter.ToUInt16(wav, body);
                    channels = BitConverter.ToUInt16(wav, body + 2);
                    rate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToUInt16(wav, body + 14);
                }
                else if (id == 0x61746164) // "data"
                {
                    if (rate > 0 && channels > 0 && bits > 0)
                        duration = TimeSpan.FromSeconds((double)size / (rate * channels * (bits / 8)));
                    if (format == 1 && bits == 16)
                    {
                        for (int i = body; i + 1 < body + size; i += 2)
                        {
                            short sample = BitConverter.ToInt16(wav, i);
                            short value = (short)Math.Clamp(sample * factor, short.MinValue, short.MaxValue);
                            result[i] = (byte)(value & 0xFF);
                            result[i + 1] = (byte)((value >> 8) & 0xFF);
                        }
                    }
                    return result;
                }
                position = body + size + (size & 1);
            }
            return wav;
        }
        catch
        {
            return wav;
        }
    }

    /// A short sine glide with a soft attack and an exponential decay, as 16-bit mono WAV.
    internal static byte[] Tone(double frequencyFrom, double frequencyTo, int milliseconds)
    {
        const int rate = 44_100;
        int count = rate * milliseconds / 1000;
        var samples = new short[count];
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / count;
            double frequency = frequencyFrom + (frequencyTo - frequencyFrom) * t;
            phase += 2 * Math.PI * frequency / rate;
            double attack = Math.Min(1.0, i / (rate * 0.004));
            double envelope = attack * Math.Exp(-5.0 * t);
            samples[i] = (short)(Math.Sin(phase) * envelope * 0.35 * Volume * short.MaxValue);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + count * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(count * 2);
        foreach (var sample in samples) writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }
}
