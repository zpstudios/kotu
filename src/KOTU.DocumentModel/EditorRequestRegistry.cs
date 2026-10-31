namespace KOTU.DocumentModel;

/// <summary>응답의 세션·요청·리비전 수명을 검증한다. 시간 제한은 호스트가 WaitAsync로 건다.</summary>
public sealed class EditorRequestRegistry : IDisposable
{
    private readonly string _epoch;
    private readonly Dictionary<long, Request> _pending = [];
    private long _next;
    private bool _disposed;
    public EditorRequestRegistry(string epoch) => _epoch = epoch;
    public int PendingCount => _pending.Count;

    public Request Begin(string kind, int expectedVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending.Count >= 32) throw new InvalidOperationException("Too many pending editor requests.");
        var request = new Request(++_next, kind, expectedVersion);
        _pending.Add(request.Id, request);
        return request;
    }

    public bool TryGet(string epoch, long id, out Request? request)
    {
        request = null;
        return !_disposed && epoch == _epoch && _pending.TryGetValue(id, out request);
    }

    public bool Complete(string epoch, long id, int version, EditorSnapshot? snapshot, string? error = null)
    {
        if (!TryGet(epoch, id, out var request) || request is null) return false;
        _pending.Remove(id);
        if (error is not null) request.Completion.TrySetException(new InvalidOperationException(error));
        else if (version < request.ExpectedVersion)
            request.Completion.TrySetException(new InvalidDataException("Stale editor response."));
        else request.Completion.TrySetResult(snapshot);
        return true;
    }

    public void Abandon(Request request) => _pending.Remove(request.Id);
    public void Fail(Exception error)
    {
        _disposed = true;
        foreach (var request in _pending.Values) request.Completion.TrySetException(error);
        _pending.Clear();
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var request in _pending.Values) request.Completion.TrySetCanceled();
        _pending.Clear();
    }

    public sealed class Request
    {
        internal Request(long id, string kind, int expectedVersion)
        { Id = id; Kind = kind; ExpectedVersion = expectedVersion; }
        public long Id { get; }
        public string Kind { get; }
        public int ExpectedVersion { get; }
        public TaskCompletionSource<EditorSnapshot?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
