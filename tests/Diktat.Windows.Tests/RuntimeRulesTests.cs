using Diktat.Core.Settings;
using Diktat.Transcription;
using Diktat.Windows.Pill;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Tests;

public class PillLayoutTests
{
    [Theory]
    [InlineData(PillPosition.Left, 1.0 / 3.0)]
    [InlineData(PillPosition.Center, 0.5)]
    [InlineData(PillPosition.Right, 2.0 / 3.0)]
    public void PlacementCentersThePillAtItsShareAboveTheTaskbar(PillPosition position, double share)
    {
        var (x, y, width, height) = PillLayout.Placement(0, 0, 1920, 1040, 1.0, position);

        Assert.Equal(380, width);
        Assert.Equal(88, height);
        Assert.Equal(1040 - 88, y);
        Assert.Equal(1920 * share, x + width / 2.0, 0);
    }

    [Fact]
    public void PlacementScalesWithTheMonitorAndStaysInside()
    {
        var (x, y, width, height) = PillLayout.Placement(-1280, 0, -1280 + 500, 1000, 1.5, PillPosition.Right);

        Assert.Equal(570, width);
        Assert.Equal(132, height);
        Assert.Equal(1000 - 132, y);
        Assert.True(x >= -1280);
    }

    [Theory]
    [InlineData(DictationPhase.Recording, false, false, PillState.Recording)]
    [InlineData(DictationPhase.Processing, false, true, PillState.Processing)]
    [InlineData(DictationPhase.Done, false, false, PillState.Done)]
    [InlineData(DictationPhase.Done, true, false, PillState.Copied)]
    [InlineData(DictationPhase.Error, false, false, PillState.Error)]
    [InlineData(DictationPhase.Idle, false, true, PillState.Meeting)]
    [InlineData(DictationPhase.Idle, false, false, PillState.Hidden)]
    public void StateFollowsDictationThenMeeting(DictationPhase phase, bool copied, bool meeting, PillState expected)
    {
        Assert.Equal(expected, PillLayout.StateFor(phase, copied, meeting, enabled: true));
    }

    [Fact]
    public void SwitchedOffHidesEverything()
    {
        Assert.Equal(PillState.Hidden, PillLayout.StateFor(DictationPhase.Recording, false, true, enabled: false));
    }

    [Fact]
    public void BarsRestWhileQuietAndGrowWithTheVoice()
    {
        Assert.Equal(PillLayout.BarWidth, PillLayout.BarHeight(0.1f, silent: false));
        Assert.Equal(PillLayout.BarMaxHeight, PillLayout.BarHeight(1f, silent: false));
        Assert.Equal(PillLayout.BarWidth, PillLayout.BarHeight(1f, silent: true));
        Assert.True(PillLayout.BarHeight(0.4f, false) > PillLayout.BarHeight(0.2f, false));
    }

    [Fact]
    public void ThinkingBarsStayInTheirRange()
    {
        for (double t = 0; t < 3; t += 0.1)
        {
            for (int i = 0; i < PillLayout.BarCount; i++)
            {
                var h = PillLayout.ThinkingBarHeight(t, i);
                Assert.InRange(h, PillLayout.BarWidth, PillLayout.BarWidth + 7);
            }
        }
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3754, "1:02:34")]
    public void ClockShowsMinutesAndHours(int seconds, string expected)
    {
        Assert.Equal(expected, PillLayout.Clock(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void HintNamesMicrophoneWarnsAndExplainsTheClipboard()
    {
        Assert.Equal("Mikrofon: Headset", PillLayout.HintText(PillState.Recording, "Headset", false, true));
        Assert.Null(PillLayout.HintText(PillState.Recording, "Headset", false, false));
        Assert.Equal("Kein Ton (Headset)", PillLayout.HintText(PillState.Recording, "Headset", true, false));
        Assert.Equal("Kein Ton", PillLayout.HintText(PillState.Recording, null, true, true));
        Assert.Equal("Nicht eingefügt, der Text liegt in der Zwischenablage", PillLayout.HintText(PillState.Copied, null, false, false));
        Assert.Null(PillLayout.HintText(PillState.Done, "Headset", false, true));
    }
}

public class DictationRulesTests
{
    [Fact]
    public void EveryModeNeedsASpeechModelAndAiModesAProvider()
    {
        Assert.True(DictationRules.IsModeAvailable(WorkflowType.TapTalk, hasSpeechModel: true, canRewrite: false));
        Assert.False(DictationRules.IsModeAvailable(WorkflowType.TapTalk, hasSpeechModel: false, canRewrite: true));
        Assert.False(DictationRules.IsModeAvailable(WorkflowType.DampfAblassen, hasSpeechModel: true, canRewrite: false));
        Assert.True(DictationRules.IsModeAvailable(WorkflowType.EmojiText, hasSpeechModel: true, canRewrite: true));
    }

    [Fact]
    public void ReasonsAreGerman()
    {
        Assert.Equal(TranscriptionException.NoModelMessage, DictationRules.UnavailableReason(WorkflowType.EmojiText, false, false));
        Assert.Equal(Diktat.AI.TextRewriterException.NoModelMessage, DictationRules.UnavailableReason(WorkflowType.TextImprover, true, false));
        Assert.Null(DictationRules.UnavailableReason(WorkflowType.TapTalk, true, false));
    }

    [Fact]
    public void CustomModeNamesWin()
    {
        var settings = new SettingsContainer();
        settings.DampfAblassen.CustomName = "  Ruhig bleiben ";
        Assert.Equal("Ruhig bleiben", DictationRules.DisplayName(settings, WorkflowType.DampfAblassen));
        Assert.Equal("Diktat :-)", DictationRules.DisplayName(settings, WorkflowType.EmojiText));
    }

    [Theory]
    [InlineData("Paul Muster", "Paul")]
    [InlineData("  Anna ", "Anna")]
    [InlineData("DOMAIN\\paul", null)]
    [InlineData("paul@example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FirstNameIgnoresLogins(string? display, string? expected)
    {
        Assert.Equal(expected, DictationRules.FirstName(display));
    }

    [Fact]
    public void CaretStepsCountLineBreaksAndEmojisOnce()
    {
        Assert.Equal(5, DictationRules.CaretSteps("Hallo"));
        Assert.Equal(3, DictationRules.CaretSteps("a\r\nb"));
        Assert.Equal(3, DictationRules.CaretSteps("a\nb"));
        Assert.Equal(3, DictationRules.CaretSteps("a👍🏽b"));
    }

    [Fact]
    public void FittingSkipsPasswordAndUnreadFields()
    {
        var field = new FocusedField { TextBefore = "Hallo", TextAfter = "" };
        Assert.Equal(Diktat.Core.Text.InsertionFitter.Fit("Welt", "Hallo", ""), DictationRules.FitToField("Welt", field, enabled: true));
        Assert.StartsWith(" ", DictationRules.FitToField("Welt", field, enabled: true));
        Assert.Equal("Welt", DictationRules.FitToField("Welt", field with { IsPassword = true }, enabled: true));
        Assert.Equal("Welt", DictationRules.FitToField("Welt", null, enabled: true));
        Assert.Equal("Welt", DictationRules.FitToField("Welt", field, enabled: false));
    }

    [Fact]
    public void AiSeesTheFieldOnlyWhenAllowed()
    {
        var field = new FocusedField { TextBefore = "Liebe Anna,", TextAfter = "Grüße" };
        Assert.NotNull(DictationRules.AiContext(field, allowed: true));
        Assert.Null(DictationRules.AiContext(field, allowed: false));
        Assert.Null(DictationRules.AiContext(field with { IsPassword = true }, allowed: true));
    }

    [Fact]
    public void CopiedOutcomesSayWhereTheTextIs()
    {
        Assert.Equal(DictationRules.PastedStatus, DictationRules.StatusFor(PasteOutcome.Pasted));
        Assert.Equal(DictationRules.CopiedStatus, DictationRules.StatusFor(PasteOutcome.CopiedFocusChanged));
        Assert.Contains("Administrator", DictationRules.StatusFor(PasteOutcome.CopiedTargetElevated));
        Assert.Equal(DictationRules.ClipboardBusy, DictationRules.StatusFor(PasteOutcome.Failed));
    }

    [Theory]
    [InlineData(20, "1 Min.")]
    [InlineData(45 * 60, "45 Min.")]
    [InlineData(123 * 60, "2 Std. 3 Min.")]
    public void MeetingDurationReadsLikeOnTheMac(double seconds, string expected)
    {
        Assert.Equal(expected, DictationRules.MeetingDuration(seconds));
    }

    [Fact]
    public void MeetingToastNamesLengthAndPeopleOnly()
    {
        Assert.Equal("45 Min., 3 Personen. In Diktat unter Meetings.", DictationRules.MeetingFinishedText(45 * 60, 3));
        Assert.StartsWith("1 Min., 1 Person.", DictationRules.MeetingFinishedText(30, 1));
    }

    [Fact]
    public void ReplacedTextMatchesDespiteLineBreakChanges()
    {
        Assert.True(TranscriptReplacer.SameText("Hallo\r\nWelt ", "Hallo\nWelt"));
        Assert.False(TranscriptReplacer.SameText("Hallo", "Hallo Welt"));
    }
}

public class MeetingAudioTests
{
    [Fact]
    public void TheLaterTrackGetsSilenceInFront()
    {
        const long frequency = 1_000_000;
        // The microphone started 0.25 s after the system sound.
        Assert.Equal(4_000, MeetingAudio.LeadingPad(1_250_000, 1_000_000, 16_000, frequency));
        Assert.Equal(0, MeetingAudio.LeadingPad(1_000_000, 1_250_000, 16_000, frequency));
        Assert.Equal(0, MeetingAudio.LeadingPad(0, 1_000_000, 16_000, frequency));
        Assert.Equal(0, MeetingAudio.LeadingPad(1_000_000, 0, 16_000, frequency));
    }

    [Fact]
    public void TracksAreMadeTheSameLength()
    {
        var (a, b) = MeetingAudio.SameLength([1f, 2f, 3f], [1f]);
        Assert.Equal(3, a.Length);
        Assert.Equal(3, b.Length);
        Assert.Equal(0f, b[2]);
    }

    [Fact]
    public void AudioOfAMeetingLiesInTheTempFolder()
    {
        var id = Guid.NewGuid();
        Assert.StartsWith(AppPaths.TempDirectory, MeetingAudio.MicrophonePath(id));
        Assert.EndsWith($"{id}-system.wav", MeetingAudio.SystemAudioPath(id));
    }
}
