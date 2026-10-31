namespace KOTU.Core.Content;

/// <summary>호출자 scope가 끝날 때 원래 콘텐츠가 남아 있으면 닫기 승인을 취소한다.</summary>
public sealed class ContentCloseLease : IAsyncDisposable
{
    private Func<Task>? _cancel;
    public ContentCloseLease(Func<Task> cancel) => _cancel = cancel;
    public async ValueTask DisposeAsync()
    {
        var cancel = Interlocked.Exchange(ref _cancel, null);
        if (cancel is not null) await cancel();
    }
}
