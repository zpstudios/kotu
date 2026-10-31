namespace KOTU.DocumentModel;

/// <summary>여러 검색 이동 중 가장 최근 목표만 소유한다. 이전 응답은 소유권을 해제하지 못한다.</summary>
public sealed class EditorSelectionRequestTracker
{
    public readonly record struct Request(long Id, long Generation, string Epoch);
    private long _next;
    private Request? _latest;
    public bool IsPending => _latest is not null;
    public Request Begin(long generation, string epoch)
    {
        var request = new Request(++_next, generation, epoch);
        _latest = request;
        return request;
    }
    public bool IsCurrent(Request request) => _latest == request;
    public bool Complete(Request request)
    {
        if (!IsCurrent(request)) return false;
        _latest = null;
        return true;
    }
    public void Cancel() => _latest = null;
}
