using KOTU.Core.Routing;
using KOTU.Core.Threading;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class CloudFileAccessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_listing_recalled_or_unavailable_while_queued_does_not_open_content(bool lookupFails)
    {
        using var worker = new ModuleWorker("cloud preview guard");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var attributes = FileAttributes.Normal;
        var reads = 0;
        var probes = 0;
        var blocked = worker.Run(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return true;
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var preview = worker.Run(ctx => CloudFileAccess.ReadLocal("queued.txt", false, ctx.Cancellation,
                () => { reads++; return "content"; }, _ =>
                {
                    probes++;
                    if (lookupFails) throw new IOException("attribute lookup unavailable");
                    return attributes;
                }));
            // 열거 뒤 워커 실행 전에 상태만 바뀐다. 테스트가 원본 파일을 열 필요는 없다.
            attributes = FileAttributes.Offline;
            release.Set();
            await blocked;
            Assert.Null(await preview);
            Assert.Equal(1, probes);
            Assert.Equal(0, reads);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void Listed_placeholder_stays_restricted_until_a_new_scan_confirms_local()
    {
        var probes = 0;
        var reads = 0;
        FileAttributes Probe(string _) { probes++; return FileAttributes.Normal; }
        string Read() { reads++; return "local content"; }
        Assert.False(CloudFileAccess.CanReadContent("downloaded.txt", true, default, Probe));
        Assert.Null(CloudFileAccess.ReadLocal("downloaded.txt", true, default, Read, Probe));
        Assert.Equal(0, probes);
        Assert.Equal(0, reads);

        // 새 스캔이 false를 넘기면 이전 요청의 제한 상태가 전역 캐시에 남지 않는다.
        Assert.Equal("local content", CloudFileAccess.ReadLocal("downloaded.txt", false, default, Read, Probe));
        Assert.Equal(1, probes);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x400000)]
    [InlineData(0x40000)]
    public void Current_placeholder_disallows_content_and_requires_cached_thumbnail(int attributes)
    {
        Assert.False(CloudFileAccess.CanReadContent("file", false, default, _ => (FileAttributes)attributes));
        var reads = 0;
        Assert.Null(CloudFileAccess.ReadLocal("file", false, default,
            () => { reads++; return "content"; }, _ => (FileAttributes)attributes));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Attribute_failures_do_not_allow_content_or_a_normal_thumbnail_request()
    {
        foreach (var failure in new Exception[] { new IOException(), new UnauthorizedAccessException(), new ArgumentException() })
        {
            var reads = 0;
            Assert.False(CloudFileAccess.CanReadContent("unavailable", false, default, _ => throw failure));
            Assert.Null(CloudFileAccess.ReadLocal("unavailable", false, default,
                () => { reads++; return "content"; }, _ => throw failure));
            Assert.Equal(0, reads);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_precedes_snapshot_short_circuit_and_attribute_probe(bool listedAsPlaceholder)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probes = 0;
        var reads = 0;
        var error = Assert.Throws<OperationCanceledException>(() => CloudFileAccess.ReadLocal(
            "file", listedAsPlaceholder, cancellation.Token,
            () => { reads++; return "content"; }, _ => { probes++; return FileAttributes.Normal; }));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, probes);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_during_attribute_probe_is_not_a_fallback(bool failProbe)
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var error = Assert.Throws<OperationCanceledException>(() => CloudFileAccess.ReadLocal(
            "file", false, cancellation.Token, () => { reads++; return "content"; }, _ =>
            {
                cancellation.Cancel();
                if (failProbe) throw new IOException("attribute lookup failed");
                return FileAttributes.Normal;
            }));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Attribute_provider_cancellation_is_preserved_without_a_canceled_caller_token()
    {
        var original = new OperationCanceledException();
        Assert.Same(original, Assert.Throws<OperationCanceledException>(() =>
            CloudFileAccess.CanReadContent("file", false, default, _ => throw original)));
    }

    [Fact]
    public void Content_read_failures_are_preserved_after_successful_probe()
    {
        var ioFailure = new IOException("content unavailable");
        Assert.Same(ioFailure, Assert.Throws<IOException>(() => CloudFileAccess.ReadLocal<string>(
            "file", false, default, () => throw ioFailure, _ => FileAttributes.Normal)));
        var canceled = new OperationCanceledException();
        Assert.Same(canceled, Assert.Throws<OperationCanceledException>(() => CloudFileAccess.ReadLocal<string>(
            "file", false, default, () => throw canceled, _ => FileAttributes.Normal)));
    }

    [Fact]
    public void Default_probe_allows_a_local_file_and_blocks_a_missing_file_without_opening_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kotu-cloud-guard-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "local content");
            Assert.True(CloudFileAccess.CanReadContent(path, false, default));
            Assert.Equal("local content", CloudFileAccess.ReadLocal(path, false, default, () => File.ReadAllText(path)));
            File.Delete(path);
            var reads = 0;
            Assert.Null(CloudFileAccess.ReadLocal(path, false, default, () => { reads++; return File.ReadAllText(path); }));
            Assert.Equal(0, reads);
        }
        finally { File.Delete(path); }
    }
}
