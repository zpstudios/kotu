using KOTU.Module.Record;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class RecordingPresentationTests
{
    [Fact]
    public void PreparingAndNativeStartupCannotClaimActiveCapture()
    {
        var preparing = Present(RecordingPhase.Starting, recording: false);
        var notYetStarted = Present(RecordingPhase.Active, recording: false);
        Assert.False(preparing.IsRecording);
        Assert.False(notYetStarted.IsRecording);
        Assert.StartsWith("Starting screen recording", preparing.Text);
        Assert.Equal("PRE", notYetStarted.Tray.Line1);
        Assert.NotEqual(0xFFFF5050u, preparing.Tray.Line1Color);
    }

    [Theory]
    [InlineData(true, "Recording screen", "VRC")]
    [InlineData(false, "Recording microphone", "ARC")]
    public void ConfirmedCaptureShowsModeTimeAndCapturedSource(bool screen, string label, string tray)
    {
        var live = Present(RecordingPhase.Active, screen, recording: true);
        Assert.True(live.IsRecording);
        Assert.StartsWith(label, live.Text);
        Assert.Contains("01:02:03", live.Text);
        Assert.Contains("Original source", live.Detail);
        Assert.Equal(tray, live.Tray.Line1);
        Assert.Equal("01:02:03", live.Tray.Line2);
        Assert.Equal(0xFFFF5050u, live.Tray.Line1Color);
    }

    [Theory]
    [InlineData((int)RecordingPhase.Stopping, "STP")]
    [InlineData((int)RecordingPhase.Saving, "SAV")]
    [InlineData((int)RecordingPhase.Finished, "END")]
    [InlineData((int)RecordingPhase.Error, "ERR")]
    public void StopAndTerminalStatesOverrideALateRecordingSnapshot(int phaseValue, string tray)
    {
        var phase = (RecordingPhase)phaseValue;
        var state = Present(phase, recording: true, previouslyRecorded: true);
        Assert.False(state.IsRecording);
        Assert.Equal(phase, state.Phase);
        Assert.Equal(tray, state.Tray.Line1);
        Assert.NotEqual(0xFFFF5050u, state.Tray.Line1Color);
    }

    [Fact]
    public void CompletionWinsEvenWhenTheCaptureSnapshotStillSaysRecording()
    {
        var completed = Present(RecordingPhase.Active, recording: true, completed: true);
        Assert.False(completed.IsRecording);
        Assert.Equal(RecordingPhase.Saving, completed.Phase);
        Assert.Equal("SAV", completed.Tray.Line1);
    }

    [Fact]
    public void NativeFinishingAfterObservedCaptureIsSavingInsteadOfStarting()
    {
        var ended = Present(RecordingPhase.Active, recording: false, previouslyRecorded: true);
        Assert.False(ended.IsRecording);
        Assert.Equal(RecordingPhase.Saving, ended.Phase);
    }

    [Fact]
    public void ANewAttemptResetsElapsedAndNeverReusesThePreviousActiveState()
    {
        var old = Present(RecordingPhase.Active, recording: true);
        var next = RecordingPresentation.Create(RecordingPhase.Starting, false, false, false, false, TimeSpan.Zero, "New microphone");
        Assert.True(old.IsRecording);
        Assert.False(next.IsRecording);
        Assert.Contains("00:00:00", next.Text);
        Assert.Contains("New microphone", next.Detail);
        Assert.DoesNotContain("Original source", next.Detail);
    }

    [Fact]
    public void IdleCannotBecomeRedFromAnUnrelatedBusyOrOldCaptureSnapshot()
    {
        var idle = Present(RecordingPhase.Ready, recording: true, previouslyRecorded: true);
        Assert.False(idle.IsRecording);
        Assert.Equal("Ready", idle.Text);
        Assert.True(idle.Tray.IsIdle);
    }

    private static RecordingPresentation Present(RecordingPhase phase, bool screen = true, bool recording = false,
        bool completed = false, bool previouslyRecorded = false) =>
        RecordingPresentation.Create(phase, screen, recording, completed, previouslyRecorded,
            new TimeSpan(1, 2, 3), "Original source");
}
