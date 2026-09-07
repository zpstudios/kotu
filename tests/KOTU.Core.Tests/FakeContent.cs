using KOTU.Core.Contracts;

namespace KOTU.Core.Tests;

internal sealed class FakeContent : IContentStateSource, IContentPathChangedSource, ICurrentPathSource, ICloseGuard, IUntitledContentSource, IContentInfoChangedSource, ITrayStatusProvider, IPlaybackStateSource, IPrintPageProvider, IContentCloseRequestSource, IWindowShrinkSource, IMediaTransportTarget,
    IContentInfoProvider, IBrowseOrderConsumer
{
    public event Action<string>? ContentOpened;
    public void RaiseContentOpened(string value) => ContentOpened?.Invoke(value);
    public event Action<string>? ContentPathChanged;
    public void RaiseContentPathChanged(string value) => ContentPathChanged?.Invoke(value);
    public event Action<string>? CurrentPathChanged;
    public void RaiseCurrentPathChanged(string value) => CurrentPathChanged?.Invoke(value);
    public event Action<bool>? UnsavedChanged;
    public void RaiseUnsavedChanged(bool value) => UnsavedChanged?.Invoke(value);
    public event Action? UntitledOpened;
    public void RaiseUntitledOpened() => UntitledOpened?.Invoke();
    public event Action? UntitledWindowRequested;
    public void RaiseUntitledWindowRequested() => UntitledWindowRequested?.Invoke();
    public event Action? ContentInfoChanged;
    public void RaiseContentInfoChanged() => ContentInfoChanged?.Invoke();
    public event Action? TrayStatusChanged;
    public void RaiseTrayStatusChanged() => TrayStatusChanged?.Invoke();
    public event Action? PlaybackStateChanged;
    public void RaisePlaybackStateChanged() => PlaybackStateChanged?.Invoke();
    public event Action? PrintRequested;
    public void RaisePrintRequested() => PrintRequested?.Invoke();
    public event Action? ContentCloseRequested;
    public void RaiseContentCloseRequested() => ContentCloseRequested?.Invoke();
    public event Action? ShrinkToMinRequested;
    public void RaiseShrinkToMinRequested() => ShrinkToMinRequested?.Invoke();
    public event Action? NeighborsChanged;
    public void RaiseNeighborsChanged() => NeighborsChanged?.Invoke();
    public int SubscriptionCount => (ContentOpened?.GetInvocationList().Length ?? 0) +
        (ContentPathChanged?.GetInvocationList().Length ?? 0) +
        (CurrentPathChanged?.GetInvocationList().Length ?? 0) +
        (UnsavedChanged?.GetInvocationList().Length ?? 0) +
        (UntitledOpened?.GetInvocationList().Length ?? 0) +
        (UntitledWindowRequested?.GetInvocationList().Length ?? 0) +
        (ContentInfoChanged?.GetInvocationList().Length ?? 0) +
        (TrayStatusChanged?.GetInvocationList().Length ?? 0) +
        (PlaybackStateChanged?.GetInvocationList().Length ?? 0) +
        (PrintRequested?.GetInvocationList().Length ?? 0) +
        (ContentCloseRequested?.GetInvocationList().Length ?? 0) +
        (ShrinkToMinRequested?.GetInvocationList().Length ?? 0) +
        (NeighborsChanged?.GetInvocationList().Length ?? 0);
    public bool HasUnsavedChanges { get; set; }
    public Func<Task<bool>> Confirm { get; set; } = () => Task.FromResult(true);
    public Task<bool> ConfirmCloseAsync() => Confirm();
    public bool HasPlaybackSurface { get; set; }
    public bool IsPlaying { get; set; }
    public bool HasMediaTransport => true;
    public bool CanPrevious => true;
    public bool CanNext => false;
    public List<string> Commands { get; } = [];
    public void Previous() => Commands.Add("previous");
    public void Next() => Commands.Add("next");
    public void Play() => Commands.Add("play");
    public void Pause() => Commands.Add("pause");
    public void OpenUntitled() => Commands.Add("untitled");
    public bool CanPrintNow => true;
    public string PrintJobName => "test job";
    public int GetPrintPageCount(PrintPageSpec spec) => 2;
    public object PrintPage { get; } = new();
    public Task<object?>? PendingPage { get; set; }
    public Task<IReadOnlyList<ContentInfoItem>?>? PendingInfo { get; set; }
    public Task<object?> CreatePrintPageAsync(int pageNumber, PrintPageSpec spec) => PendingPage ?? Task.FromResult<object?>(PrintPage);
    public Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync() =>
        PendingInfo ?? Task.FromResult<IReadOnlyList<ContentInfoItem>?>([new("Format", "test")]);
    public TrayStatus GetTrayStatus() => TrayStatus.Idle("TEST");
    public string? BrowseFolder { get; private set; }
    public IReadOnlyList<string>? BrowseFiles { get; private set; }
    public void SetBrowseOrder(string folder, IReadOnlyList<string> files)
    {
        BrowseFolder = folder;
        BrowseFiles = files;
    }
}
