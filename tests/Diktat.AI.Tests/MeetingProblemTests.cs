using Diktat.AI.Meetings;

namespace Diktat.AI.Tests;

public sealed class MeetingProblemTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_000);

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    /// A short gap between two speakers is no problem; only lasting digital silence is.
    [Fact]
    public void SilenceCountsOnlyAfterAWhile()
    {
        var tracker = new MicrophoneSilenceTracker();
        Assert.False(tracker.IsSilentTooLong(-160, At(0)));
        Assert.False(tracker.IsSilentTooLong(-160, At(4.9)));
        Assert.True(tracker.IsSilentTooLong(-160, At(5)));
        Assert.True(tracker.IsSilentTooLong(-120, At(30)));
    }

    /// A quiet room is far above digital silence and never a problem.
    [Fact]
    public void AQuietRoomIsNoSilence()
    {
        var tracker = new MicrophoneSilenceTracker();
        for (int second = 0; second < 60; second++)
        {
            Assert.False(tracker.IsSilentTooLong(-70, At(second)));
        }
    }

    [Fact]
    public void SoundEndsTheSilence()
    {
        var tracker = new MicrophoneSilenceTracker();
        tracker.IsSilentTooLong(-160, At(0));
        Assert.True(tracker.IsSilentTooLong(-160, At(6)));
        Assert.False(tracker.IsSilentTooLong(-40, At(7)));
        // The clock starts again with the next silence.
        Assert.False(tracker.IsSilentTooLong(-160, At(8)));
        Assert.False(tracker.IsSilentTooLong(-160, At(12)));
        Assert.True(tracker.IsSilentTooLong(-160, At(13)));
    }

    [Fact]
    public void AResetStartsTheClockAgain()
    {
        var tracker = new MicrophoneSilenceTracker();
        tracker.IsSilentTooLong(-160, At(0));
        tracker.Reset();
        Assert.False(tracker.IsSilentTooLong(-160, At(6)));
    }

    /// With several problems at once the page shows the most urgent.
    [Fact]
    public void AMissingMicrophoneOutranksTheRest()
    {
        Assert.Equal(MeetingProblem.MicrophoneLost, new[] { MeetingProblem.SystemAudioLost, MeetingProblem.MicrophoneSilent, MeetingProblem.MicrophoneLost }.Min());
        Assert.Equal(MeetingProblem.MicrophoneSilent, new[] { MeetingProblem.SystemAudioLost, MeetingProblem.MicrophoneSilent }.Min());
    }

    [Fact]
    public void TheSetShowsTheMostUrgentAndSaysWhenThatChanged()
    {
        var problems = new MeetingProblems();
        Assert.Null(problems.Current);
        Assert.True(problems.Set(MeetingProblem.SystemAudioLost, true));
        Assert.Equal(MeetingProblem.SystemAudioLost, problems.Current);
        Assert.False(problems.Set(MeetingProblem.SystemAudioLost, true));
        Assert.True(problems.Set(MeetingProblem.MicrophoneLost, true));
        Assert.Equal(MeetingProblem.MicrophoneLost, problems.Current);
        // A less urgent one going away changes nothing on screen.
        Assert.False(problems.Set(MeetingProblem.SystemAudioLost, false));
        Assert.True(problems.Set(MeetingProblem.MicrophoneLost, false));
        Assert.Null(problems.Current);
    }

    [Fact]
    public void EveryProblemHasItsTexts()
    {
        foreach (var problem in Enum.GetValues<MeetingProblem>())
        {
            Assert.NotEmpty(problem.Title());
            Assert.NotEmpty(problem.Detail());
            Assert.NotEmpty(problem.ShortLabel());
        }
        Assert.Equal("Mikrofon prüfen", MeetingProblem.MicrophoneSilent.ShortLabel());
        Assert.Equal("Ton prüfen", MeetingProblem.SystemAudioLost.ShortLabel());
    }
}

public sealed class CallDetectionTests
{
    private static CallDetection.Candidate Candidate(string app, bool input = true, bool output = false) => new(app, input, output);

    [Fact]
    public void CallAppWithOpenMicrophoneIsACall()
    {
        Assert.Equal(["Zoom"], CallDetection.Calls([Candidate("Zoom.exe")]));
        Assert.Equal(["Teams"], CallDetection.Calls([Candidate("MSTeams_8wekyb3d8bbwe")]));
        Assert.Equal(["Teams"], CallDetection.Calls([Candidate("ms-teams.exe")]));
    }

    [Fact]
    public void CallAppWithoutMicrophoneIsNot()
    {
        Assert.Empty(CallDetection.Calls([Candidate("zoom.exe", input: false, output: true)]));
    }

    [Fact]
    public void BrowserNeedsSoundInAndOut()
    {
        Assert.Empty(CallDetection.Calls([Candidate("chrome.exe", input: true, output: false)]));
        Assert.Equal(["Browser"], CallDetection.Calls([Candidate("chrome.exe", input: true, output: true)]));
        Assert.Equal(["Browser"], CallDetection.Calls([Candidate("msedge.exe", input: true, output: true)]));
    }

    [Fact]
    public void OtherAppsAreNoCalls()
    {
        Assert.Empty(CallDetection.Calls([Candidate("audacity.exe", input: true, output: true)]));
        Assert.Empty(CallDetection.Calls([Candidate("diktat.exe", input: true, output: true)]));
    }

    [Fact]
    public void NoRepeatsForSeveralProcessesOfOneApp()
    {
        var processes = new[] { Candidate("zoom.exe"), Candidate("Zoom.exe"), Candidate("ms-teams.exe"), Candidate("MSTeams_8wekyb3d8bbwe") };
        Assert.Equal(["Zoom", "Teams"], CallDetection.Calls(processes));
    }
}

public sealed class CallStreaksTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_000);

    /// A call is reported on its second check, once.
    [Fact]
    public void ACallIsReportedAfterTwoChecks()
    {
        var streaks = new CallStreaks();
        Assert.Empty(streaks.Evaluate(["Zoom"], Start));
        Assert.Equal(["Zoom"], streaks.Evaluate(["Zoom"], Start.AddSeconds(3)));
        Assert.Empty(streaks.Evaluate(["Zoom"], Start.AddSeconds(6)));
        Assert.Empty(streaks.Evaluate(["Zoom"], Start.AddSeconds(9)));
    }

    /// A microphone open for a single check (a short blip) is no call.
    [Fact]
    public void ABlipIsNoCall()
    {
        var streaks = new CallStreaks();
        Assert.Empty(streaks.Evaluate(["Teams"], Start));
        Assert.Empty(streaks.Evaluate([], Start.AddSeconds(3)));
        Assert.Empty(streaks.Evaluate(["Teams"], Start.AddSeconds(6)));
        Assert.Equal(["Teams"], streaks.Evaluate(["Teams"], Start.AddSeconds(9)));
    }

    /// The same app again within 15 minutes stays quiet; after that it is suggested again.
    [Fact]
    public void TheSameAppStaysQuietForAWhile()
    {
        var streaks = new CallStreaks();
        streaks.Evaluate(["Zoom"], Start);
        Assert.Equal(["Zoom"], streaks.Evaluate(["Zoom"], Start.AddSeconds(3)));
        streaks.Evaluate([], Start.AddMinutes(5));
        streaks.Evaluate(["Zoom"], Start.AddMinutes(10));
        Assert.Empty(streaks.Evaluate(["Zoom"], Start.AddMinutes(10).AddSeconds(3)));
        streaks.Evaluate([], Start.AddMinutes(16));
        streaks.Evaluate(["Zoom"], Start.AddMinutes(17));
        Assert.Equal(["Zoom"], streaks.Evaluate(["Zoom"], Start.AddMinutes(17).AddSeconds(3)));
    }

    /// Another app is not held back by the quiet time of the first.
    [Fact]
    public void AppsCountOnTheirOwn()
    {
        var streaks = new CallStreaks();
        streaks.Evaluate(["Zoom"], Start);
        Assert.Equal(["Zoom"], streaks.Evaluate(["Zoom", "Teams"], Start.AddSeconds(3)));
        Assert.Equal(["Teams"], streaks.Evaluate(["Zoom", "Teams"], Start.AddSeconds(6)));
    }

    [Fact]
    public void AResetForgetsTheRunningCount()
    {
        var streaks = new CallStreaks();
        streaks.Evaluate(["Zoom"], Start);
        streaks.Reset();
        Assert.Empty(streaks.Evaluate(["Zoom"], Start.AddSeconds(3)));
        Assert.Equal(["Zoom"], streaks.Evaluate(["Zoom"], Start.AddSeconds(6)));
    }

    [Fact]
    public void ChecksEveryThreeSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), CallStreaks.Interval);
        Assert.Equal(2, CallStreaks.ChecksBeforeReport);
        Assert.Equal(TimeSpan.FromMinutes(15), CallStreaks.QuietTime);
    }
}
