#if KOTU_THUMBNAIL_TEST_HARNESS
using System.Collections.Concurrent;
using KOTU.Core.Routing;
using KOTU.Core.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    private static Func<string, bool, bool, ThumbnailRaster.Target, CancellationToken, TilePreview>? HarnessProvider;
    private sealed record HarnessCall(string Path, bool Automatic, bool Audio, ThumbnailRaster.Target Target, long Tick);

    internal static async Task<object> RunHarnessAsync(Window window)
    {
        var results = new List<object>();
        var calls = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var auto = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var details = new ConcurrentQueue<HarnessCall>();
        var autoAudio = 0;
        var surface = new ThumbnailExplorer();
        surface.SetColumns(3);
        window.Content = surface;
        await Task.Delay(600);
        var root = Path.Combine(Path.GetTempPath(), "KOTU-A399-Fake-" + Guid.NewGuid().ToString("N"));
        ExplorerListing.Entry Entry(string name) => new(Path.Combine(root, name), name, false, 1, DateTime.UnixEpoch, DateTime.UnixEpoch);
        static TilePreview Success() => new(new ThumbnailRaster.Pixels(1, 1, [0, 160, 240, 255]), null, ThumbnailOutcome.Available);
        void Count(string path, bool automatic, bool audio, ThumbnailRaster.Target target)
        {
            details.Enqueue(new(path, automatic, audio, target, Environment.TickCount64));
            calls.AddOrUpdate(path, 1, (_, n) => n + 1);
            if (automatic) auto.AddOrUpdate(path, 1, (_, n) => n + 1);
            if (automatic && audio) Interlocked.Increment(ref autoAudio);
        }
        int CountOf(ExplorerListing.Entry entry, bool automatic = false) => (automatic ? auto : calls).GetValueOrDefault(entry.Path);
        bool CompletedMiss(ExplorerListing.Entry entry) => surface.FindVmByPath(entry.Path) is { } vm &&
            !vm.Requests.Token.IsCancellationRequested && !vm.PreviewInFlight && vm.PreviewKnownEmpty;
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static async Task Until(Func<bool> condition, string message, int seconds = 8)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(50);
            Require(condition(), message);
        }
        bool Painted(ExplorerListing.Entry entry) => surface.FindVmByPath(entry.Path) is { } vm &&
            surface.LivePreviewHostOf(vm)?.Children.OfType<Image>().Any() == true;
        HarnessProvider = (path, automatic, audio, target, _) =>
        {
            Count(path, automatic, audio, target);
            var name = Path.GetFileName(path);
            if (name == "timeout.pdf") return new(null, null, ThumbnailOutcome.Timeout);
            if (name == "audiofail.mp3") return new(null, "safe audio", ThumbnailOutcome.CacheMiss, AudioFailed: true);
            if (name == "invalid.pdf") return new(null, null, ThumbnailOutcome.InvalidOrOversized);
            if (name == "forever.pdf") return new(null, null, ThumbnailOutcome.CacheMiss);
            return automatic ? Success() : new(null, null, name == "icon.pdf" ? ThumbnailOutcome.IconFallback : ThumbnailOutcome.CacheMiss);
        };
        var basic = new[] { Entry("recover.pdf"), Entry("icon.pdf"), Entry("forever.pdf"), Entry("timeout.pdf"), Entry("audiofail.mp3"), Entry("invalid.pdf") };
        surface.ShowEntries(root, basic);
        await Until(() => basic.All(entry => CountOf(entry) >= 1), "initial visible requests");
        await Task.Delay(11500);
        Require(Painted(basic[0]) && Painted(basic[1]), "miss and icon did not recover without file events");
        Require(CountOf(basic[0]) == 2 && CountOf(basic[1]) == 2 && CountOf(basic[2]) == 3,
            "2/2/3 request contract: " + string.Join(",", basic.Select(e => e.Name + "=" + CountOf(e))));
        Require(basic.Skip(3).All(entry => CountOf(entry) == 1), "terminal outcome retried");
        results.Add(new { Case = "visible outcomes", Requests = basic.Select(e => new { e.Name, Total = CountOf(e), Automatic = CountOf(e, true) }).ToArray() });

        var editing = Entry("editing.pdf");
        using var release = new ManualResetEventSlim();
        HarnessProvider = (path, automatic, audio, target, _) =>
        {
            Count(path, automatic, audio, target);
            if (!automatic) return new(null, null, ThumbnailOutcome.CacheMiss);
            release.Wait(TimeSpan.FromSeconds(10)); // 취소를 무시하는 합성 제공자.
            return Success();
        };
        surface.ShowEntries(root, [editing]);
        await Until(() => CountOf(editing, true) == 1, "editing scenario automatic start");
        surface.BeginRenameByPath(editing.Path);
        Require(ExplorerRenameBox.IsEditing, "rename did not enter");
        release.Set();
        await Until(() => surface._retryReady.Count == 1, "successful result was not retained while editing");
        Require(!Painted(editing), "result painted during editing");
        var box = FindRetryDescendant<TextBox>(surface.TileGrid);
        Require(box?.Tag is Action<bool>, "rename cancellation hook");
        ((Action<bool>)box!.Tag)(false);
        await Until(() => Painted(editing), "edit ended did not apply saved success");
        Require(CountOf(editing) == 2, "saved success caused extra provider request: " +
            System.Text.Json.JsonSerializer.Serialize(details.Where(call => call.Path == editing.Path)));
        results.Add(new { Case = "success during editing", Total = CountOf(editing), PaintedAfterEdit = true });

        var oldTarget = details.Last(call => call.Path == editing.Path).Target;
        HarnessProvider = (path, automatic, audio, target, _) =>
        {
            Count(path, automatic, audio, target);
            return Success();
        };
        surface.SetColumns(2);
        bool CurrentSizePainted() => surface.FindVmByPath(editing.Path) is { PreviewInFlight: false } vm &&
            surface.LivePreviewHostOf(vm) is { } host && Equals(host.Tag, surface.PreviewTarget(host)) && Painted(editing);
        await Until(() => CountOf(editing) >= 3 && CurrentSizePainted(), "real resize did not refresh current target");
        await Task.Delay(150);
        var resizedTarget = details.Last(call => call.Path == editing.Path).Target;
        Require(oldTarget != resizedTarget && CountOf(editing) == 3 && CountOf(editing, true) == 1,
            "real resize request contract: " + System.Text.Json.JsonSerializer.Serialize(details.Where(call => call.Path == editing.Path)));
        results.Add(new { Case = "real column resize", ExtraOrdinary = 1, OldTarget = oldTarget, NewTarget = resizedTarget, CurrentTargetPainted = true });

        HarnessProvider = (path, automatic, audio, target, _) =>
        {
            Count(path, automatic, audio, target);
            return automatic ? Success() : new(null, null, ThumbnailOutcome.CacheMiss);
        };
        var offscreen = Entry("offscreen.pdf");
        surface.ShowEntries(root, [offscreen]);
        await Until(() => CountOf(offscreen) == 1 && CompletedMiss(offscreen), "offscreen initial");
        var many = Enumerable.Range(0, 90).Select(i => Entry("row" + i + ".pdf")).Append(offscreen).ToArray();
        surface.ShowEntries(root, many);
        surface.TileGrid.ScrollIntoView(surface._vms[0]);
        await Task.Delay(2500);
        Require(!surface.IsRetryVisible(surface.FindVmByPath(offscreen.Path)!), "offscreen item passed viewport check");
        Require(CountOf(offscreen, true) == 0, "offscreen timer launched");
        surface.TileGrid.ScrollIntoView(surface.FindVmByPath(offscreen.Path));
        await Until(() => Painted(offscreen), "viewport entry did not resume");
        Require(CountOf(offscreen, true) == 1, "viewport resume count");
        results.Add(new { Case = "real viewport pause and resume", Automatic = CountOf(offscreen, true) });

        var late = Entry("late.pdf");
        using var lateRelease = new ManualResetEventSlim();
        HarnessProvider = (path, automatic, audio, target, _) =>
        {
            Count(path, automatic, audio, target);
            if (path == late.Path) lateRelease.Wait(TimeSpan.FromSeconds(10));
            return Success();
        };
        surface.ShowEntries(root, [late]);
        await Until(() => CountOf(late) > 0, "late initial");
        var next = Entry("next.pdf");
        surface.ShowEntries(root + "-next", [next]);
        lateRelease.Set();
        await Until(() => Painted(next), "new folder success");
        Require(surface.FindVmByPath(late.Path) is null && surface._retryReady.Keys.All(vm => vm.Path != late.Path), "late result leaked");
        results.Add(new { Case = "late folder result isolated", Passed = true });

        var reload = Entry("reload.pdf");
        HarnessProvider = (path, automatic, audio, target, _) => { Count(path, automatic, audio, target); return new(null, null, ThumbnailOutcome.CacheMiss); };
        surface.ShowEntries(root, [reload]);
        await Until(() => CountOf(reload) > 0 && CompletedMiss(reload), "reload initial completion");
        for (var i = 0; i < 5; i++)
        {
            window.Content = null;
            await Task.Delay(40);
            window.Content = surface;
            await Task.Delay(80);
            await Until(() => CompletedMiss(reload), "reload miss completion");
        }
        await Until(() => CountOf(reload, true) == 2, "reload timer did not survive", 15);
        surface.ShowEntries(root, [reload with { Size = 2 }]);
        await Task.Delay(2200);
        Require(CountOf(reload, true) == 2, "replacement refunded automatic budget");
        Require(autoAudio == 0, "automatic audio access");
        results.Add(new { Case = "reload and replacement budget", Automatic = CountOf(reload, true) });
        window.Content = null;
        return new { FakeProvider = true, Passed = true, FileContentsRead = 0, AudioAutomatic = autoAudio,
            RealWinRtProviderTested = false, Calls = details.ToArray(), Cases = results };
    }
}
#endif
