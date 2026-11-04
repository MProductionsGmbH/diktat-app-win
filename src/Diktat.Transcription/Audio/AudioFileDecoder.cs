using System.Text;
using Concentus;
using Concentus.Oggfile;
using NLayer;

namespace Diktat.Transcription.Audio;

/// Interleaved float samples from an audio file, block by block.
public interface IAudioSampleReader : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// Fills <paramref name="buffer"/> with interleaved samples and returns how many it wrote
    /// (a multiple of <see cref="Channels"/>); 0 at the end.
    int Read(Span<float> buffer);
}

/// One file format. The Windows app adds a Media Foundation decoder for m4a, aac, mp4 and
/// wma through <see cref="AudioFileDecoder.Register"/>.
public interface IAudioFileDecoder
{
    /// Whether this decoder reads the file, judged by its name and its first bytes (up to 512).
    bool CanDecode(string path, ReadOnlySpan<byte> header);

    /// Opens the file; throws <see cref="AudioDecodeException"/> when it cannot.
    IAudioSampleReader Open(string path);
}

/// Turns dropped audio files into the 16 kHz mono samples the speech models expect:
/// WAV, MP3 and Opus in Ogg (WhatsApp, Telegram and Signal voice messages) on any OS, plus
/// whatever decoders the app registers.
public sealed class AudioFileDecoder
{
    private readonly List<IAudioFileDecoder> _decoders = [new WavDecoder(), new Mp3Decoder(), new OggOpusDecoder()];
    private readonly Lock _lock = new();

    public AudioFileDecoder(params IEnumerable<IAudioFileDecoder> additional)
    {
        _decoders.AddRange(additional);
    }

    /// The extensions of the built-in decoders, for file dialogs.
    public static IReadOnlyList<string> BuiltInExtensions { get; } = [".wav", ".wave", ".mp3", ".ogg", ".opus", ".oga"];

    /// Adds a decoder that is tried after the built-in ones (and after those registered earlier).
    public void Register(IAudioFileDecoder decoder)
    {
        lock (_lock) _decoders.Add(decoder);
    }

    /// Opens the file with the first decoder that accepts it.
    public IAudioSampleReader Open(string path)
    {
        if (!File.Exists(path)) throw AudioDecodeException.Unreadable(new FileNotFoundException(path));
        var header = ReadHeader(path);
        List<IAudioFileDecoder> decoders;
        lock (_lock) decoders = [.. _decoders];

        Exception? lastError = null;
        foreach (var decoder in decoders)
        {
            if (!decoder.CanDecode(path, header)) continue;
            try
            {
                return decoder.Open(path);
            }
            catch (Exception error) when (error is AudioDecodeException or InvalidDataException or IOException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
            {
                lastError = error;
            }
        }
        throw lastError as AudioDecodeException ?? AudioDecodeException.Unreadable(lastError);
    }

    /// The whole file at its own rate and channel count.
    public AudioData Decode(string path, CancellationToken ct = default)
    {
        using var reader = Open(path);
        return ReadAll(reader, ct);
    }

    /// The whole file as 16 kHz mono; long files are converted block by block.
    public float[] DecodeToMono16k(string path, CancellationToken ct = default)
    {
        using var reader = Open(path);
        return ReadMono16k(reader, ct);
    }

    public Task<float[]> DecodeToMono16kAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => DecodeToMono16k(path, ct), ct);

    public static AudioData ReadAll(IAudioSampleReader reader, CancellationToken ct)
    {
        var channels = Math.Max(1, reader.Channels);
        var output = new FloatBuffer(1 << 16);
        var buffer = new float[4096 * channels];
        try
        {
            int read;
            while ((read = reader.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.AddRange(buffer.AsSpan(0, read));
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException)
        {
            // A damaged end (cut-off download, crash) still gives what was readable.
            if (output.Count == 0) throw AudioDecodeException.Unreadable(error);
        }
        if (output.Count == 0) throw AudioDecodeException.Empty();
        return new AudioData(output.ToArray(), reader.SampleRate, channels);
    }

    public static float[] ReadMono16k(IAudioSampleReader reader, CancellationToken ct)
    {
        var channels = Math.Max(1, reader.Channels);
        if (reader.SampleRate <= 0) throw AudioDecodeException.Unreadable();
        var resampler = new StreamingResampler(reader.SampleRate, AudioResampler.TargetSampleRate);
        var output = new FloatBuffer(1 << 16);
        var buffer = new float[8192 * channels];
        var mono = new float[8192];
        var any = false;
        try
        {
            int read;
            while ((read = reader.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                any = true;
                var frames = read / channels;
                if (channels == 1)
                {
                    resampler.Process(buffer.AsSpan(0, frames), output);
                    continue;
                }
                var scale = 1f / channels;
                for (var frame = 0; frame < frames; frame++)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++) sum += buffer[frame * channels + c];
                    mono[frame] = sum * scale;
                }
                resampler.Process(mono.AsSpan(0, frames), output);
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException)
        {
            if (!any) throw AudioDecodeException.Unreadable(error);
        }
        resampler.Flush(output);
        if (output.Count == 0) throw AudioDecodeException.Empty();
        return output.ToArray();
    }

    private static byte[] ReadHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[(int)Math.Min(512, stream.Length)];
            stream.ReadExactly(header);
            return header;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw AudioDecodeException.Unreadable(error);
        }
    }

    internal static bool HasExtension(string path, params string[] extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static int IndexOf(ReadOnlySpan<byte> header, string ascii) => header.IndexOf(Encoding.ASCII.GetBytes(ascii));
}

/// RIFF/WAVE files (see <see cref="WavFile"/>).
public sealed class WavDecoder : IAudioFileDecoder
{
    public bool CanDecode(string path, ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8);

    public IAudioSampleReader Open(string path) => WavSampleReader.Open(File.OpenRead(path));
}

/// MPEG audio layer III (and I/II) with the managed NLayer decoder, so it needs no codec
/// from the system.
public sealed class Mp3Decoder : IAudioFileDecoder
{
    public bool CanDecode(string path, ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[..3].SequenceEqual("ID3"u8)) return true;
        // An MPEG audio frame sync; layer bits 00 would be AAC (ADTS), which this is not.
        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0 && ((header[1] >> 1) & 0b11) != 0) return true;
        return AudioFileDecoder.HasExtension(path, ".mp3", ".mp2", ".mpga");
    }

    public IAudioSampleReader Open(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            var file = new MpegFile(stream);
            if (file.SampleRate <= 0 || file.Channels <= 0) throw AudioDecodeException.Unreadable();
            return new Reader(file, stream);
        }
        catch (Exception error) when (error is not AudioDecodeException)
        {
            stream.Dispose();
            throw AudioDecodeException.Unreadable(error);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private sealed class Reader(MpegFile file, Stream stream) : IAudioSampleReader
    {
        private float[] _buffer = [];

        public int SampleRate => file.SampleRate;
        public int Channels => file.Channels;

        public int Read(Span<float> buffer)
        {
            var count = buffer.Length - buffer.Length % Channels;
            if (count <= 0) return 0;
            if (_buffer.Length < count) _buffer = new float[count];
            var read = file.ReadSamples(_buffer, 0, count);
            if (read <= 0) return 0;
            read -= read % Channels;
            _buffer.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public void Dispose()
        {
            file.Dispose();
            stream.Dispose();
        }
    }
}

/// Opus in an Ogg container: voice messages from WhatsApp, Telegram and Signal. Decoded at
/// 48 kHz with the managed Concentus decoder.
public sealed class OggOpusDecoder : IAudioFileDecoder
{
    private const int OpusRate = 48_000;

    public bool CanDecode(string path, ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[..4].SequenceEqual("OggS"u8))
        {
            return AudioFileDecoder.IndexOf(header, "OpusHead") >= 0 || AudioFileDecoder.HasExtension(path, ".opus");
        }
        return false;
    }

    public IAudioSampleReader Open(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            var channels = ChannelCount(stream);
            stream.Position = 0;
            var decoder = OpusCodecFactory.CreateDecoder(OpusRate, channels);
            var ogg = new OpusOggReadStream(decoder, stream);
            return new Reader(ogg, stream, channels);
        }
        catch (Exception error) when (error is not AudioDecodeException)
        {
            stream.Dispose();
            throw AudioDecodeException.Unreadable(error);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// The channel count from the OpusHead packet. More than two channels (surround mappings)
    /// would need a multistream decoder, which voice messages never use.
    internal static int ChannelCount(Stream stream)
    {
        var header = new byte[Math.Min(4096, (int)Math.Min(int.MaxValue, stream.Length))];
        stream.ReadExactly(header);
        var index = AudioFileDecoder.IndexOf(header, "OpusHead");
        if (index < 0 || index + 19 > header.Length) throw AudioDecodeException.Unreadable();
        int channels = header[index + 9];
        var mapping = header[index + 18];
        if (channels is < 1 or > 2 || mapping > 1) throw AudioDecodeException.Unreadable();
        return channels;
    }

    private sealed class Reader(OpusOggReadStream ogg, Stream stream, int channels) : IAudioSampleReader
    {
        private short[] _pending = [];
        private int _pendingOffset;

        public int SampleRate => OpusRate;
        public int Channels => channels;

        public int Read(Span<float> buffer)
        {
            var written = 0;
            var capacity = buffer.Length - buffer.Length % channels;
            while (written < capacity)
            {
                if (_pendingOffset >= _pending.Length)
                {
                    if (!ogg.HasNextPacket) break;
                    var packet = ogg.DecodeNextPacket();
                    _pending = packet ?? [];
                    _pendingOffset = 0;
                    if (_pending.Length == 0) continue;
                }
                var count = Math.Min(capacity - written, _pending.Length - _pendingOffset);
                for (var i = 0; i < count; i++) buffer[written + i] = _pending[_pendingOffset + i] / 32768f;
                written += count;
                _pendingOffset += count;
            }
            return written - written % channels;
        }

        public void Dispose() => stream.Dispose();
    }
}
