using System.IO;
using System.Diagnostics;
using System.Text;

namespace Diktat.Windows.Platform;

/// A small file log in %LOCALAPPDATA%\Diktat\Logs, one file per day, kept for a week.
/// Thread-safe. Never log transcripts, clipboard contents, field text or secrets.
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static DateOnly _day;
    private static bool _failed;
    private static bool _cleaned;

    public static string Directory { get; set; } = AppPaths.LogsDirectory;

    /// Also write every line to the debugger output.
    public static bool EchoToDebug { get; set; } = Debugger.IsAttached;

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? exception = null) => Write("WARN ", message, exception);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("HH:mm:ss.fff")).Append(' ')
            .Append(level).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
            .Append(message);
        if (exception is not null) line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        var text = line.ToString();
        if (EchoToDebug) Debug.WriteLine(text);

        lock (Gate)
        {
            if (_failed) return;
            try
            {
                var day = DateOnly.FromDateTime(now);
                if (_writer is null || day != _day)
                {
                    _writer?.Dispose();
                    System.IO.Directory.CreateDirectory(Directory);
                    var path = Path.Combine(Directory, $"diktat-{day:yyyy-MM-dd}.log");
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _day = day;
                    if (!_cleaned)
                    {
                        _cleaned = true;
                        DeleteOldFiles();
                    }
                }
                _writer.WriteLine(text);
                if (exception?.StackTrace is { } stack && level == "ERROR") _writer.WriteLine(stack);
            }
            catch
            {
                // A log that cannot be written must not take the app down; stop trying.
                _failed = true;
            }
        }
    }

    private static void DeleteOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "diktat-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    public static void Flush()
    {
        lock (Gate) _writer?.Flush();
    }
}
