using KOTU.Core.Contracts;

namespace KOTU.Core.Content;

/// <summary>중첩 콘텐츠의 계약과 열린 경로를 소유한다. XAML과 실제 워커 해제는 뷰의 책임이다.</summary>
public sealed class ChildContentHost : IContentStateSource, IContentInfoProvider, ICloseGuard,
    ITrayStatusProvider, IPlaybackStateSource, IPrintPageProvider, IUntitledContentSource,
    IContentPathChangedSource, IContentInfoChangedSource, IBrowseOrderConsumer,
    ICurrentPathSource, IMediaTransportTarget, IContentCloseRequestSource
{
    private readonly Action<Action> _dispatch;
    private ContentContractSession? _session;
    private bool _dirty;
    private long _generation;
    private string? _browseFolder;
    private IReadOnlyList<string> _browseFiles = [];
    public object? Child => _session?.Content;
    public string? OpenedPath { get; private set; }
    public event Action? StateChanged;
    public event Action<string>? ContentOpened;
    public event Action<string>? ContentPathChanged;
    public event Action<string>? CurrentPathChanged;
    public event Action<bool>? UnsavedChanged;
    public event Action? UntitledOpened;
    public event Action? UntitledWindowRequested;
    public event Action? ContentInfoChanged;
    public event Action? TrayStatusChanged;
    public event Action? PlaybackStateChanged;
    public event Action? PrintRequested;
    public event Action? ContentCloseRequested;
    public event Action? NeighborsChanged;

    public ChildContentHost(Action<Action> dispatch) => _dispatch = dispatch;

    public void Attach(object child, string? initialPath)
    {
        if (_session is not null) throw new InvalidOperationException("Detach the previous child first.");
        ContentContractSession? session = null;
        session = new ContentContractSession(child, _dispatch, () => ReferenceEquals(_session, session));
        _session = session;
        var generation = ++_generation;
        OpenedPath = initialPath;
        session.ContentOpened += path =>
        {
            OpenedPath = path;
            StateChanged?.Invoke();
            if (ReferenceEquals(_session, session)) ContentOpened?.Invoke(path);
        };
        session.UntitledOpened += () =>
        {
            OpenedPath = null;
            StateChanged?.Invoke();
            if (ReferenceEquals(_session, session)) UntitledOpened?.Invoke();
        };
        session.UnsavedChanged += dirty =>
        {
            _dirty = dirty;
            UnsavedChanged?.Invoke(dirty);
        };
        session.ContentPathChanged += value =>
        {
            OpenedPath = value;
            StateChanged?.Invoke();
            if (ReferenceEquals(_session, session)) ContentPathChanged?.Invoke(value);
        };
        session.CurrentPathChanged += value => CurrentPathChanged?.Invoke(value);
        session.UntitledWindowRequested += () => UntitledWindowRequested?.Invoke();
        session.ContentInfoChanged += () => ContentInfoChanged?.Invoke();
        session.TrayStatusChanged += () => TrayStatusChanged?.Invoke();
        session.PlaybackStateChanged += () => PlaybackStateChanged?.Invoke();
        session.PrintRequested += () => PrintRequested?.Invoke();
        session.ContentCloseRequested += () => ContentCloseRequested?.Invoke();
        session.NeighborsChanged += () => NeighborsChanged?.Invoke();
        if (child is IBrowseOrderConsumer browse && _browseFolder is { } folder)
            browse.SetBrowseOrder(folder, _browseFiles);
        NotifyState(session, generation);
    }

    public void Detach(Action removeVisuals)
    {
        var session = _session;
        var generation = ++_generation;
        _session = null;
        session?.Dispose();
        OpenedPath = null;
        var wasDirty = _dirty;
        _dirty = false;
        // 구독 해제 후 바와 센터를 제거한다. Unloaded가 보내는 옛 통지는 이미 무효다.
        removeVisuals();
        if (wasDirty && _generation == generation)
        {
            UnsavedChanged?.Invoke(false);
        }
        if (session is not null) NotifyState(null, generation);
    }

    private void NotifyState(ContentContractSession? session, long generation)
    {
        bool Current() => _generation == generation && ReferenceEquals(_session, session);
        if (!Current()) return;
        StateChanged?.Invoke();
        if (!Current()) return;
        TrayStatusChanged?.Invoke();
        if (!Current()) return;
        PlaybackStateChanged?.Invoke();
        if (!Current()) return;
        NeighborsChanged?.Invoke();
    }

    public void SetBrowseOrder(string folder, IReadOnlyList<string> files)
    {
        _browseFolder = folder;
        _browseFiles = files;
        (Child as IBrowseOrderConsumer)?.SetBrowseOrder(folder, files);
    }

    public bool HasUnsavedChanges => Child is ICloseGuard { HasUnsavedChanges: true };
    public Task<bool> ConfirmCloseAsync() => _session?.ConfirmCloseAsync() ?? Task.FromResult(true);
    public bool HasPlaybackSurface => Child is IPlaybackStateSource { HasPlaybackSurface: true };
    public bool IsPlaying => Child is IPlaybackStateSource { IsPlaying: true };
    private IMediaTransportTarget? TransportChild => Child as IMediaTransportTarget;
    public bool HasMediaTransport => TransportChild is not null;
    public bool CanPrevious => TransportChild is { CanPrevious: true };
    public bool CanNext => TransportChild is { CanNext: true };
    public void Previous() => TransportChild?.Previous();
    public void Next() => TransportChild?.Next();
    public void Play() => TransportChild?.Play();
    public void Pause() => TransportChild?.Pause();
    public void OpenUntitled() => (Child as IUntitledContentSource)?.OpenUntitled();
    public bool CanPrintNow => Child is IPrintPageProvider { CanPrintNow: true };
    public string PrintJobName => (Child as IPrintPageProvider)?.PrintJobName ?? string.Empty;
    public int GetPrintPageCount(PrintPageSpec spec) => (Child as IPrintPageProvider)?.GetPrintPageCount(spec) ?? 0;
    public async Task<object?> CreatePrintPageAsync(int pageNumber, PrintPageSpec spec)
    {
        var session = _session;
        if (session?.Content is not IPrintPageProvider provider) return null;
        var page = await provider.CreatePrintPageAsync(pageNumber, spec);
        return ReferenceEquals(_session, session) ? page : null;
    }

    public async Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync()
    {
        var session = _session;
        if (session?.Content is not IContentInfoProvider provider) return null;
        var info = await provider.GetContentInfoAsync();
        return ReferenceEquals(_session, session) ? info : null;
    }

    public TrayStatus GetTrayStatus()
    {
        if (Child is ITrayStatusProvider provider) return provider.GetTrayStatus();
        if (OpenedPath is not { } path) return TrayStatus.Idle("ALL");
        long bytes = -1;
        try { bytes = new FileInfo(path).Length; }
        catch { /* 파일 크기를 못 읽으면 기존 빈 크기 표시를 유지한다. */ }
        return TrayStatus.Open(TrayFormat.Extension(path), TrayFormat.Size(bytes));
    }
}
