using KOTU.Core.Contracts;
using KOTU.Core.Routing;
using KOTU.Core.Threading;
using Microsoft.UI.Xaml;

namespace KOTU.App.Overlays;

public sealed partial class ContentInfoOverlay
{
    private const int PrefetchConcurrency = 3;
    private const int SelectionCacheCap = 4000;
    private readonly Dictionary<BrowseInfoKey, IReadOnlyList<ContentInfoItem>> _selectionCache = [];
    private BrowseInfoKey? _activeSelectionKey;
    private ModuleWorkerPool? _prefetchPool;
    private CancellationTokenSource? _prefetchCts;
    private int _prefetchSeq;
    private int _metadataGeneration = -1;
    private ModuleWorkerPool PrefetchPool => _prefetchPool ??= new ModuleWorkerPool(
        "KOTU info prefetch", PrefetchConcurrency, ThreadPriority.BelowNormal);

    /// <summary>Immediate snapshot rows; files may enrich on a worker, folders never start I/O.</summary>
    public void ShowForSelection(BrowseSelection selection)
    {
        if (selection.Entry is not { } entry || selection.Key is not { } key) return;
        Visibility = Visibility.Visible;
        OverlayBorder.IsHitTestVisible = true;
        ShowHint(OverlayHints.Docked(OverlayHints.InfoKey));
        if (_activeSelectionKey == key) return;
        CancelSelectionRequest();
        _activeSelectionKey = key;
        _activePath = entry.Path;
        var seq = ++_seq;
        RenderItems(SelectionQuickInfo.BuildSafeRows(entry));
        // Safety precedes both cache lookup and worker creation, including unknown attributes.
        if (!BrowseInfo.CanReadContent(entry)) return;
        if (_selectionCache.TryGetValue(key, out var cached)) { RenderItems(cached); return; }
        var cts = _selectionCts = new CancellationTokenSource();
        _ = LoadSelectionAsync(entry, key, seq, cts);
    }

    private async Task LoadSelectionAsync(ExplorerListing.Entry entry, BrowseInfoKey key,
        int seq, CancellationTokenSource cts)
    {
        try
        {
            var items = await Worker.Run(context => SelectionQuickInfo.Build(entry, context.Cancellation), cts.Token);
            if (cts.IsCancellationRequested || seq != _seq || _activeSelectionKey != key) return;
            StoreSelectionCache(key, items);
            RenderItems(items);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Keep the snapshot rows already shown. Never retry through a general content builder.
        }
        finally
        {
            if (ReferenceEquals(_selectionCts, cts)) _selectionCts = null;
            cts.Dispose();
        }
    }

    private void CancelSelectionRequest()
    {
        _selectionCts?.Cancel();
        _activeSelectionKey = null;
        ++_seq;
    }

    public void ShowCheckSummary(BrowseCheckSummary checks)
    {
        CancelSelectionRequest();
        _activePath = null;
        Visibility = Visibility.Visible;
        OverlayBorder.IsHitTestVisible = true;
        ShowHint(OverlayHints.Docked(OverlayHints.InfoKey));
        RenderItems(checks.BuildRows());
    }

    /// <summary>Navigation or unload invalidates work before the next scan publishes its snapshot.</summary>
    public void InvalidateSelectionSnapshot()
    {
        CancelSelectionRequest();
        ++_prefetchSeq;
        _metadataGeneration = -1;
        _prefetchCts?.Cancel();
    }

    public void PrefetchSelectionInfo(IReadOnlyList<ExplorerListing.Entry> entries, int generation)
    {
        // Sorting/filtering can publish the same scan repeatedly. Warm it at most once.
        // Newly revealed uncached files remain available through ordinary on-demand selection.
        if (_metadataGeneration == generation) return;
        _prefetchCts?.Cancel();
        _selectionCache.Clear();
        _metadataGeneration = generation;
        var cts = _prefetchCts = new CancellationTokenSource();
        _ = PrefetchAsync(entries, generation, ++_prefetchSeq, cts);
    }

    private async Task PrefetchAsync(IReadOnlyList<ExplorerListing.Entry> entries, int generation,
        int seq, CancellationTokenSource cts)
    {
        using var gate = new SemaphoreSlim(PrefetchConcurrency);
        var running = new List<Task>();
        async Task FetchAsync(ExplorerListing.Entry entry, BrowseInfoKey key)
        {
            try
            {
                var items = await PrefetchPool.Run(context => SelectionQuickInfo.Build(entry, context.Cancellation), cts.Token);
                if (!cts.IsCancellationRequested && seq == _prefetchSeq && generation == _metadataGeneration)
                    StoreSelectionCache(key, items);
            }
            catch (OperationCanceledException) { }
            catch { /* Failed enrichment has no alternate content-reading path. */ }
            finally { gate.Release(); }
        }
        try
        {
            foreach (var entry in entries)
            {
                if (cts.IsCancellationRequested || seq != _prefetchSeq || running.Count >= SelectionCacheCap) break;
                if (!BrowseInfo.CanReadContent(entry) || !ExplorerListing.MatchesExtension(entry.Name,
                    KOTU.Module.Image.ImageFolderNavigator.SupportedExtensions)) continue;
                var key = BrowseInfoKey.From(entry, generation);
                if (_selectionCache.ContainsKey(key)) continue;
                await gate.WaitAsync(cts.Token);
                if (cts.IsCancellationRequested || seq != _prefetchSeq) { gate.Release(); break; }
                running.Add(FetchAsync(entry, key));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await Task.WhenAll(running);
            if (ReferenceEquals(_prefetchCts, cts)) _prefetchCts = null;
            cts.Dispose();
        }
    }

    private void StoreSelectionCache(BrowseInfoKey key, IReadOnlyList<ContentInfoItem> items)
    {
        if (_selectionCache.Count >= SelectionCacheCap) _selectionCache.Clear();
        _selectionCache[key] = items;
    }
}
