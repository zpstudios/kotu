using System.Threading.Channels;
using KOTU.Core.IO;
using KOTU.Core.Threading;
using KOTU.Module.Image;
using Xunit;

namespace KOTU.Module.Image.Tests;

public sealed class ImageFileWatchTests
{
    [Fact]
    public async Task Atomic_replace_notifies_the_viewer_and_keeps_the_old_read_consistent()
    {
        var folder = Directory.CreateTempSubdirectory();
        var current = Path.Combine(folder.FullName, "image.png");
        var replacement = Path.Combine(folder.FullName, "image.tmp");
        var actions = Channel.CreateUnbounded<Action>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var worker = new ModuleWorker("image watcher test");
        var changed = false;
        var watch = new ImageFileWatch(worker, actions.Writer.TryWrite, batch => changed |= batch.Contains(current));
        try
        {
            File.WriteAllBytes(current, [1]);
            File.WriteAllBytes(replacement, [2]);
            watch.SetFolder(folder.FullName);
            await worker.Run(_ => true);
            using var reading = SharedRead.OpenReplaceable(current);
            File.Replace(replacement, current, destinationBackupFileName: null);
            while (!changed) (await actions.Reader.ReadAsync(timeout.Token))();
            Assert.Equal(1, reading.ReadByte());
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(current));
        }
        finally
        {
            watch.SetFolder(null);
            await worker.Run(_ => true);
            File.Delete(current);
            File.Delete(replacement);
            folder.Delete();
        }
    }

    [Fact]
    public async Task Switching_folders_ignores_an_old_callback_already_in_the_owner_queue()
    {
        var first = Directory.CreateTempSubdirectory();
        var second = Directory.CreateTempSubdirectory();
        var oldPath = Path.Combine(first.FullName, "same.png");
        var newPath = Path.Combine(second.FullName, "same.png");
        var actions = Channel.CreateUnbounded<Action>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var worker = new ModuleWorker("image watcher test");
        var seen = new List<WatchChanges>();
        var watch = new ImageFileWatch(worker, actions.Writer.TryWrite, seen.Add);
        try
        {
            watch.SetFolder(first.FullName);
            await worker.Run(_ => true);
            File.WriteAllBytes(oldPath, [1]);
            var stale = await actions.Reader.ReadAsync(timeout.Token);
            watch.SetFolder(second.FullName);
            stale();
            Assert.Empty(seen);
            await worker.Run(_ => true);
            File.WriteAllBytes(newPath, [2]);
            while (!seen.Any(batch => batch.Contains(newPath)))
                (await actions.Reader.ReadAsync(timeout.Token))();
            Assert.DoesNotContain(seen, batch => batch.Contains(oldPath));
        }
        finally
        {
            watch.SetFolder(null);
            await worker.Run(_ => true);
            File.Delete(oldPath);
            File.Delete(newPath);
            first.Delete();
            second.Delete();
        }
    }
}
