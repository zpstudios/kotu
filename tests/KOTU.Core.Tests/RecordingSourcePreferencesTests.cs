using KOTU.Core.Settings;
using KOTU.Module.Record;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class RecordingSourcePreferencesTests
{
    private static readonly CaptureSource Screen = new("Screen", "display", 0);
    private static readonly CaptureSource Window = new("Window", null, 123, 456);
    private static readonly CaptureSource Camera = new("Camera", null, 0, CameraId: "camera");

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    [InlineData("object")]
    public void LegacyCameraMigratesLazilyWithoutCreatingAStartupWrite(string type)
    {
        var settings = new MemorySettingsService();
        settings.Set("record.sourceKey", "camera:old");
        if (type == "null") settings.Set<string?>("record.cameraKey", null);
        if (type == "number") settings.Set("record.cameraKey", 42);
        if (type == "object") settings.Set("record.cameraKey", new { value = "camera:bad" });
        var preferences = RecordingSourcePreferences.Read(settings);
        Assert.Equal("", preferences.VideoKey);
        Assert.Equal("camera:old", preferences.CameraKey);
        Assert.Equal("camera:old", settings.Get("record.sourceKey", ""));
        Assert.Null(settings.Get<string?>("record.cameraKey", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("camera:new")]
    [InlineData("display:wrong-kind")]
    [InlineData("invalid")]
    public void ExplicitCameraStringWinsOverLegacyEvenWhenEmptyOrInvalid(string key)
    {
        var settings = new MemorySettingsService();
        settings.Set("record.sourceKey", "camera:old");
        settings.Set("record.cameraKey", key);
        Assert.Equal(new RecordingSourcePreferences("", key), RecordingSourcePreferences.Read(settings));
    }

    [Theory]
    [InlineData("display:missing")]
    [InlineData("window:123:456")]
    [InlineData("unknown")]
    public void NonCameraKeysStayIndependentAndUnknownPreferencesNeverDefault(string key)
    {
        var settings = new MemorySettingsService();
        settings.Set("record.sourceKey", key);
        var preferences = RecordingSourcePreferences.Read(settings);
        Assert.Equal(key, preferences.VideoKey);
        Assert.Equal("", preferences.CameraKey);
        var selected = RecordingSourceCatalog.SelectVisual([Screen, Window], preferences.VideoKey, false, true);
        Assert.Equal(key == RecordingSourceCatalog.Key(Window) ? Window : null, selected);
        Assert.Null(RecordingSourceCatalog.SelectVisual([Camera], preferences.CameraKey, true, true));
    }

    [Fact]
    public void IndependentSelectionsRetainOtherKindAndRestoreOnlyExactReappearance()
    {
        var videoKey = RecordingSourceCatalog.Key(Window);
        var cameraKey = RecordingSourceCatalog.Key(Camera);
        Assert.Same(Window, RecordingSourceCatalog.SelectVisual([Screen, Window], videoKey, false, false));
        Assert.Same(Camera, RecordingSourceCatalog.SelectVisual([Camera], cameraKey, true, false));
        Assert.Null(RecordingSourceCatalog.SelectVisual([Camera with { CameraId = "replacement" }], cameraKey, true, false));
        Assert.Same(Window, RecordingSourceCatalog.SelectVisual([Screen, Window], videoKey, false, false));
        var returned = Camera with { Label = "Renamed" };
        Assert.Same(returned, RecordingSourceCatalog.SelectVisual([returned], cameraKey, true, false));
        Assert.Null(RecordingSourceCatalog.SelectVisual([Screen], videoKey, false, false));
        Assert.Same(returned, RecordingSourceCatalog.SelectVisual([returned], cameraKey, true, false));
        Assert.Same(Screen, RecordingSourceCatalog.SelectVisual([Screen, Window], "", false, true));
        Assert.Null(RecordingSourceCatalog.SelectVisual([Camera], "", true, true));
    }

    [Fact]
    public void WrongKindRememberedKeysCannotCrossCategories()
    {
        var mixed = new[] { Screen, Window, Camera };
        Assert.Null(RecordingSourceCatalog.SelectVisual(mixed, RecordingSourceCatalog.Key(Camera), false, true));
        Assert.Null(RecordingSourceCatalog.SelectVisual(mixed, RecordingSourceCatalog.Key(Screen), true, true));
    }

    [Fact]
    public void ActiveOverlayRetainsIndependentInactivePreferencesAndExactRequiredAudio()
    {
        var idle = new RecordingDiscoverySelection(Window, Camera, "idle-mic", "idle-output");
        var activeCamera = Camera with { CameraId = "active-camera" };
        var observed = idle.WithActive(new(activeCamera, "active-mic", null));
        Assert.Same(Window, observed.VideoSource);
        Assert.Same(activeCamera, observed.CameraSource);
        Assert.Equal("active-mic", observed.MicrophoneId);
        Assert.Equal("idle-output", observed.OutputId);
        Assert.True(RecordingSourceCatalog.CoversObservation(new(activeCamera, "active-mic", null), observed));
        Assert.False(RecordingSourceCatalog.CoversObservation(new(Camera, "active-mic", null), observed));
        var screenObserved = idle.WithActive(new(Screen, null, "active-output"));
        Assert.Same(Camera, screenObserved.CameraSource);
        Assert.Same(Screen, screenObserved.VideoSource);
        Assert.Equal("idle-mic", screenObserved.MicrophoneId);
        Assert.Equal("active-output", screenObserved.OutputId);
        var audioObserved = idle.WithActive(new(null, "active-mic", null));
        Assert.Same(Window, audioObserved.VideoSource);
        Assert.Same(Camera, audioObserved.CameraSource);
        Assert.Same(idle, idle.WithActive(null));
    }

    [Fact]
    public void OldCappedObservationCannotProveNewKindIdentityOrAudioLoss()
    {
        var observed = new RecordingDiscoverySelection(Screen, Camera, "mic", "output");
        Assert.False(RecordingSourceCatalog.CoversObservation(new(Window, "mic", "output"), observed));
        Assert.False(RecordingSourceCatalog.CoversObservation(new(Camera with { CameraId = "new" }, "mic", "output"), observed));
        Assert.False(RecordingSourceCatalog.CoversObservation(new(Camera, "new-mic", "output"), observed));
        Assert.False(RecordingSourceCatalog.CoversObservation(new(Screen, "mic", "new-output"), observed));
        Assert.True(RecordingSourceCatalog.CoversObservation(new(null, "mic", null), observed));
    }

    [Fact]
    public void InactiveCategoryLossAndFailedCategoriesDoNotStopTheActiveSelection()
    {
        var screen = new RecordingSelection(Screen, null, null);
        var camera = new RecordingSelection(Camera, null, null);
        Assert.Null(RecordingSourceCatalog.Missing(screen, new([Screen], [], [], Cameras: [])));
        Assert.Null(RecordingSourceCatalog.Missing(camera, new([], [], [], Cameras: [Camera])));
        Assert.Null(RecordingSourceCatalog.Missing(camera, new([], [], [], CameraError: "failed")));
        Assert.NotNull(RecordingSourceCatalog.Missing(camera, new([Screen], [], [], Cameras: [])));
        Assert.NotNull(RecordingSourceCatalog.Missing(screen, new([], [], [], Cameras: [Camera])));
    }

    [Fact]
    public void CameraCapKeepsIndependentPreferenceWhenVideoIsAlsoSelected()
    {
        var preferred = Camera with { CameraId = "camera-99" };
        var discovery = new RecordingDiscoverySelection(Window, preferred, null, null);
        var items = Enumerable.Range(0, 100).Select(i => Camera with { CameraId = "camera-" + i });
        var limited = RecordingSourceCatalog.LimitCameras(items, discovery.CameraSource!.CameraId);
        Assert.Equal(64, limited.Count);
        Assert.Equal(preferred, RecordingSourceCatalog.SelectVisual(limited, RecordingSourceCatalog.Key(preferred), true, false));
        Assert.Same(Window, discovery.VideoSource);
    }
}
