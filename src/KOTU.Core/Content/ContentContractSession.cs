using KOTU.Core.Contracts;

namespace KOTU.Core.Content;

/// <summary>
/// 콘텐츠 한 번의 부착이 소유하는 계약 구독. 해제 후 같은 객체를 다시 붙여도
/// 이전 구독의 예약 콜백은 살아나지 않는다. 화면과 워커의 정리는 소유자가 담당한다.
/// </summary>
public sealed class ContentContractSession : IDisposable
{
    private readonly Action<Action> _dispatch;
    private readonly Func<bool> _isCurrent;
    private int _disposed;
    public object Content { get; }
    public bool IsCurrent => Volatile.Read(ref _disposed) == 0 && _isCurrent();

    public ContentContractSession(object content, Action<Action> dispatch, Func<bool> isCurrent)
    {
        Content = content;
        _dispatch = dispatch;
        _isCurrent = isCurrent;
        if (content is IContentStateSource sourceContentOpened) sourceContentOpened.ContentOpened += OnContentOpened;
        if (content is IContentPathChangedSource sourceContentPathChanged) sourceContentPathChanged.ContentPathChanged += OnContentPathChanged;
        if (content is ICurrentPathSource sourceCurrentPathChanged) sourceCurrentPathChanged.CurrentPathChanged += OnCurrentPathChanged;
        if (content is ICloseGuard sourceUnsavedChanged) sourceUnsavedChanged.UnsavedChanged += OnUnsavedChanged;
        if (content is IUntitledContentSource sourceUntitledOpened) sourceUntitledOpened.UntitledOpened += OnUntitledOpened;
        if (content is IUntitledContentSource sourceUntitledWindowRequested) sourceUntitledWindowRequested.UntitledWindowRequested += OnUntitledWindowRequested;
        if (content is IContentInfoChangedSource sourceContentInfoChanged) sourceContentInfoChanged.ContentInfoChanged += OnContentInfoChanged;
        if (content is ITrayStatusProvider sourceTrayStatusChanged) sourceTrayStatusChanged.TrayStatusChanged += OnTrayStatusChanged;
        if (content is IPlaybackStateSource sourcePlaybackStateChanged) sourcePlaybackStateChanged.PlaybackStateChanged += OnPlaybackStateChanged;
        if (content is IPrintPageProvider sourcePrintRequested) sourcePrintRequested.PrintRequested += OnPrintRequested;
        if (content is IContentCloseRequestSource sourceContentCloseRequested) sourceContentCloseRequested.ContentCloseRequested += OnContentCloseRequested;
        if (content is IWindowShrinkSource sourceShrinkToMinRequested) sourceShrinkToMinRequested.ShrinkToMinRequested += OnShrinkToMinRequested;
        if (content is IMediaTransportTarget sourceNeighborsChanged) sourceNeighborsChanged.NeighborsChanged += OnNeighborsChanged;
    }

    public event Action<string>? ContentOpened;
    private void OnContentOpened(string value) => Dispatch(() => ContentOpened?.Invoke(value));

    public event Action<string>? ContentPathChanged;
    private void OnContentPathChanged(string value) => Dispatch(() => ContentPathChanged?.Invoke(value));

    public event Action<string>? CurrentPathChanged;
    private void OnCurrentPathChanged(string value) => Dispatch(() => CurrentPathChanged?.Invoke(value));

    public event Action<bool>? UnsavedChanged;
    private void OnUnsavedChanged(bool value) => Dispatch(() => UnsavedChanged?.Invoke(value));

    public event Action? UntitledOpened;
    private void OnUntitledOpened() => Dispatch(() => UntitledOpened?.Invoke());

    public event Action? UntitledWindowRequested;
    private void OnUntitledWindowRequested() => Dispatch(() => UntitledWindowRequested?.Invoke());

    public event Action? ContentInfoChanged;
    private void OnContentInfoChanged() => Dispatch(() => ContentInfoChanged?.Invoke());

    public event Action? TrayStatusChanged;
    private void OnTrayStatusChanged() => Dispatch(() => TrayStatusChanged?.Invoke());

    public event Action? PlaybackStateChanged;
    private void OnPlaybackStateChanged() => Dispatch(() => PlaybackStateChanged?.Invoke());

    public event Action? PrintRequested;
    private void OnPrintRequested() => Dispatch(() => PrintRequested?.Invoke());

    public event Action? ContentCloseRequested;
    private void OnContentCloseRequested() => Dispatch(() => ContentCloseRequested?.Invoke());

    public event Action? ShrinkToMinRequested;
    private void OnShrinkToMinRequested() => Dispatch(() => ShrinkToMinRequested?.Invoke());

    public event Action? NeighborsChanged;
    private void OnNeighborsChanged() => Dispatch(() => NeighborsChanged?.Invoke());

    private void Dispatch(Action callback)
    {
        // 발화 스레드에서는 UI를 읽을 수 없다. 소유자 동일성은 디스패치 안에서만 묻는다.
        if (Volatile.Read(ref _disposed) != 0) return;
        _dispatch(() =>
        {
            if (IsCurrent) callback();
        });
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsCurrent) return false;
        var allowed = Content is not ICloseGuard guard || !guard.HasUnsavedChanges ||
            await guard.ConfirmCloseAsync();
        // 버리기는 더티 상태를 유지한 채 허용될 수 있다. 재검사는 부착의 동일성만 본다.
        return allowed && IsCurrent;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Content is IContentStateSource sourceContentOpened) sourceContentOpened.ContentOpened -= OnContentOpened;
        if (Content is IContentPathChangedSource sourceContentPathChanged) sourceContentPathChanged.ContentPathChanged -= OnContentPathChanged;
        if (Content is ICurrentPathSource sourceCurrentPathChanged) sourceCurrentPathChanged.CurrentPathChanged -= OnCurrentPathChanged;
        if (Content is ICloseGuard sourceUnsavedChanged) sourceUnsavedChanged.UnsavedChanged -= OnUnsavedChanged;
        if (Content is IUntitledContentSource sourceUntitledOpened) sourceUntitledOpened.UntitledOpened -= OnUntitledOpened;
        if (Content is IUntitledContentSource sourceUntitledWindowRequested) sourceUntitledWindowRequested.UntitledWindowRequested -= OnUntitledWindowRequested;
        if (Content is IContentInfoChangedSource sourceContentInfoChanged) sourceContentInfoChanged.ContentInfoChanged -= OnContentInfoChanged;
        if (Content is ITrayStatusProvider sourceTrayStatusChanged) sourceTrayStatusChanged.TrayStatusChanged -= OnTrayStatusChanged;
        if (Content is IPlaybackStateSource sourcePlaybackStateChanged) sourcePlaybackStateChanged.PlaybackStateChanged -= OnPlaybackStateChanged;
        if (Content is IPrintPageProvider sourcePrintRequested) sourcePrintRequested.PrintRequested -= OnPrintRequested;
        if (Content is IContentCloseRequestSource sourceContentCloseRequested) sourceContentCloseRequested.ContentCloseRequested -= OnContentCloseRequested;
        if (Content is IWindowShrinkSource sourceShrinkToMinRequested) sourceShrinkToMinRequested.ShrinkToMinRequested -= OnShrinkToMinRequested;
        if (Content is IMediaTransportTarget sourceNeighborsChanged) sourceNeighborsChanged.NeighborsChanged -= OnNeighborsChanged;
    }
}
