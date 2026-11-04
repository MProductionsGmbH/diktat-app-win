using System.Runtime.InteropServices;
using System.Text;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Platform;

/// Every format on the clipboard, copied byte for byte (HGLOBAL formats and enhanced
/// metafiles), so it survives the clipboard changing and can be written back. Covers text,
/// Unicode text, RTF, HTML, images (DIB/DIBV5/PNG), file drops (CF_HDROP + shell formats) and
/// app-specific formats such as Office's. Formats that are GDI or private handles (CF_BITMAP,
/// CF_PALETTE, CF_METAFILEPICT, owner-display) are skipped; Windows synthesizes CF_BITMAP from
/// CF_DIB again.
internal sealed class ClipboardSnapshot
{
    /// Above this the snapshot gives up (a huge image from an editor): the transcript then stays
    /// on the clipboard instead of a partial restore.
    public const long MaximumBytes = 128L * 1024 * 1024;

    public sealed record Entry(uint Format, string? Name, byte[] Data);

    public IReadOnlyList<Entry> Entries { get; }
    public bool IsEmpty => Entries.Count == 0;
    public long TotalBytes { get; }

    private ClipboardSnapshot(List<Entry> entries)
    {
        Entries = entries;
        TotalBytes = entries.Sum(e => (long)e.Data.Length);
    }

    public static readonly ClipboardSnapshot Empty = new([]);

    /// Reads the open clipboard. The caller has opened it. Null when the clipboard lists
    /// content that could not be read (restoring an empty snapshot would wipe it).
    public static ClipboardSnapshot? ReadOpenClipboard()
    {
        var entries = new List<Entry>();
        long total = 0;
        bool listedReadable = false;
        uint format = 0;
        while ((format = Win32.EnumClipboardFormats(format)) != 0)
        {
            if (IsSkipped(format)) continue;
            listedReadable = true;
            try
            {
                var data = format == Win32.CF_ENHMETAFILE ? ReadEnhMetaFile() : ReadGlobal(format);
                if (data is null) continue;
                total += data.Length;
                if (total > MaximumBytes)
                {
                    Log.Warn($"Clipboard snapshot over {MaximumBytes / (1024 * 1024)} MB; not restoring");
                    return null;
                }
                entries.Add(new Entry(format, FormatName(format), data));
            }
            catch (Exception ex)
            {
                Log.Warn($"Clipboard format {format} unreadable", ex);
            }
        }
        if (listedReadable && entries.Count == 0) return null;
        return new ClipboardSnapshot(entries);
    }

    /// Writes the snapshot to the open clipboard, which the caller has emptied.
    public void WriteToOpenClipboard()
    {
        foreach (var entry in Entries)
        {
            // Registered formats get new IDs only across sessions; re-register by name to be safe.
            uint format = entry.Name is not null ? Win32.RegisterClipboardFormat(entry.Name) : entry.Format;
            if (format == 0) continue;
            if (format == Win32.CF_ENHMETAFILE)
            {
                var handle = Win32.SetEnhMetaFileBits((uint)entry.Data.Length, entry.Data);
                if (handle == IntPtr.Zero) continue;
                if (Win32.SetClipboardData(format, handle) == IntPtr.Zero) Win32.DeleteEnhMetaFile(handle);
                continue;
            }
            SetGlobal(format, entry.Data);
        }
    }

    /// Puts bytes on the open clipboard as HGLOBAL. The clipboard owns the memory on success.
    public static bool SetGlobal(uint format, ReadOnlySpan<byte> data)
    {
        var size = (nuint)Math.Max(1, data.Length);
        var handle = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, size);
        if (handle == IntPtr.Zero) return false;
        var pointer = Win32.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            Win32.GlobalFree(handle);
            return false;
        }
        try
        {
            if (data.Length > 0) Marshal.Copy(data.ToArray(), 0, pointer, data.Length);
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }
        if (Win32.SetClipboardData(format, handle) == IntPtr.Zero)
        {
            Win32.GlobalFree(handle);
            return false;
        }
        return true;
    }

    public static bool SetUnicodeText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        return SetGlobal(Win32.CF_UNICODETEXT, bytes);
    }

    public static bool SetDword(string formatName, uint value)
    {
        uint format = Win32.RegisterClipboardFormat(formatName);
        return format != 0 && SetGlobal(format, BitConverter.GetBytes(value));
    }

    private static bool IsSkipped(uint format) =>
        format is Win32.CF_BITMAP or Win32.CF_METAFILEPICT or Win32.CF_PALETTE or Win32.CF_OWNERDISPLAY
            or Win32.CF_DSPBITMAP or Win32.CF_DSPMETAFILEPICT or Win32.CF_DSPENHMETAFILE
        || format is >= Win32.CF_PRIVATEFIRST and <= Win32.CF_PRIVATELAST
        || format is >= Win32.CF_GDIOBJFIRST and <= Win32.CF_GDIOBJLAST;

    private static byte[]? ReadGlobal(uint format)
    {
        var handle = Win32.GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        long size = (long)(nuint)Win32.GlobalSize(handle);
        if (size <= 0 || size > MaximumBytes) return null;
        var pointer = Win32.GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(pointer, data, 0, (int)size);
            return data;
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }
    }

    private static byte[]? ReadEnhMetaFile()
    {
        var handle = Win32.GetClipboardData(Win32.CF_ENHMETAFILE);
        if (handle == IntPtr.Zero) return null;
        uint size = Win32.GetEnhMetaFileBits(handle, 0, null);
        if (size == 0 || size > MaximumBytes) return null;
        var data = new byte[size];
        return Win32.GetEnhMetaFileBits(handle, size, data) == size ? data : null;
    }

    /// The registered name of a format, null for the predefined ones (below 0xC000).
    private static string? FormatName(uint format)
    {
        if (format < 0xC000) return null;
        var buffer = new char[256];
        int length = Win32.GetClipboardFormatName(format, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }
}
