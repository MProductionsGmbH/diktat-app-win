using System.IO;
using Diktat.Transcription.Audio;
using NAudio.Wave;

namespace Diktat.Windows.Runtime;

/// Audio formats Windows decodes itself through Media Foundation: Voice Memos from the iPhone
/// (m4a/AAC), videos (mp4), WMA, FLAC, and MP3 as a fallback for files the built-in decoder rejects.
public sealed class MediaFoundationDecoder : IAudioFileDecoder
{
    public static readonly IReadOnlyList<string> Extensions =
        [".m4a", ".aac", ".adts", ".mp4", ".m4v", ".mov", ".3gp", ".3g2", ".wma", ".wmv", ".asf", ".flac", ".mp3", ".amr"];

    public bool CanDecode(string path, ReadOnlySpan<byte> header)
    {
        if (Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return true;
        // An MP4 container under another name: "ftyp" at byte 4.
        return header.Length >= 8 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p';
    }

    public IAudioSampleReader Open(string path)
    {
        try
        {
            var reader = new MediaFoundationReader(path);
            return new Reader(reader);
        }
        catch (Exception ex) when (ex is not AudioDecodeException)
        {
            throw AudioDecodeException.Unreadable(ex);
        }
    }

    private sealed class Reader : IAudioSampleReader
    {
        private readonly MediaFoundationReader _reader;
        private readonly ISampleProvider _samples;
        private float[] _buffer = [];

        public Reader(MediaFoundationReader reader)
        {
            _reader = reader;
            _samples = reader.ToSampleProvider();
        }

        public int SampleRate => _samples.WaveFormat.SampleRate;
        public int Channels => _samples.WaveFormat.Channels;

        public int Read(Span<float> buffer)
        {
            int channels = Math.Max(1, Channels);
            int wanted = buffer.Length - buffer.Length % channels;
            if (wanted == 0) return 0;
            if (_buffer.Length < wanted) _buffer = new float[wanted];
            int total = 0;
            while (total < wanted)
            {
                int read = _samples.Read(_buffer, total, wanted - total);
                if (read <= 0) break;
                total += read;
            }
            total -= total % channels;
            _buffer.AsSpan(0, total).CopyTo(buffer);
            return total;
        }

        public void Dispose() => _reader.Dispose();
    }
}
