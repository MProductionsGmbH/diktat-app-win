using Diktat.Core.Settings;

namespace Diktat.Core.Tests;

public sealed class MeetingSourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "diktat-source-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void WithoutChoiceTheOldSwitchDecides()
    {
        Assert.Equal(MeetingSource.Online, MeetingSourceInfo.Resolve(null, legacySystemAudio: true, canRecordSystemAudio: true));
        Assert.Equal(MeetingSource.Room, MeetingSourceInfo.Resolve(null, legacySystemAudio: false, canRecordSystemAudio: true));
    }

    [Fact]
    public void ChoiceBeatsTheOldSwitch()
    {
        Assert.Equal(MeetingSource.SystemOnly, MeetingSourceInfo.Resolve(MeetingSource.SystemOnly, legacySystemAudio: false, canRecordSystemAudio: true));
        Assert.Equal(MeetingSource.Room, MeetingSourceInfo.Resolve(MeetingSource.Room, legacySystemAudio: true, canRecordSystemAudio: true));
    }

    [Fact]
    public void WithoutSystemAudioOnlyTheRoomIsLeft()
    {
        Assert.Equal(MeetingSource.Room, MeetingSourceInfo.Resolve(MeetingSource.Online, legacySystemAudio: true, canRecordSystemAudio: false));
    }

    [Fact]
    public void WhatEachSourceRecords()
    {
        Assert.True(MeetingSource.Room.UsesMicrophone());
        Assert.False(MeetingSource.Room.UsesSystemAudio());
        Assert.True(MeetingSource.Online.UsesMicrophone());
        Assert.True(MeetingSource.Online.UsesSystemAudio());
        Assert.False(MeetingSource.SystemOnly.UsesMicrophone());
        Assert.True(MeetingSource.SystemOnly.UsesSystemAudio());
    }

    [Fact]
    public void TheOnlineCardCoversBothOnlineSources()
    {
        Assert.Equal([MeetingSource.Room, MeetingSource.Online], MeetingSourceInfo.Cards);
        Assert.Equal(MeetingSource.Online, MeetingSource.SystemOnly.Card());
        Assert.Equal(MeetingSource.Online, MeetingSource.Online.Card());
        Assert.Equal(MeetingSource.Room, MeetingSource.Room.Card());
        Assert.Equal(MeetingSource.Online, MeetingSourceInfo.Online(withMicrophone: true));
        Assert.Equal(MeetingSource.SystemOnly, MeetingSourceInfo.Online(withMicrophone: false));
    }

    /// The values are those of the Mac app, and the choice survives a restart.
    [Fact]
    public void TheChoiceIsStoredWithTheMacNames()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = new SettingsContainer();
        Assert.Null(settings.App.MeetingSource);
        Assert.True(settings.App.SuggestsMeetingOnCall);
        settings.App.MeetingSource = MeetingSource.SystemOnly;
        settings.App.SuggestsMeetingOnCall = false;
        store.Save(settings);

        var json = File.ReadAllText(store.Path);
        Assert.Contains("\"meetingSource\": \"systemOnly\"", json);
        Assert.Contains("\"suggestsMeetingOnCall\": false", json);
        var loaded = store.Load();
        Assert.Equal(MeetingSource.SystemOnly, loaded.App.MeetingSource);
        Assert.False(loaded.App.SuggestsMeetingOnCall);
    }

    /// An unknown value is no choice: the old switch (on Windows: the room) decides.
    [Fact]
    public void UnknownChoiceFallsBackToTheOldSwitch()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{"app":{"meetingSource":"nonsense","summarizesMeetings":false}}""");
        var loaded = new SettingsStore(path).Load();
        Assert.Null(loaded.App.MeetingSource);
        Assert.False(loaded.App.SummarizesMeetings);
        Assert.Equal(MeetingSource.Online, MeetingSourceInfo.Resolve(loaded.App.MeetingSource, legacySystemAudio: true, canRecordSystemAudio: true));
        Assert.Equal(MeetingSource.Room, MeetingSourceInfo.Resolve(loaded.App.MeetingSource, legacySystemAudio: false, canRecordSystemAudio: true));
    }
}
