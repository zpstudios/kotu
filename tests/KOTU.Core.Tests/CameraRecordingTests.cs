using System.Diagnostics;
using KOTU.Module.Record;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class CameraRecordingTests
{
    private static readonly CaptureSource Camera = new("Camera: Test", null, 0, CameraId: "test-camera");
    private static readonly CaptureSource Screen = new("Screen", "display-test", 0);
    private static long At(double seconds) => 1 + (long)(seconds * Stopwatch.Frequency);

    [Theory]
    [InlineData("camera-interface", "camera-interface")]
    [InlineData("CAMERA-Interface", "camera-INTERFACE")]
    public void NativeCameraResolutionPreservesNativeSpellingAndWinRtSelection(string selectedId, string nativeId)
    {
        var selected = Camera with { CameraId = selectedId };
        var key = RecordingSourceCatalog.Key(selected);
        var resolved = RecordingSourceCatalog.ResolveNativeCameraId(selected.CameraId!, ["other-camera", nativeId]);
        Assert.Equal(nativeId, resolved);
        Assert.Equal("camera:" + selectedId, RecordingSourceCatalog.Key(selected));
        Assert.Same(selected, RecordingSourceCatalog.SelectVisual([selected], key, true, false));
    }

    [Fact]
    public void MissingNativeCameraNeverFallsBackToAnotherDevice()
    {
        Assert.Null(RecordingSourceCatalog.ResolveNativeCameraId("selected-camera", []));
        Assert.Null(RecordingSourceCatalog.ResolveNativeCameraId("selected-camera", ["other-camera", "selected-camera-extra"]));
        Assert.Null(RecordingSourceCatalog.ResolveNativeCameraId("", [""]));
    }

    [Theory]
    [InlineData("selected-camera", "SELECTED-CAMERA")]
    [InlineData("selected-camera", "selected-camera")]
    public void AmbiguousNativeCameraFailsEvenWhenOneMatchHasExactSpelling(string first, string second)
    {
        Assert.Null(RecordingSourceCatalog.ResolveNativeCameraId("selected-camera", [first, "other-camera", second]));
    }

    [Fact]
    public void CameraIdentityNeverBecomesAWindowAndNameChangesPreserveSelection()
    {
        Assert.Equal(CaptureSourceKind.Camera, Camera.Kind);
        Assert.Equal("camera:test-camera", RecordingSourceCatalog.Key(Camera));
        Assert.NotEqual(RecordingSourceCatalog.Key(new("Window", null, 0)), RecordingSourceCatalog.Key(Camera));
        var renamed = Camera with { Label = "Camera: Renamed" };
        Assert.Same(renamed, RecordingSourceCatalog.SelectVisual([renamed], RecordingSourceCatalog.Key(Camera), true, false));
        Assert.Null(RecordingSourceCatalog.Missing(new(Camera, null, null), new([], [], [], Cameras: [renamed])));
    }

    [Fact]
    public void CameraIsNeverTheInitialOrReplacementDefault()
    {
        Assert.Null(RecordingSourceCatalog.SelectVisual([Camera], null, true, true));
        Assert.Null(RecordingSourceCatalog.SelectVisual([Camera], null, true, false));
        Assert.Same(Screen, RecordingSourceCatalog.SelectVisual([Screen], null, false, true));
        Assert.Null(RecordingSourceCatalog.SelectVisual([Camera], "camera:missing", true, true));
        Assert.Null(RecordingSourceCatalog.SelectVisual([Screen], "window:1:2", false, true));
        Assert.Same(Camera, RecordingSourceCatalog.SelectVisual([Camera], RecordingSourceCatalog.Key(Camera), true, false));
    }

    [Fact]
    public void FailedCameraObservationIsNotRemovalAndDoesNotHideOtherCategories()
    {
        var active = new RecordingSelection(Camera, null, null);
        Assert.Null(RecordingSourceCatalog.Missing(active, new([], [], [], CameraError: "failed")));
        Assert.Contains("camera", RecordingSourceCatalog.Missing(active, new(null, [], [], ScreenError: "failed", Cameras: []))!);
        Assert.Null(RecordingSourceCatalog.Missing(active, new(null, [], [], ScreenError: "failed", Cameras: [Camera])));
        Assert.Null(RecordingSourceCatalog.Missing(new(Screen, null, null), new([Screen], [], [], CameraError: "failed")));
    }

    [Fact]
    public void CameraObservationCannotProveAnotherCameraOrAudioSourceWasLost()
    {
        var previous = new RecordingSelection(Camera, "mic-a", "out-a");
        Assert.False(RecordingSourceCatalog.Covers(new(Camera with { CameraId = "new" }, null, null), previous));
        Assert.False(RecordingSourceCatalog.Covers(new(Camera, "mic-b", null), previous));
        Assert.True(RecordingSourceCatalog.Covers(new(Camera, null, null), previous));
        Assert.Contains("microphone", RecordingSourceCatalog.Missing(previous, new([], [], [new("Output", "out-a", false)], Cameras: [Camera]))!);
        Assert.Null(RecordingSourceCatalog.Missing(new(Camera, null, null), new([], [], [], Cameras: [Camera])));
    }

    [Fact]
    public void CameraCapRetainsExactPreferredDeviceWithoutAutoSelectingIt()
    {
        var candidates = Enumerable.Range(0, 100).Select(i => Camera with { CameraId = "camera-" + i }).ToArray();
        var limited = RecordingSourceCatalog.LimitCameras(candidates, "camera-99");
        Assert.Equal(64, limited.Count);
        Assert.Equal("camera-99", limited[^1].CameraId);
        Assert.DoesNotContain(limited, c => c.CameraId == "camera-64");
        Assert.Null(RecordingSourceCatalog.SelectVisual(limited, null, true, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CameraNeedsNativeRecordingAndItsOwnFirstFrameInEitherOrder(bool frameFirst)
    {
        var guard = new CameraRecordingGuard(At(0));
        if (frameFirst) guard.Frame(At(1)); else guard.NativeRecording(At(1));
        Assert.False(guard.IsRecording);
        var starting = RecordingPresentation.Create(RecordingPhase.Active, RecordingKind.Camera, guard.IsRecording, false, false, TimeSpan.Zero, Camera.Label);
        Assert.StartsWith("Starting camera recording", starting.Text);
        Assert.False(starting.IsRecording);
        if (frameFirst) guard.NativeRecording(At(2)); else guard.Frame(At(2));
        Assert.True(guard.IsRecording);
        Assert.Equal(At(2), guard.Snapshot.Started);
        var active = RecordingPresentation.Create(RecordingPhase.Active, RecordingKind.Camera, guard.IsRecording, false, false, TimeSpan.Zero, Camera.Label);
        Assert.StartsWith("Recording camera", active.Text);
        Assert.Equal("VRC", active.Tray.Line1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupDeadlineStopsOnceAndRetainsFailureEvenWithLateSuccessfulCompletion(bool nativeStatus)
    {
        var guard = new CameraRecordingGuard(At(0));
        if (nativeStatus) guard.NativeRecording(At(1)); else guard.Frame(At(1));
        Assert.False(guard.Check(At(9.99)));
        Assert.True(guard.Check(At(10)));
        guard.Frame(At(11));
        guard.NativeRecording(At(11));
        Assert.False(guard.IsRecording);
        Assert.False(guard.Check(At(20)));
        Assert.Contains("10 seconds", guard.Complete(null)!.Error!);
        Assert.Null(guard.Complete("second callback"));
    }

    [Fact]
    public void SourceFrameDeadlineResetsOnlyOnFreshSourceFrames()
    {
        var guard = new CameraRecordingGuard(At(0));
        guard.NativeRecording(At(1));
        guard.Frame(At(1));
        Assert.False(guard.Check(At(5.99)));
        guard.Frame(At(5.99));
        guard.NativeRecording(At(9));
        Assert.False(guard.Check(At(10.98)));
        Assert.True(guard.Check(At(10.99)));
        Assert.False(guard.IsRecording);
        Assert.Contains("stopped providing frames", guard.Complete("native later error")!.Error!);
    }

    [Fact]
    public void UserStopAndNativeTerminalBlockLateStatusFramesAndAutomaticStops()
    {
        var guard = new CameraRecordingGuard(At(0));
        guard.Stop(At(0));
        guard.NativeRecording(At(1));
        guard.Frame(At(1));
        Assert.False(guard.IsRecording);
        Assert.False(guard.Check(At(30)));
        Assert.Null(guard.Complete(null)!.Error);
        Assert.Null(guard.Complete("duplicate"));
        var earlyFailure = new CameraRecordingGuard(At(0));
        Assert.Equal("denied", earlyFailure.Complete("denied")!.Error);
        earlyFailure.NativeRecording(At(2));
        earlyFailure.Frame(At(2));
        Assert.False(earlyFailure.IsRecording);
        Assert.False(earlyFailure.Check(At(30)));
    }

    [Fact]
    public async Task GuardDeadlineAndCompletionRaceHasExactlyOneTerminalOutcome()
    {
        for (var i = 0; i < 30; i++)
        {
            var guard = new CameraRecordingGuard(At(0));
            using var ready = new Barrier(2);
            var timeout = Task.Run(() => { ready.SignalAndWait(); return guard.Check(At(10)); });
            var completion = Task.Run(() => { ready.SignalAndWait(); return guard.Complete("native error"); });
            await Task.WhenAll(timeout, completion);
            var result = await completion;
            Assert.NotNull(result);
            Assert.Equal(await timeout ? "The camera did not start within 10 seconds. Check Windows camera privacy settings, close other apps using the camera, then refresh and try again. The camera format may not be supported." : "native error", result.Error);
            Assert.Null(guard.Complete(null));
            Assert.False(guard.IsRecording);
            Assert.False(guard.Check(At(30)));
        }
    }

    [Fact]
    public void CameraFileNameUsesThePreparedFolderAndExistingNonOverwriteTransaction()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kotu-camera-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var destination = RecordingOutput.CreateDestinationPath(folder, RecordingKind.Camera, new DateTime(2026, 10, 26));
            Assert.StartsWith("KOTU-camera-", Path.GetFileName(destination));
            Assert.Equal(".mp4", Path.GetExtension(destination));
            Assert.Equal(folder, Path.GetDirectoryName(destination));
            Assert.False(File.Exists(destination));
            var temporary = RecordingOutput.CreateTemporaryPath(destination, ".mp4");
            File.WriteAllText(temporary, "camera partial");
            File.WriteAllText(destination, "existing");
            Assert.Throws<IOException>(() => RecordingOutput.Publish(temporary, destination, overwrite: false));
            Assert.Equal("existing", File.ReadAllText(destination));
            Assert.Equal("camera partial", File.ReadAllText(temporary));
            RecordingOutput.Discard(temporary);
            Assert.Equal("existing", File.ReadAllText(destination));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FailedUserStopRestoresConfirmedCaptureAndRunningClock()
    {
        var guard = new CameraRecordingGuard(At(0));
        guard.NativeRecording(At(1));
        guard.Frame(At(2));
        guard.Stop(At(3));
        Assert.False(guard.IsRecording);
        Assert.Equal(At(3), guard.Snapshot.Ended);
        guard.StopFailed();
        Assert.True(guard.IsRecording);
        Assert.Equal(0, guard.Snapshot.Ended);
        guard.Frame(At(4));
        Assert.False(guard.Check(At(8.99)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NativeFinishingTerminalOrGuardFailureCannotBeRevivedByAStopException(int ending)
    {
        var guard = new CameraRecordingGuard(At(0));
        guard.NativeRecording(At(1));
        guard.Frame(At(2));
        if (ending == 2) Assert.True(guard.Check(At(7)));
        guard.Stop(At(8));
        if (ending == 0) guard.NativeFinishing(At(9));
        if (ending == 1) guard.Complete(null, At(9));
        var ended = guard.Snapshot;
        guard.StopFailed();
        guard.Frame(At(10));
        guard.NativeRecording(At(10));
        Assert.Equal(ended, guard.Snapshot);
        Assert.False(guard.IsRecording);
        if (ending == 2) Assert.Contains("stopped providing frames", guard.Snapshot.PendingStopReason!);
    }

    [Fact]
    public async Task ConcurrentStopAndCallbacksFreezeOneConsistentTerminalClock()
    {
        for (var i = 0; i < 30; i++)
        {
            var guard = new CameraRecordingGuard(At(0));
            using var ready = new Barrier(3);
            await Task.WhenAll(
                Task.Run(() => { ready.SignalAndWait(); guard.Frame(At(3)); }),
                Task.Run(() => { ready.SignalAndWait(); guard.NativeRecording(At(2)); }),
                Task.Run(() => { ready.SignalAndWait(); guard.Complete(null, At(1)); }));
            var frozen = guard.Snapshot;
            Assert.True(frozen.Ended >= frozen.Started);
            guard.Frame(At(20));
            guard.NativeRecording(At(20));
            guard.StopFailed();
            guard.Check(At(30));
            Assert.Equal(frozen, guard.Snapshot);
        }
    }

    [Fact]
    public void GuardFailureIsVisibleWhileCompletionIsStillPending()
    {
        var guard = new CameraRecordingGuard(At(0));
        Assert.True(guard.Check(At(10)));
        Assert.NotNull(guard.Snapshot.PendingStopReason);
        guard.StopFailed();
        Assert.NotNull(guard.Snapshot.PendingStopReason);
        Assert.NotNull(guard.Complete(null, At(12))!.Error);
        Assert.Null(guard.Snapshot.PendingStopReason);
    }

    [Fact]
    public void UserStopKeepsRawStartupEvidenceWithoutChangingItsFrozenPresentation()
    {
        var guard = new CameraRecordingGuard(At(0));
        guard.Stop(At(1));
        var stopping = guard.Snapshot;
        guard.NativeRecording(At(2));
        guard.Frame(At(3));
        Assert.Equal(stopping, guard.Snapshot);
        guard.StopFailed();
        Assert.True(guard.IsRecording);
        Assert.Equal(At(3), guard.Snapshot.Started);
        Assert.False(guard.Check(At(7.99)));
        Assert.True(guard.Check(At(8)));
    }

    [Fact]
    public void LongFailedUserStopUsesFramesObservedDuringTheAttempt()
    {
        var guard = new CameraRecordingGuard(At(0));
        guard.NativeRecording(At(1));
        guard.Frame(At(1));
        guard.Stop(At(2));
        var stopping = guard.Snapshot;
        guard.Frame(At(15));
        Assert.Equal(stopping, guard.Snapshot);
        guard.StopFailed();
        Assert.False(guard.Check(At(19.99)));
        Assert.True(guard.Check(At(20)));
    }

    [Fact]
    public void NativeFinishingBeforeFirstFrameRemainsVisibleAfterUserStopThrows()
    {
        var guard = new CameraRecordingGuard(At(0));
        guard.Stop(At(1));
        guard.NativeFinishing(At(2));
        guard.StopFailed();
        guard.Frame(At(3));
        guard.NativeRecording(At(3));
        Assert.Equal(0, guard.Snapshot.Started);
        Assert.Contains("finishing", guard.Snapshot.PendingStopReason!);
        Assert.False(guard.IsRecording);
        Assert.False(guard.Check(At(30)));
    }
}
