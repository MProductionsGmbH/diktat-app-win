using System.Buffers.Binary;
using System.Text;

namespace Diktat.Transcription.Audio;

/// Decoded audio: interleaved float samples.
public sealed record AudioData(float[] Samples, int SampleRate, int Channels)
{
    public int FrameCount => Channels > 0 ? Samples.Length / Channels : 0;

    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds((double)FrameCount / SampleRate) : TimeSpan.Zero;

    public float[] ToMono16k() => AudioResampler.ToMono16k(Samples, SampleRate, Channels);
}

public enum WavSampleFormat
{
    Pcm16,
    Float32,
}

/// The audio file could not be read, with a German message for the UI.
public sealed class AudioDecodeException(string message, Exception? inner = null) : Exception(message, inner)
{
    public static AudioDecodeException Unreadable(Exception? inner = null) => new(L.T("Diese Audiodatei kann Diktat nicht öffnen."), inner);

    public static AudioDecodeException Empty() => new(L.T("Die Audiodatei ist leer."));
}

/// RIFF/WAVE reading and writing: PCM 8/16/24/32 bit, IEEE float 32/64 bit, WAVE_FORMAT_EXTENSIBLE.
/// A data chunk whose size says 0 or more than the file holds (a recording cut off by a crash)
/// is read up to the end of the file.
public static class WavFile
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public static AudioData Read(string path)
    {
        using var reader = WavSampleReader.Open(File.OpenRead(path));
        return AudioFileDecoder.ReadAll(reader, CancellationToken.None);
    }

    public static AudioData Read(Stream stream)
    {
        using var reader = WavSampleReader.Open(stream, leaveOpen: true);
        return AudioFileDecoder.ReadAll(reader, CancellationToken.None);
    }

    /// Reads a file and converts it to 16 kHz mono.
    public static float[] ReadMono16k(string path) => Read(path).ToMono16k();

    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate, int channels = 1, WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        using var writer = new WavWriter(path, sampleRate, channels, format);
        writer.Write(samples);
    }

    public static void Write(Stream stream, ReadOnlySpan<float> samples, int sampleRate, int channels = 1, WavSampleFormat format = WavSampleFormat.Pcm16)
    {
        using var writer = new WavWriter(stream, sampleRate, channels, format, leaveOpen: true);
        writer.Write(samples);
    }

    internal readonly record struct Format(ushort Tag, int Channels, int SampleRate, int BitsPerSample, int BlockAlign);

    /// Parses the header up to the start of the samples. Returns the format, the data offset and
    /// the data length (clamped to what the stream holds when it is seekable).
    internal static (Format Format, long DataOffset, long DataLength) ParseHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        if (!TryReadExactly(stream, header) || Ascii(header[..4]) != "RIFF" || Ascii(header[8..12]) != "WAVE")
        {
            throw AudioDecodeException.Unreadable();
        }

        Format? format = null;
        long position = 12;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (TryReadExactly(stream, chunkHeader))
        {
            position += 8;
            var id = Ascii(chunkHeader[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            if (id == "fmt ")
            {
                if (size < 16) throw AudioDecodeException.Unreadable();
                var fmt = new byte[size];
                if (!TryReadExactly(stream, fmt)) throw AudioDecodeException.Unreadable();
                position += size;
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                var rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (tag == FormatExtensible && size >= 26)
                {
                    // The sub-format GUID starts with the actual format tag.
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                }
                format = new Format(tag, channels, rate, bits, blockAlign);
                if (size % 2 == 1) { Skip(stream, 1); position++; }
            }
            else if (id == "data")
            {
                if (format is not { } f || f.Channels <= 0 || f.SampleRate <= 0 || f.BlockAlign <= 0)
                {
                    throw AudioDecodeException.Unreadable();
                }
                if (!(f.Tag == FormatPcm && f.BitsPerSample is 8 or 16 or 24 or 32) && !(f.Tag == FormatFloat && f.BitsPerSample is 32 or 64))
                {
                    throw AudioDecodeException.Unreadable();
                }
                var length = size;
                if (stream.CanSeek)
                {
                    var remaining = stream.Length - position;
                    // 0 or too large: the header of an interrupted recording.
                    if (length == 0 || length > remaining) length = remaining;
                }
                else if (length == 0 || length == uint.MaxValue)
                {
                    length = long.MaxValue;
                }
                length -= length % f.BlockAlign;
                return (f, position, length);
            }
            else
            {
                var skip = size + size % 2;
                Skip(stream, skip);
                position += skip;
            }
        }
        throw AudioDecodeException.Unreadable();
    }

    internal static float ReadSample(ReadOnlySpan<byte> bytes, Format format) => (format.Tag, format.BitsPerSample) switch
    {
        (FormatPcm, 8) => (bytes[0] - 128) / 128f,
        (FormatPcm, 16) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        (FormatPcm, 24) => ((bytes[0] | bytes[1] << 8 | (sbyte)bytes[2] << 16)) / 8388608f,
        (FormatPcm, 32) => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        (FormatFloat, 32) => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        (FormatFloat, 64) => (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes),
        _ => throw AudioDecodeException.Unreadable(),
    };

    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }
        var buffer = new byte[4096];
        while (count > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) return;
            count -= read;
        }
    }
}

/// Reads the samples of a WAV stream block by block.
public sealed class WavSampleReader : IAudioSampleReader
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly WavFile.Format _format;
    private readonly int _bytesPerSample;
    private long _remaining;
    private byte[] _bytes = [];

    private WavSampleReader(Stream stream, bool leaveOpen, WavFile.Format format, long dataLength)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _format = format;
        _bytesPerSample = format.BitsPerSample / 8;
        _remaining = dataLength;
    }

    public static WavSampleReader Open(Stream stream, bool leaveOpen = false)
    {
        try
        {
            var (format, _, length) = WavFile.ParseHeader(stream);
            return new WavSampleReader(stream, leaveOpen, format, length);
        }
        catch
        {
            if (!leaveOpen) stream.Dispose();
            throw;
        }
    }

    public int SampleRate => _format.SampleRate;
    public int Channels => _format.Channels;

    public int Read(Span<float> buffer)
    {
        var frameBytes = _bytesPerSample * Channels;
        var frames = buffer.Length / Channels;
        var wanted = (int)Math.Min((long)frames * frameBytes, _remaining);
        wanted -= wanted % frameBytes;
        if (wanted <= 0) return 0;
        if (_bytes.Length < wanted) _bytes = new byte[wanted];
        var read = 0;
        while (read < wanted)
        {
            var n = _stream.Read(_bytes, read, wanted - read);
            if (n == 0) break;
            read += n;
        }
        read -= read % frameBytes;
        _remaining = read < wanted ? 0 : _remaining - read;
        var samples = read / _bytesPerSample;
        for (var i = 0; i < samples; i++)
        {
            buffer[i] = WavFile.ReadSample(_bytes.AsSpan(i * _bytesPerSample, _bytesPerSample), _format);
        }
        return samples;
    }

    public void Dispose()
    {
        if (!_leaveOpen) _stream.Dispose();
    }
}

/// Writes a WAV file progressively, e.g. while recording a meeting. The sizes in the header
/// are updated on <see cref="Flush"/> and <see cref="Dispose"/>; when the app ends before that,
/// <see cref="WavRecovery.RepairIfNeeded"/> fixes the header from the file length.
public sealed class WavWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _dataSizeOffset;
    private readonly long _dataStart;
    private byte[] _bytes = [];
    private bool _disposed;

    public WavWriter(string path, int sampleRate, int channels = 1, WavSampleFormat format = WavSampleFormat.Pcm16)
        : this(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read), sampleRate, channels, format, leaveOpen: false)
    {
    }

    public WavWriter(Stream stream, int sampleRate, int channels = 1, WavSampleFormat format = WavSampleFormat.Pcm16, bool leaveOpen = false)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _stream = stream;
        _leaveOpen = leaveOpen;
        SampleRate = sampleRate;
        Channels = channels;
        SampleFormat = format;
        var bits = format == WavSampleFormat.Pcm16 ? 16 : 32;
        var blockAlign = channels * bits / 8;

        var start = stream.CanSeek ? stream.Position : 0;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 36);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], (ushort)(format == WavSampleFormat.Pcm16 ? 1 : 3));
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)bits);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0);
        stream.Write(header);
        _riffStart = start;
        _dataSizeOffset = start + 40;
        _dataStart = start + 44;
    }

    private readonly long _riffStart;

    public int SampleRate { get; }
    public int Channels { get; }
    public WavSampleFormat SampleFormat { get; }
    public long DataBytes { get; private set; }

    /// Writes interleaved samples (clipped to -1 ... 1 for 16-bit PCM).
    public void Write(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytesPerSample = SampleFormat == WavSampleFormat.Pcm16 ? 2 : 4;
        var length = samples.Length * bytesPerSample;
        if (_bytes.Length < length) _bytes = new byte[length];
        var span = _bytes.AsSpan(0, length);
        if (SampleFormat == WavSampleFormat.Pcm16)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var value = Math.Clamp(samples[i], -1f, 1f);
                BinaryPrimitives.WriteInt16LittleEndian(span[(i * 2)..], (short)Math.Round(value * (value < 0 ? 32768f : 32767f)));
            }
        }
        else
        {
            for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(span[(i * 4)..], samples[i]);
        }
        _stream.Write(span);
        DataBytes += length;
    }

    /// Writes the current sizes into the header and flushes to disk.
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PatchHeader();
        _stream.Flush();
    }

    private void PatchHeader()
    {
        if (!_stream.CanSeek) return;
        var end = _stream.Position;
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)Math.Min(uint.MaxValue, _dataStart - _riffStart - 8 + DataBytes));
        _stream.Position = _riffStart + 4;
        _stream.Write(value);
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)Math.Min(uint.MaxValue, DataBytes));
        _stream.Position = _dataSizeOffset;
        _stream.Write(value);
        _stream.Position = end;
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            PatchHeader();
            _stream.Flush();
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen) _stream.Dispose();
        }
    }
}
