using KOTU.Core.Settings;
using KOTU.Module.Record;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class RecordingStartPolicyTests
{
    private static readonly CaptureSource Screen = new("Screen A", "display-a", 0);
    private static readonly CaptureSource Window = new("Window A", null, 123, 456);
    private static readonly CaptureSource Camera = new("Camera A", null, 0, CameraId: "camera-a");
    private static readonly MicrophoneDevice Mic = new("Mic A", "mic-a");
    private static readonly OutputDevice Output = new("Output A", "out-a", false);
    private static readonly RecordingFolderState Folder = new(1, new(""), "prepared-folder", null, null, null);
    private static RecordingStartInput Ready => new()
    {
        Folder = Folder, Video = Screen, Camera = Camera, Microphone = Mic, Output = Output,
        IncludeMicrophone = true, IncludeOutput = true, CamerasObserved = true, CameraCount = 1,
        Preferences = new(RecordingSourceCatalog.Key(Screen), RecordingSourceCatalog.Key(Camera)),
        MicrophonePreference = Mic.Id, OutputPreference = Output.Id,
    };

    [Theory]
    [InlineData((int)RecordingKind.Screen, "display:display-a", ".mp4")]
    [InlineData((int)RecordingKind.Camera, "camera:camera-a", ".mp4")]
    [InlineData((int)RecordingKind.Microphone, null, ".wav")]
    public void ExplicitKindSelectsOnlyItsOwnSourceAndOutputContract(int value, string? sourceKey, string extension)
    {
        var request = RecordingStartPolicy.Evaluate((RecordingKind)value, Ready).Request!;
        Assert.NotNull(request);
        Assert.Equal(sourceKey, request.Source is null ? null : RecordingSourceCatalog.Key(request.Source));
        Assert.Equal(extension, request.Extension);
        Assert.Equal(Mic.Id, request.MicrophoneId);
        Assert.Equal(request.IsVisual ? Output.Id : null, request.OutputId);
        Assert.Equal(request.Source, request.Selection.Source);
        Assert.Same(Folder, request.Folder);
    }

    [Fact]
    public void VisualKindsNeverAcceptTheOtherKindsSelection()
    {
        Assert.False(RecordingStartPolicy.Evaluate(RecordingKind.Screen, Ready with { Video = Camera }).CanStart);
        Assert.False(RecordingStartPolicy.Evaluate(RecordingKind.Camera, Ready with { Camera = Screen }).CanStart);
        Assert.False(RecordingStartPolicy.Evaluate(RecordingKind.Screen, Ready with { Video = null }).CanStart);
        Assert.False(RecordingStartPolicy.Evaluate(RecordingKind.Camera, Ready with { Camera = null }).CanStart);
        Assert.Same(Window, RecordingStartPolicy.Evaluate(RecordingKind.Screen, Ready with { Video = Window }).Request!.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioAlwaysUsesOnlySelectedMicrophoneRegardlessOfVisualCheckboxes(bool include)
    {
        var input = Ready with { IncludeMicrophone = include, IncludeOutput = include, Video = null, Camera = null, Output = null };
        var request = RecordingStartPolicy.Evaluate(RecordingKind.Microphone, input).Request!;
        Assert.Equal(Mic.Id, request.MicrophoneId);
        Assert.Null(request.Source);
        Assert.Null(request.OutputId);
        Assert.Contains("Microphone: Mic A", request.Labels);
        Assert.DoesNotContain("System audio", request.Labels);
        Assert.False(RecordingStartPolicy.Evaluate(RecordingKind.Microphone, input with { Microphone = null }).CanStart);
    }

    [Theory]
    [InlineData((int)RecordingKind.Screen)]
    [InlineData((int)RecordingKind.Camera)]
    public void VisualAudioRequirementsMatchEffectiveCapturedEndpoints(int kind)
    {
        var silent = Ready with { IncludeMicrophone = false, IncludeOutput = false, Microphone = null, Output = null };
        var request = RecordingStartPolicy.Evaluate((RecordingKind)kind, silent).Request!;
        Assert.Null(request.MicrophoneId);
        Assert.Null(request.OutputId);
        Assert.True(request.IsVisual);
        Assert.Contains("system audio output", RecordingStartPolicy.Evaluate((RecordingKind)kind, silent with { IncludeOutput = true }).Detail);
        Assert.Contains("Choose a microphone", RecordingStartPolicy.Evaluate((RecordingKind)kind, silent with { IncludeMicrophone = true }).Detail);
        var micOnly = RecordingStartPolicy.Evaluate((RecordingKind)kind, Ready with { IncludeOutput = false }).Request!;
        Assert.Equal(Mic.Id, micOnly.MicrophoneId);
        Assert.Null(micOnly.OutputId);
        var outputOnly = RecordingStartPolicy.Evaluate((RecordingKind)kind, Ready with { IncludeMicrophone = false }).Request!;
        Assert.Null(outputOnly.MicrophoneId);
        Assert.Equal(Output.Id, outputOnly.OutputId);
    }

    [Theory]
    [InlineData("disposed")]
    [InlineData("busy")]
    [InlineData("starting")]
    [InlineData("close")]
    [InlineData("picker")]
    [InlineData("folder-init")]
    [InlineData("source-init")]
    [InlineData("folder-error")]
    [InlineData("no-folder")]
    public void CommonPrerequisitesBlockEveryKind(string condition)
    {
        var input = condition switch
        {
            "disposed" => Ready with { Disposed = true },
            "busy" => Ready with { Busy = true },
            "starting" => Ready with { Starting = true },
            "close" => Ready with { CloseDialog = true },
            "picker" => Ready with { ChoosingFolder = true },
            "folder-init" => Ready with { FolderInitializing = true },
            "source-init" => Ready with { CatalogInitializing = true },
            "folder-error" => Ready with { Folder = Folder with { Error = "Access denied" } },
            _ => Ready with { Folder = null },
        };
        foreach (var kind in Enum.GetValues<RecordingKind>())
        {
            var decision = RecordingStartPolicy.Evaluate(kind, input);
            Assert.False(decision.CanStart);
            Assert.Null(decision.Request);
            Assert.NotEmpty(decision.Detail);
        }
    }

    [Theory]
    [InlineData((int)RecordingPhase.Starting, "starting")]
    [InlineData((int)RecordingPhase.Active, "Stop and save")]
    [InlineData((int)RecordingPhase.Stopping, "stopping or saving")]
    [InlineData((int)RecordingPhase.Saving, "stopping or saving")]
    [InlineData((int)RecordingPhase.Error, "Stop and save")]
    [InlineData((int)RecordingPhase.Finished, "Stop and save")]
    public void OwnershipOutlastsPresentationAndAllBusyPhasesBlockStarts(int phase, string reason)
    {
        foreach (var kind in Enum.GetValues<RecordingKind>())
        {
            var decision = RecordingStartPolicy.Evaluate(kind, Ready with { Busy = true, Phase = (RecordingPhase)phase });
            Assert.False(decision.CanStart);
            Assert.Contains(reason, decision.Detail);
        }
        Assert.True(RecordingStartPolicy.Evaluate(RecordingKind.Screen, Ready with { Phase = RecordingPhase.Error }).CanStart);
    }

    [Fact]
    public void CameraReasonsDistinguishEmptyFailedAndUnselectedWithoutGuessingNativeAvailability()
    {
        var input = Ready with { Camera = null };
        Assert.Contains("Choose an available camera", RecordingStartPolicy.Evaluate(RecordingKind.Camera, input).Detail);
        Assert.Contains("No cameras available", RecordingStartPolicy.Evaluate(RecordingKind.Camera, input with { CameraCount = 0 }).Detail);
        Assert.Contains("Could not refresh cameras", RecordingStartPolicy.Evaluate(RecordingKind.Camera, input with { CameraError = "failed" }).Detail);
        Assert.True(RecordingStartPolicy.Evaluate(RecordingKind.Camera, Ready with { CameraError = "failed" }).CanStart);
    }

    [Fact]
    public void AcceptedSnapshotKeepsOriginalInputsAndPreparedFolderAfterLaterChanges()
    {
        using var temp = new TestDirectory();
        var first = Path.Combine(temp.Path, "first");
        var input = Ready with { Folder = Folder with { Spec = new(first), DisplayPath = first } };
        var request = RecordingStartPolicy.Evaluate(RecordingKind.Camera, input).Request!;
        input = input with
        {
            Camera = Camera with { CameraId = "other", Label = "Other camera" },
            Microphone = Mic with { Id = "other-mic" }, Output = Output with { Id = "other-output" },
            Folder = Folder with { Spec = new(Path.Combine(temp.Path, "second")) },
            Preferences = new("display:other", "camera:other"), IncludeMicrophone = false,
        };
        var prepared = RecordingOutput.PrepareFolder(request.Folder.Spec!, () => throw new InvalidOperationException());
        var destination = RecordingOutput.CreateDestinationPath(prepared, request.Kind, DateTime.Now);
        Assert.Equal(first, Path.GetDirectoryName(destination));
        Assert.StartsWith("KOTU-camera-", Path.GetFileName(destination));
        Assert.EndsWith(request.Extension, destination);
        Assert.False(File.Exists(destination));
        Assert.Equal(Camera, request.Source);
        Assert.Equal(Mic.Id, request.MicrophoneId);
        Assert.Equal(Output.Id, request.OutputId);
        Assert.Contains("Camera A", request.Labels);
        Assert.True(request.IncludeMicrophonePreference);
        Assert.NotEqual(input.Preferences, request.Preferences);
    }

    [Theory]
    [InlineData((int)RecordingKind.Screen)]
    [InlineData((int)RecordingKind.Camera)]
    [InlineData((int)RecordingKind.Microphone)]
    public void AcceptedKindSavesBothRememberedVisualChoicesAndRawAudioPreferences(int kind)
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var input = Ready with { IncludeMicrophone = false, Preferences = new("window:10:20", "camera:missing") };
        var request = RecordingStartPolicy.Evaluate((RecordingKind)kind, input).Request!;
        request.SavePreferences(new JsonSettingsService(path));
        var restarted = new JsonSettingsService(path);
        Assert.Equal(input.Preferences, RecordingSourcePreferences.Read(restarted));
        Assert.False(restarted.Get("record.includeMicrophone", true));
        Assert.True(restarted.Get("record.includeSystemAudio", false));
        Assert.Equal(Mic.Id, restarted.Get("record.microphoneId", ""));
        Assert.Equal(Output.Id, restarted.Get("record.outputId", ""));
        Assert.Equal(request.IsVisual ? "screen" : "microphone", restarted.Get("record.mode", ""));
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KOTU-a403-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
