using KOTU.Module.Record;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class RecordingOutputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "KOTU-record-tests-" + Guid.NewGuid().ToString("N"));

    public RecordingOutputTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(true, "Videos")]
    [InlineData(false, "Music")]
    public void DefaultFolderUsesTheMatchingLibraryAndCreatesKotu(bool screen, string library)
    {
        var root = Path.Combine(_directory, library);
        var folder = RecordingOutput.PrepareFolder(null, screen, mode =>
        {
            Assert.Equal(screen, mode);
            return root;
        });
        Assert.Equal(Path.Combine(root, "KOTU"), folder);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
        Assert.Equal(folder, RecordingOutput.PrepareFolder(folder, screen));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void CustomFolderDoesNotResolveOrFallBackToLibrary()
    {
        var custom = Path.Combine(_directory, "custom");
        Assert.Equal(custom, RecordingOutput.PrepareFolder(custom, true, _ => throw new InvalidOperationException("must not resolve")));
        var blocked = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocked, "existing file");
        Assert.Throws<IOException>(() => RecordingOutput.PrepareFolder(blocked, true));
        Assert.Equal("existing file", File.ReadAllText(blocked));
    }

    [Fact]
    public void MissingLibraryOrRelativeFolderFailsExplicitly()
    {
        Assert.Throws<IOException>(() => RecordingOutput.PrepareFolder(null, true, _ => ""));
        Assert.Throws<IOException>(() => RecordingOutput.PrepareFolder("relative-folder", false));
    }

    [Theory]
    [InlineData(true, ".mp4")]
    [InlineData(false, ".wav")]
    public void GeneratedNamesAreDistinctWithoutCreatingEmptyDestinations(bool screen, string extension)
    {
        var timestamp = new DateTime(2026, 10, 6, 11, 22, 33);
        var paths = Enumerable.Range(0, 100).Select(_ => RecordingOutput.CreateDestinationPath(_directory, screen, timestamp)).ToArray();
        Assert.Equal(paths.Length, paths.Distinct().Count());
        Assert.All(paths, path =>
        {
            Assert.Equal(_directory, Path.GetDirectoryName(path));
            Assert.EndsWith(extension, path);
            Assert.Contains("20261006-112233", path);
            Assert.False(File.Exists(path));
        });
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void DirectPublicationRefusesARaceCollisionAndKeepsRecoveryFile()
    {
        var destination = RecordingOutput.CreateDestinationPath(_directory, true, DateTime.Now);
        var temporary = RecordingOutput.CreateTemporaryPath(destination, ".mp4");
        File.WriteAllText(temporary, "new recording");
        File.WriteAllText(destination, "concurrently created file");
        Assert.Throws<IOException>(() => RecordingOutput.Publish(temporary, destination, overwrite: false));
        Assert.Equal("concurrently created file", File.ReadAllText(destination));
        Assert.Equal("new recording", File.ReadAllText(temporary));
    }

    [Fact]
    public void DirectPublicationMovesToANewDestination()
    {
        var destination = RecordingOutput.CreateDestinationPath(_directory, false, DateTime.Now);
        var temporary = RecordingOutput.CreateTemporaryPath(destination, ".wav");
        File.WriteAllText(temporary, "new recording");
        RecordingOutput.Publish(temporary, destination, overwrite: false);
        Assert.Equal("new recording", File.ReadAllText(destination));
        Assert.False(File.Exists(temporary));
    }

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
