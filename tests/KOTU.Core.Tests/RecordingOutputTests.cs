using KOTU.Module.Record;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class RecordingOutputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "KOTU-record-tests-" + Guid.NewGuid().ToString("N"));

    public RecordingOutputTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DiscardPreservesExistingDestinationAndOtherSessions()
    {
        var destination = Path.Combine(_directory, "recording.mp4");
        var temporary = RecordingOutput.CreateTemporaryPath(destination, ".mp4");
        var other = RecordingOutput.CreateTemporaryPath(destination, ".mp4");
        File.WriteAllText(destination, "previous recording");
        File.WriteAllText(temporary, "discard this");
        File.WriteAllText(other, "another active session");
        RecordingOutput.Discard(temporary);
        RecordingOutput.Discard(temporary); // Repeated cleanup is harmless.
        Assert.Equal("previous recording", File.ReadAllText(destination));
        Assert.Equal("another active session", File.ReadAllText(other));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void EmptyRecordingCannotReplaceDestination()
    {
        var destination = Path.Combine(_directory, "recording.wav");
        var temporary = RecordingOutput.CreateTemporaryPath(destination, ".wav");
        File.WriteAllText(destination, "previous recording");
        File.WriteAllBytes(temporary, []);
        Assert.Throws<IOException>(() => RecordingOutput.Publish(temporary, destination));
        Assert.Equal("previous recording", File.ReadAllText(destination));
    }

    [Fact]
    public void SuccessfulFinalizationReplacesOnlyTheChosenFile()
    {
        var destination = Path.Combine(_directory, "recording.wav");
        var temporary = RecordingOutput.CreateTemporaryPath(destination, ".wav");
        File.WriteAllText(destination, "previous recording");
        File.WriteAllText(temporary, "finalized recording");
        RecordingOutput.Publish(temporary, destination);
        Assert.Equal("finalized recording", File.ReadAllText(destination));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void FailedPublishRetainsTemporaryRecording()
    {
        var destination = Path.Combine(_directory, "recording.mp4");
        var temporary = RecordingOutput.CreateTemporaryPath(destination, ".mp4");
        Directory.CreateDirectory(destination); // A directory cannot be overwritten as a file.
        File.WriteAllText(temporary, "recoverable recording");
        var error = Assert.ThrowsAny<Exception>(() => RecordingOutput.Publish(temporary, destination));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("recoverable recording", File.ReadAllText(temporary));
        Assert.True(Directory.Exists(destination));
    }

    public void Dispose()
    {
        // This directory is uniquely owned by this test instance.
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
