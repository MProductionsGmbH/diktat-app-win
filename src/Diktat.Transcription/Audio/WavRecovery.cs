using System.Buffers.Binary;
using System.Text;

namespace Diktat.Transcription.Audio;

/// Makes a WAV readable whose header still says zero bytes. That is what a recorder leaves
/// when the app ends without stopping it (crash, forced close, power loss): the samples are
/// on disk, only the RIFF and data sizes are missing. Port of the macOS WAVRecovery.
public static class WavRecovery
{
    /// Patches the size fields in place. Returns true when something was fixed.
    public static bool RepairIfNeeded(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var length = stream.Length;
            if (length <= 12) return false;
            var header = new byte[(int)Math.Min(length, 65_536)];
            stream.ReadExactly(header);
            var patches = SizePatches(header, length);
            if (patches is null) return false;
            Span<byte> value = stackalloc byte[4];
            foreach (var (offset, size) in patches)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, size);
                stream.Position = offset;
                stream.Write(value);
            }
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// The size fields to rewrite (byte offset and value), null when the file is no WAV or its
    /// sizes already match the file length.
    public static IReadOnlyList<(int Offset, uint Value)>? SizePatches(ReadOnlySpan<byte> header, long fileLength)
    {
        if (header.Length < 12 || Ascii(header, 0) != "RIFF" || Ascii(header, 8) != "WAVE") return null;

        var offset = 12;
        while (offset + 8 <= header.Length)
        {
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[(offset + 4)..]);
            if (Ascii(header, offset) == "data")
            {
                var dataBytes = fileLength - (offset + 8);
                var riffBytes = fileLength - 8;
                if (dataBytes < 0) return null;
                // Sizes above 4 GB cannot be expressed; the reader then reads to the end anyway.
                var data = (uint)Math.Min(uint.MaxValue, dataBytes);
                var riff = (uint)Math.Min(uint.MaxValue, riffBytes);
                if (size == data && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == riff) return null;
                return [(4, riff), (offset + 4, data)];
            }
            // Chunks before the data (format, padding) carry correct sizes; they are word-aligned.
            var next = offset + 8 + size + size % 2;
            if (next > int.MaxValue) return null;
            offset = (int)next;
        }
        return null;
    }

    private static string Ascii(ReadOnlySpan<byte> bytes, int offset) => Encoding.ASCII.GetString(bytes.Slice(offset, 4));
}
