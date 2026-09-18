using KOTU.Core.Jobs;
using Xunit;

namespace KOTU.Module.Archive.Tests;

public class ArchiveMultiSourceRoundTripTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySelectedFileAndFolderSurvivesNativeRoundTrip(bool sevenZip)
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-selection-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "첫 파일.txt");
            var second = Path.Combine(root, "second.txt");
            var folder = Path.Combine(root, "folder");
            Directory.CreateDirectory(folder);
            File.WriteAllText(first, "first contents");
            File.WriteAllText(second, "second contents");
            File.WriteAllText(Path.Combine(folder, "nested.txt"), "nested contents");
            var target = Path.Combine(root, sevenZip ? "result.7z" : "result.zip");
            var jobs = new BackgroundJobService();
            var coordinator = new ArchiveJobCoordinator(jobs);
            var created = await coordinator.StartCreate([first, second, folder], target, sevenZip).Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(BackgroundJobState.Succeeded, created.State);
            var backend = new SevenZipBackend();
            Assert.Equal(3, backend.List(target).Count(entry => !entry.IsDirectory));
            var extracted = await coordinator.StartExtractMany([target], true)[0].Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(BackgroundJobState.Succeeded, extracted.State);
            Assert.NotNull(extracted.ResultPath);
            Assert.Equal("first contents", File.ReadAllText(Path.Combine(extracted.ResultPath!, "첫 파일.txt")));
            Assert.Equal("second contents", File.ReadAllText(Path.Combine(extracted.ResultPath!, "second.txt")));
            Assert.Equal("nested contents", File.ReadAllText(Path.Combine(extracted.ResultPath!, "folder", "nested.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MissingSelectedSourceFailsInsteadOfCreatingPartialArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "KOTU-missing-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "existing.txt");
            File.WriteAllText(existing, "keep");
            var target = Path.Combine(root, "result.zip");
            var result = await new ArchiveJobCoordinator(new BackgroundJobService())
                .StartCreate([existing, Path.Combine(root, "missing.txt")], target, false)
                .Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(BackgroundJobState.Failed, result.State);
            Assert.False(File.Exists(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
