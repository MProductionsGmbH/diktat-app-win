using Diktat.Core.History;
using Diktat.Core.Settings;
using Diktat.Core.Support;

namespace Diktat.Core.Tests;

/// "Alle Daten löschen", on folders made up in the temp directory, never the real ones.
public sealed class UserDataCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diktat-cleanup-" + Guid.NewGuid().ToString("N"));

    private string Roaming => Path.Combine(_root, "Roaming");
    private string Local => Path.Combine(_root, "Local");
    private string Temp => Path.Combine(_root, "Temp");

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void TheFoldersAreDiktatsFolderInEachBase()
    {
        Assert.Equal(
            [Path.Combine(Roaming, "Diktat"), Path.Combine(Local, "Diktat"), Path.Combine(Temp, "Diktat")],
            UserDataCleanup.Folders("Diktat", Roaming, Local, Temp));
    }

    /// A missing environment variable must never point the removal at the working directory.
    [Fact]
    public void AnEmptyOrRelativeBaseIsSkipped()
    {
        Assert.Empty(UserDataCleanup.Folders("Diktat", "", null, "   ", "relative"));
        Assert.Equal([Path.Combine(Local, "Diktat")], UserDataCleanup.Folders("Diktat", Local, Local + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void RemovesEverythingDiktatStoredAndNothingElse()
    {
        var diktat = new[]
        {
            Write(Path.Combine(Roaming, "Diktat", "settings.json")),
            Write(Path.Combine(Roaming, "Diktat", "ai-provider.json")),
            Write(Path.Combine(Roaming, "Diktat", "History", "history.dat")),
            Write(Path.Combine(Roaming, "Diktat", "Meetings", ".meetings")),
            Write(Path.Combine(Roaming, "Diktat", "secrets", "ai-provider.2fopenAI.secret")),
            Write(Path.Combine(Local, "Diktat", "Models", "parakeet", "encoder.onnx")),
            Write(Path.Combine(Local, "Diktat", "Logs", "diktat-2026-11-23.log")),
            Write(Path.Combine(Local, "Diktat", "muted-output.txt")),
            Write(Path.Combine(Temp, "Diktat", "meetings", Guid.NewGuid() + "-mic.wav")),
        };
        File.SetAttributes(diktat[0], FileAttributes.ReadOnly);
        var others = new[]
        {
            Write(Path.Combine(Roaming, "Other App", "settings.json")),
            Write(Path.Combine(Roaming, "Diktat Backup", "settings.json")),
            Write(Path.Combine(Temp, "unrelated.wav")),
        };

        var report = UserDataCleanup.Remove(UserDataCleanup.Folders("Diktat", Roaming, Local, Temp));

        Assert.True(report.Succeeded);
        Assert.Equal(3, report.Removed.Count);
        Assert.All(diktat, path => Assert.False(File.Exists(path), path));
        Assert.False(Directory.Exists(Path.Combine(Roaming, "Diktat")));
        Assert.False(Directory.Exists(Path.Combine(Local, "Diktat")));
        Assert.False(Directory.Exists(Path.Combine(Temp, "Diktat")));
        Assert.All(others, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void WhatIsAlreadyGoneCountsAsRemoved()
    {
        var report = UserDataCleanup.Remove([Path.Combine(_root, "never-there")]);
        Assert.True(report.Succeeded);
    }

    [Fact]
    public void WhatCannotBeRemovedIsReportedAndTheRestStillGoes()
    {
        // Root may write anywhere; a folder without write permission blocks nothing then.
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root") return;
        var stuck = Write(Path.Combine(Roaming, "Diktat", "History", "history.dat"));
        var other = Write(Path.Combine(Local, "Diktat", "Logs", "diktat.log"));
        FileStream? held = null;
        if (OperatingSystem.IsWindows())
        {
            held = new FileStream(stuck, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        else
        {
            File.SetUnixFileMode(Path.GetDirectoryName(stuck)!, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        try
        {
            var report = UserDataCleanup.Remove(UserDataCleanup.Folders("Diktat", Roaming, Local), attempts: 2, pause: TimeSpan.FromMilliseconds(1));
            var failure = Assert.Single(report.Failed);
            Assert.Equal(Path.Combine(Roaming, "Diktat"), failure.Path);
            Assert.False(report.Succeeded);
            Assert.False(File.Exists(other));
        }
        finally
        {
            held?.Dispose();
        }
    }
}

/// After "Alle Daten löschen" the stores keep what is in memory but never write it back.
public sealed class StopWritingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "diktat-stop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void SettingsAreNotWrittenBack()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new SettingsStore(path);
        var settings = new SettingsContainer();
        store.Save(settings);
        Assert.True(File.Exists(path));

        store.StopWriting();
        File.Delete(path);
        settings.App.AutomaticParagraphs = !settings.App.AutomaticParagraphs;
        store.Save(settings);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void HistoryIsNotWrittenBack()
    {
        var store = new TranscriptHistoryStore(_directory, new PassThroughDataProtector(), HistoryRetention.Day);
        store.StopWriting();
        store.Add("Noch ein Text", WorkflowType.TapTalk);
        store.Prune();
        Assert.Single(store.Entries);
        Assert.False(File.Exists(store.Path));
        Assert.False(Directory.Exists(_directory));
    }
}
