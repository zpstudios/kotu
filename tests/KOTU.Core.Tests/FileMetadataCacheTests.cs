using KOTU.Core.Content;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class FileMetadataCacheTests
{
    [Fact]
    public async Task Duplicate_refreshes_share_one_read_and_invalidated_slow_result_cannot_overwrite_new_value()
    {
        var first = new TaskCompletionSource<FileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var cache = new FileMetadataCache((_, _) => ++calls == 1 ? first.Task : Task.FromResult(new FileMetadata(true, 20)));
        var old = cache.GetAsync("a");
        var duplicate = cache.GetAsync("A");
        Assert.Equal(1, calls);
        cache.Invalidate("a");
        Assert.Equal(20L, (await cache.GetAsync("a"))!.Length);
        first.SetResult(new(true, 10));
        Assert.Null(await old);
        Assert.Null(await duplicate);
        Assert.Equal(20L, cache.Peek("A")!.Length);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancelled_file_switch_and_closed_window_discard_late_uninterruptible_reads()
    {
        var slow = new TaskCompletionSource<FileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var cache = new FileMetadataCache((path, _) => path == "old" ? slow.Task : Task.FromResult(new FileMetadata(true, 99)));
        var old = cache.GetAsync("old", cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(99L, (await cache.GetAsync("new"))!.Length);
        cache.Dispose();
        slow.SetResult(new(true, 1));
        Assert.Null(await old);
        Assert.Null(cache.Peek("new"));
        Assert.Null(await cache.GetAsync("new"));
    }

    [Fact]
    public async Task Failed_lookup_is_unknown_and_cache_capacity_is_bounded()
    {
        using var cache = new FileMetadataCache((_, _) => throw new IOException("unavailable"), capacity: 2);
        Assert.Equal(FileMetadata.Unknown, await cache.GetAsync("a"));
        await cache.GetAsync("b");
        await cache.GetAsync("c");
        Assert.Null(cache.Peek("a"));
        Assert.NotNull(cache.Peek("b"));
        Assert.NotNull(cache.Peek("c"));
    }

    [Fact]
    public async Task File_growth_and_deletion_are_observed_after_invalidation()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tmp");
        using var cache = new FileMetadataCache((file, _) => Task.Run(() => FileMetadata.Read(file)));
        try
        {
            await File.WriteAllBytesAsync(path, new byte[10]);
            Assert.Equal(10L, (await cache.GetAsync(path))!.Length);
            await File.WriteAllBytesAsync(path, new byte[20]);
            cache.Invalidate(path);
            Assert.Equal(20L, (await cache.GetAsync(path))!.Length);
            File.Delete(path);
            cache.Invalidate(path);
            Assert.Equal(new FileMetadata(false, null), await cache.GetAsync(path));
        }
        finally { File.Delete(path); }
    }
}
