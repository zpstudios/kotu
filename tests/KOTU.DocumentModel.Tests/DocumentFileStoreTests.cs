using System.Text;
using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class DocumentFileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kotu-save-test-{Guid.NewGuid():N}");
    private readonly DocumentFileStore _store = new();

    public DocumentFileStoreTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateAndReplacePreserveExactBytesAndStamp(bool existing)
    {
        var path = Path.Combine(_directory, "document.txt");
        if (existing) File.WriteAllText(path, "old content longer than new bytes");
        byte[] bytes = [0xff, 0xfe, 0x41, 0, 13, 0, 10, 0];
        var result = _store.WriteAndVerify(path, bytes);
        Assert.True(result.Verified);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(_store.HasChanged(path, result.Stamp));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void FailedReplacementPreservesOriginalAndRemovesTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows 파일 공유 잠금 계약.
        var path = Path.Combine(_directory, "document.txt");
        File.WriteAllText(path, "original");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => _store.WriteAndVerify(path, Encoding.UTF8.GetBytes("replacement")));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ReadOnlyFileIsNotReplaced()
    {
        var path = Path.Combine(_directory, "read-only.txt");
        File.WriteAllText(path, "original");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<IOException>(() => _store.WriteAndVerify(path, [1, 2, 3]));
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void ChangedOrDeletedFileInvalidatesStamp()
    {
        var path = Path.Combine(_directory, "document.txt");
        var saved = _store.WriteAndVerify(path, [1]);
        File.WriteAllBytes(path, [1, 2]);
        Assert.True(_store.HasChanged(path, saved.Stamp));
        File.Delete(path);
        Assert.True(_store.HasChanged(path, saved.Stamp));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
