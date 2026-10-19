namespace KOTU.DocumentModel;

/// <summary>One worker, one latest request, one queued delivery. Dispose disconnects UI references.</summary>
public sealed class TextSearchWorker : IDisposable
{
    private sealed record Request(TextSearchSnapshot Snapshot, string Query, CancellationTokenSource Cancellation);
    private readonly object _gate = new();
    private Func<Action, bool>? _dispatch;
    private Action<TextSearchResult>? _apply;
    private Request? _latest;
    private Request? _pending;
    private TextSearchResult? _delivery;
    private bool _running;
    private bool _deliveryQueued;
    private bool _disposed;
    private readonly int _debounceMilliseconds;

    public TextSearchWorker(Func<Action, bool> dispatch, Action<TextSearchResult> apply, int debounceMilliseconds = 150)
    {
        _dispatch = dispatch;
        _apply = apply;
        _debounceMilliseconds = debounceMilliseconds;
    }

    public void Submit(TextSearchSnapshot snapshot, string query)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelCore();
            _latest = _pending = new(snapshot, query, new());
            if (_running) return;
            _running = true;
            _ = Task.Run(RunAsync);
        }
    }

    public void Cancel()
    {
        lock (_gate) CancelCore();
    }

    private void CancelCore()
    {
        _latest?.Cancellation.Cancel();
        // A request still in the mailbox has never reached the worker and owns no in-flight token.
        _pending?.Cancellation.Dispose();
        _latest = _pending = null;
        _delivery = null;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Request request;
            lock (_gate)
            {
                if (_disposed || _pending is null) { _running = false; return; }
                request = _pending;
                _pending = null;
            }
            try
            {
                await Task.Delay(_debounceMilliseconds, request.Cancellation.Token).ConfigureAwait(false);
                var result = TextSearch.Find(request.Snapshot, request.Query, request.Cancellation.Token);
                lock (_gate)
                {
                    if (_disposed || !ReferenceEquals(_latest, request)) continue;
                    _delivery = result;
                    if (!_deliveryQueued)
                    {
                        _deliveryQueued = true;
                        if (_dispatch?.Invoke(Deliver) != true)
                        {
                            _deliveryQueued = false;
                            _delivery = null;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_latest, request)) _latest = null;
                    request.Cancellation.Dispose();
                }
            }
        }
    }

    private void Deliver()
    {
        lock (_gate)
        {
            _deliveryQueued = false;
            var result = _delivery;
            _delivery = null;
            if (!_disposed && result is not null) _apply?.Invoke(result);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CancelCore();
            _dispatch = null;
            _apply = null;
        }
    }
}
