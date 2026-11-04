using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace Diktat.Transcription.Tests;

/// Writes a small Opus-in-Ogg file with the managed Concentus encoder, like a voice message.
internal static class OpusTestFile
{
    public static void Write(string path, float[] interleaved48k, int channels)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48_000, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 32_000;
        using var file = File.Create(path);
        var ogg = new OpusOggWriteStream(encoder, file);
        var pcm = interleaved48k.Select(s => (short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue)).ToArray();
        ogg.WriteSamples(pcm, 0, pcm.Length);
        ogg.Finish();
    }
}
