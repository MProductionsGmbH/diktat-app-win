using System.Diagnostics;
using System.IO;
using Diktat.Transcription.Audio;
using Diktat.Windows.Platform;

namespace Diktat.Windows.Runtime;

/// Where a meeting's raw audio lies while it records and until it is evaluated: in the temp
/// folder, one 16 kHz mono WAV per track, written in chunks so a long meeting does not grow in
/// memory. Deleted once the transcript is saved (as on the Mac).
public static class MeetingAudio
{
    public static string Directory => Path.Combine(AppPaths.TempDirectory, "meetings");

    public static string MicrophonePath(Guid id) => Path.Combine(Directory, $"{id}-mic.wav");
    public static string SystemAudioPath(Guid id) => Path.Combine(Directory, $"{id}-system.wav");

    public static bool Exists(Guid id) => File.Exists(MicrophonePath(id));

    public static void Delete(Guid id)
    {
        foreach (var path in new[] { MicrophonePath(id), SystemAudioPath(id) })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn("Deleting meeting audio failed", ex);
            }
        }
    }

    /// Removes audio no pending meeting needs any more (a meeting deleted or finished elsewhere).
    public static void DeleteOrphans(IEnumerable<Guid> pendingMeetings)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var keep = pendingMeetings.ToHashSet();
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.wav"))
            {
                // "<36-character id>-mic" or "<id>-system"
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length >= 36 && Guid.TryParse(name[..36], out var id) && keep.Contains(id)) continue;
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Cleaning up meeting audio failed", ex);
        }
    }

    /// Samples of silence to put in front of a track so that both tracks start at the same moment.
    /// <paramref name="trackStart"/> and <paramref name="otherStart"/> are Stopwatch timestamps of
    /// each track's first sample; 0 means unknown (then nothing is padded).
    public static int LeadingPad(long trackStart, long otherStart, int sampleRate, long frequency = 0)
    {
        if (trackStart == 0 || otherStart == 0 || trackStart <= otherStart) return 0;
        if (frequency <= 0) frequency = Stopwatch.Frequency;
        return (int)Math.Round((double)(trackStart - otherStart) / frequency * sampleRate);
    }

    /// Reads a track, repairing a header a crash left at zero bytes.
    public static float[] Read(string path)
    {
        WavRecovery.RepairIfNeeded(path);
        return WavFile.ReadMono16k(path);
    }

    /// Pads the shorter track at the end, so both have the same length.
    public static (float[] A, float[] B) SameLength(float[] a, float[] b)
    {
        if (a.Length == b.Length) return (a, b);
        int length = Math.Max(a.Length, b.Length);
        if (a.Length < length) Array.Resize(ref a, length);
        if (b.Length < length) Array.Resize(ref b, length);
        return (a, b);
    }
}

/// Appends chunks of a track to its WAV file on a background thread, in order.
internal sealed class TrackWriter
{
    private readonly WavWriter _writer;
    private Task _chain = Task.CompletedTask;
    private readonly object _gate = new();

    public TrackWriter(string path, int sampleRate)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new WavWriter(path, sampleRate);
    }

    public long SamplesWritten { get; private set; }

    public void Append(float[] samples)
    {
        if (samples.Length == 0) return;
        SamplesWritten += samples.Length;
        lock (_gate)
        {
            _chain = _chain.ContinueWith(_ =>
            {
                _writer.Write(samples);
                _writer.Flush();
            }, TaskScheduler.Default);
        }
    }

    public void AppendSilence(int samples)
    {
        if (samples > 0) Append(new float[samples]);
    }

    /// Writes what is queued and closes the file.
    public async Task CloseAsync()
    {
        Task chain;
        lock (_gate) chain = _chain;
        try
        {
            await chain.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Writing meeting audio failed", ex);
        }
        finally
        {
            _writer.Dispose();
        }
    }
}
