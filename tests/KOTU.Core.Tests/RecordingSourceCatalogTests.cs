using System.Collections.ObjectModel;
using KOTU.Module.Record;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class RecordingSourceCatalogTests
{
    private static readonly CaptureSource Screen = new("Screen A", "DISPLAY1", 0);
    private static readonly CaptureSource Window = new("Original title", null, 123, 456);
    private static readonly MicrophoneDevice Mic = new("Mic A", "mic-a", true);
    private static readonly OutputDevice Output = new("Speaker A", "out-a", true);

    [Fact]
    public void WindowTitleChangePreservesIdentityButReusedHandleDoesNot()
    {
        Assert.Equal(RecordingSourceCatalog.Key(Window), RecordingSourceCatalog.Key(Window with { Label = "New title" }));
        Assert.NotEqual(RecordingSourceCatalog.Key(Window), RecordingSourceCatalog.Key(Window with { ProcessId = 987 }));
        Assert.NotEqual(RecordingSourceCatalog.Key(Screen), RecordingSourceCatalog.Key(Screen with { DisplayName = "DISPLAY2" }));
        Assert.Null(RecordingSourceCatalog.Missing(new(Window, null, null), new([Window with { Label = "New title" }], [], [])));
        Assert.NotNull(RecordingSourceCatalog.Missing(new(Window, null, null), new([Window with { ProcessId = 987 }], [], [])));
    }

    [Fact]
    public void FailedCategoriesDoNotProveSourceLoss()
    {
        Assert.Null(RecordingSourceCatalog.Missing(new(Screen, Mic.Id, Output.Id), new(null, null, null, "error", "error", "error")));
        Assert.Null(RecordingSourceCatalog.Missing(new(Screen, Mic.Id, Output.Id), new([Screen], null, [Output], MicrophoneError: "error")));
        Assert.Contains("microphone", RecordingSourceCatalog.Missing(new(Screen, Mic.Id, Output.Id), new([Screen], [], [Output]))!);
    }

    [Fact]
    public void ObservationBeforeSelectionChangeCannotEstablishLossOfNewRecordingSource()
    {
        var oldChoice = new RecordingSelection(Screen, Mic.Id, Output.Id);
        Assert.False(RecordingSourceCatalog.Covers(new(Window, Mic.Id, Output.Id), oldChoice));
        Assert.False(RecordingSourceCatalog.Covers(new(Screen, "new-mic", Output.Id), oldChoice));
        Assert.False(RecordingSourceCatalog.Covers(new(Screen, Mic.Id, "new-output"), oldChoice));
        Assert.True(RecordingSourceCatalog.Covers(new(Screen, null, null), oldChoice));
        Assert.True(RecordingSourceCatalog.Covers(new(null, Mic.Id, null), oldChoice));
    }

    [Fact]
    public void SilentVideoDoesNotRequireAudioDevicesAndWavRequiresOnlyItsMic()
    {
        Assert.Null(RecordingSourceCatalog.Missing(new(Screen, null, null), new([Screen], [], [])));
        Assert.Null(RecordingSourceCatalog.Missing(new(null, Mic.Id, null), new([], [Mic], [])));
        Assert.Contains("microphone", RecordingSourceCatalog.Missing(new(null, Mic.Id, null), new([], [], []))!);
    }

    [Fact]
    public void NewDefaultNeverRetargetsSelectedDevice()
    {
        var changed = Output with { IsDefault = false, Label = "Speaker A" };
        var newDefault = new OutputDevice("Speaker B (Default)", "out-b", true);
        Assert.Equal(Output.Id, RecordingSourceCatalog.Select([newDefault, changed], Output.Id, d => d.Id, d => d.IsDefault, false)!.Id);
        Assert.Null(RecordingSourceCatalog.Missing(new(Screen, null, Output.Id), new([Screen], [], [newDefault, changed])));
        Assert.Contains("system audio", RecordingSourceCatalog.Missing(new(Screen, null, Output.Id), new([Screen], [], [newDefault]))!);
    }

    [Fact]
    public void InitialDefaultSelectionIsAllowedOnlyWithoutRememberedIdentity()
    {
        var other = new MicrophoneDevice("Other", "mic-b");
        Assert.Same(Mic, RecordingSourceCatalog.Select([other, Mic], null, d => d.Id, d => d.IsDefault, true));
        Assert.Null(RecordingSourceCatalog.Select([other], Mic.Id, d => d.Id, d => d.IsDefault, true));
        Assert.Null(RecordingSourceCatalog.Select([other, Mic], null, d => d.Id, d => d.IsDefault, false));
        Assert.Same(Mic, RecordingSourceCatalog.Select([other, Mic], Mic.Id, d => d.Id, d => d.IsDefault, false));
    }

    [Fact]
    public void UnchangedSnapshotDoesNotMutateRowsOrReplaceFocusedObjects()
    {
        var rows = new ObservableCollection<CaptureSource> { Screen, Window };
        var changes = 0;
        rows.CollectionChanged += (_, _) => changes++;
        RecordingSourceCatalog.Apply(rows, [Screen with { }, Window with { }], RecordingSourceCatalog.Key);
        Assert.Equal(0, changes);
        Assert.Same(Screen, rows[0]);
        Assert.Same(Window, rows[1]);
    }

    [Fact]
    public void DiffMovesAndRenamesOnlyChangedRows()
    {
        var rows = new ObservableCollection<CaptureSource> { Screen, Window };
        RecordingSourceCatalog.Apply(rows, [Window, Screen], RecordingSourceCatalog.Key);
        Assert.Same(Window, rows[0]);
        Assert.Same(Screen, rows[1]);
        var renamed = Window with { Label = "Changed" };
        RecordingSourceCatalog.Apply(rows, [renamed, Screen], RecordingSourceCatalog.Key);
        Assert.Same(renamed, rows[0]);
        Assert.Same(Screen, rows[1]);
    }

    [Fact]
    public void RemovalAndNewDefaultDoNotCreateReplacementSelection()
    {
        var rows = new ObservableCollection<MicrophoneDevice> { Mic };
        var replacement = new MicrophoneDevice("Other (Default)", "mic-b", true);
        RecordingSourceCatalog.Apply(rows, [replacement], d => d.Id);
        Assert.Single(rows);
        Assert.Null(RecordingSourceCatalog.Select(rows, Mic.Id, d => d.Id, d => d.IsDefault, false));
    }
}
