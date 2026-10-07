using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KOTU.Core.Contracts;
using KOTU.Core.Content;
using KOTU.Core.Diagnostics;
using KOTU.Core.Routing;

namespace KOTU.Module.AllReadable;

/// <summary>
/// 파일 형식별 자식의 센터와 하단 바만 교체한다. 셸의 모듈 정체성과 전 형식 탐색 목록은 유지한다.
/// 계약과 열린 경로는 ChildContentHost, XAML과 Unloaded를 통한 워커 해제는 이 뷰가 담당한다.
/// </summary>
public sealed partial class AllReadableView : UserControl, ISettingsSnapshotSource, IContentStateSource, IContentInfoProvider,
    IBottomBarProvider, IDriveStripHost, IBackgroundJobOwner, ICloseGuard, IFileOpenTarget, ITrayStatusProvider,
    IPlaybackStateSource, IPrintPageProvider, IUntitledContentSource, IContentPathChangedSource,
    IContentInfoChangedSource, IBrowseOrderConsumer, ICurrentPathSource, IMediaTransportTarget,
    IContentCloseRequestSource, IContentOpenFailedSource, IBottomOverlayConsumer
{
    private readonly IReadOnlyList<IModule> _children;
    private readonly ChildContentHost _content;
    private bool _driveStripShown;
    private object? _bottomOverlay;

    public void SetBottomOverlay(object overlay)
    {
        _bottomOverlay = overlay;
        (_content.Child as IBottomOverlayConsumer)?.SetBottomOverlay(overlay);
    }
    private static long _nextJournalView;
    private readonly long _journalView = Interlocked.Increment(ref _nextJournalView);
    private long _transitionGeneration;
    private string? _childModuleId;

    public event Action<string>? ContentOpened
    {
        add => _content.ContentOpened += value;
        remove => _content.ContentOpened -= value;
    }
    public event Action<string>? ContentPathChanged
    {
        add => _content.ContentPathChanged += value;
        remove => _content.ContentPathChanged -= value;
    }
    public event Action<string>? CurrentPathChanged
    {
        add => _content.CurrentPathChanged += value;
        remove => _content.CurrentPathChanged -= value;
    }
    public event Action<bool>? UnsavedChanged
    {
        add => _content.UnsavedChanged += value;
        remove => _content.UnsavedChanged -= value;
    }
    public event Action? UntitledOpened
    {
        add => _content.UntitledOpened += value;
        remove => _content.UntitledOpened -= value;
    }
    public event Action? UntitledWindowRequested
    {
        add => _content.UntitledWindowRequested += value;
        remove => _content.UntitledWindowRequested -= value;
    }
    public event Action? ContentInfoChanged
    {
        add => _content.ContentInfoChanged += value;
        remove => _content.ContentInfoChanged -= value;
    }
    public event Action? TrayStatusChanged
    {
        add => _content.TrayStatusChanged += value;
        remove => _content.TrayStatusChanged -= value;
    }
    public event Action? PlaybackStateChanged
    {
        add => _content.PlaybackStateChanged += value;
        remove => _content.PlaybackStateChanged -= value;
    }
    public event Action? PrintRequested
    {
        add => _content.PrintRequested += value;
        remove => _content.PrintRequested -= value;
    }
    public event Action? ContentCloseRequested
    {
        add => _content.ContentCloseRequested += value;
        remove => _content.ContentCloseRequested -= value;
    }
    public event Action? ContentOpenFailed
    {
        add => _content.ContentOpenFailed += value;
        remove => _content.ContentOpenFailed -= value;
    }
    public event Action? NeighborsChanged
    {
        add => _content.NeighborsChanged += value;
        remove => _content.NeighborsChanged -= value;
    }

    public AllReadableView(OpenContext context, IReadOnlyList<IModule> children)
    {
        InitializeComponent();
        _children = children;
        _content = new ChildContentHost(action =>
        {
            // UI에 도착한 통지는 다시 큐에 넣지 않는다. 중첩 셸 큐에 옛 자식 통지가 남지 않게 한다.
            if (DispatcherQueue.HasThreadAccess) action();
            else DispatcherQueue.TryEnqueue(() => action());
        });
        _content.StateChanged += UpdateBars;
        _content.ContentOpenFailed += () => RecordTransition(ContentTransitionStage.OpenFailed);
        Loaded += (_, _) =>
        {
            if (_content.Child is null) Focus(FocusState.Programmatic);
        };
        Unloaded += (_, _) =>
        {
            _initialPathProbe.Dispose();
            RecordTransition(ContentTransitionStage.Unloaded);
            DetachChild();
        };
        if (context.FilePath is { } path) OpenInitialPathAsync(path);
        UpdateBars();
    }

    public bool TryOpenFile(string path)
    {
        _initialPathProbe.Cancel();
        if (AllReadableRouting.ResolveChild(_children, path) is not { } module) return false;
        ShowChild(module, OpenContext.ForFile(path));
        return true;
    }

    private readonly KOTU.Core.Threading.LatestRequest _initialPathProbe = new();

    private async void OpenInitialPathAsync(string path)
    {
        var request = _initialPathProbe.Begin();
        try
        {
            // 통합 모듈의 단발 진입 확인. 실제 읽기와 재생은 자식 워커를 그대로 사용한다.
            var exists = await Task.Run(() => File.Exists(path), request);
            if (!request.IsCancellationRequested && exists) TryOpenFile(path);
        }
        catch (OperationCanceledException) { }
    }

    private void ShowChild(IModule module, OpenContext context)
    {
        DiagTrace.Write("allread", $"ShowChild {module.Id} path={context.FilePath ?? "(none)"}");
        _transitionGeneration++;
        ContentTransitionJournal.Shared.Record(_journalView, _transitionGeneration, module.Id, ContentTransitionStage.Begin);
        try
        {
            DetachChild();
            _childModuleId = module.Id;
            RecordTransition(ContentTransitionStage.CreateBegin);
            if (module.CreateView(context) is not UIElement view)
            {
                RecordTransition(ContentTransitionStage.OpenFailed);
                DiagTrace.Write("allread", $"ShowChild failed (no view) {module.Id}");
                return;
            }
            RecordTransition(ContentTransitionStage.Created);
            _content.Attach(view, context.FilePath);
            RecordTransition(ContentTransitionStage.ContractsAttached);
            ChildHost.Content = view;
            if (_bottomOverlay is { } overlay)
                (view as IBottomOverlayConsumer)?.SetBottomOverlay(overlay);
            RecordTransition(ContentTransitionStage.ViewAttached);
            ChildBarHost.Content = (view as IBottomBarProvider)?.TakeBottomBar() as UIElement;
            RecordTransition(ContentTransitionStage.BarAttached);
            UpdateBars();
            RecordTransition(ContentTransitionStage.Completed);
        }
        catch (Exception ex)
        {
            RecordTransition(ContentTransitionStage.Failed, ex);
            throw;
        }
    }

    private void DetachChild()
    {
        RecordTransition(ContentTransitionStage.DetachBegin);
        try
        {
            _content.Detach(() =>
            {
                RecordTransition(ContentTransitionStage.ContractsDetached);
                // 구독 무효화 다음 바, 센터 순으로 제거한다. 자식의 Unloaded가 실제 정리를 맡는다.
                ChildBarHost.Content = null;
                RecordTransition(ContentTransitionStage.BarRemoved);
                ChildHost.Content = null;
                RecordTransition(ContentTransitionStage.ViewRemoved);
            });
            UpdateBars();
            RecordTransition(ContentTransitionStage.Detached);
            _childModuleId = null;
        }
        catch (Exception ex)
        {
            RecordTransition(ContentTransitionStage.Failed, ex);
            throw;
        }
    }

    private void RecordTransition(ContentTransitionStage stage, Exception? error = null) =>
        ContentTransitionJournal.Shared.Record(_journalView, _transitionGeneration, _childModuleId, stage, error);

    public void SetBrowseOrder(string folder, IReadOnlyList<string> files) => _content.SetBrowseOrder(folder, files);
    public Guid? ActiveJobId => (_content.Child as IBackgroundJobOwner)?.ActiveJobId;
    public bool HasUnsavedChanges => _content.HasUnsavedChanges;
    public Task<bool> ConfirmCloseAsync() => _content.ConfirmCloseAsync();
    public void CaptureSettings() => (_content.Child as ISettingsSnapshotSource)?.CaptureSettings();
    public void OpenUntitled() => _content.OpenUntitled();
    public bool HasPlaybackSurface => _content.HasPlaybackSurface;
    public bool IsPlaying => _content.IsPlaying;
    public bool HasMediaTransport => _content.HasMediaTransport;
    public bool CanPrevious => _content.CanPrevious;
    public bool CanNext => _content.CanNext;
    public void Previous() => _content.Previous();
    public void Next() => _content.Next();
    public void Play() => _content.Play();
    public void Pause() => _content.Pause();
    public bool CanPrintNow => _content.CanPrintNow;
    public string PrintJobName => _content.PrintJobName;
    public int GetPrintPageCount(PrintPageSpec spec) => _content.GetPrintPageCount(spec);
    public Task<object?> CreatePrintPageAsync(int pageNumber, PrintPageSpec spec) =>
        _content.CreatePrintPageAsync(pageNumber, spec);
    public Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync() => _content.GetContentInfoAsync();
    public TrayStatus GetTrayStatus() => _content.GetTrayStatus();

    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(StatusBar);
        StatusBar.Background = null;
        StatusBar.Padding = new Thickness(0);
        return StatusBar;
    }

    public void AttachDriveStrip(object strip) => DriveStripHost.Content = strip as UIElement;
    public void ShowDriveStrip(bool show)
    {
        _driveStripShown = show;
        UpdateBars();
    }

    private void UpdateBars()
    {
        var hasChild = _content.Child is not null;
        ChildBarHost.Visibility = hasChild ? Visibility.Visible : Visibility.Collapsed;
        OwnBar.Visibility = hasChild ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderText.Visibility = hasChild ? Visibility.Collapsed : Visibility.Visible;
        var showStrip = !hasChild && _driveStripShown;
        DriveStripHost.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.Visibility = showStrip ? Visibility.Collapsed : Visibility.Visible;
        FileNameText.Text = _content.OpenedPath is { } path ? Path.GetFileName(path) : "No file open";
    }
}
