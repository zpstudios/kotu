using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.Module.AllReadable;

/// <summary>
/// All Readable 화면 (A59) — 파일 형식별 모듈 뷰를 <b>중첩 호스팅</b>한다.
/// 파일을 열면 확장자로 담당 모듈을 골라 자식 뷰를 만들고, <b>센터</b>(ChildHost)와
/// <b>하단 바</b>(ChildBarHost)만 그 뷰의 것으로 바꾼다. 셸에는 이 뷰가 계속 하나의 모듈로 보이므로
/// 창 제목·아이콘·좌우 오버레이·시작 메뉴는 All Readable 것으로 유지된다.
///
/// 셸과의 계약은 전부 <b>자식에게 위임</b>하는 형태다:
///  · <see cref="IContentStateSource"/> — 자식이 연 파일을 그대로 중계(오버레이·탐색기 동기화).
///  · <see cref="IContentInfoProvider"/> — 우측 정보 오버레이가 자식의 상세 정보를 그대로 받는다.
///  · <see cref="ICloseGuard"/> — 문서 자식의 미저장 가드(A37)가 셸까지 이어진다.
///  · <see cref="IBottomBarProvider"/>·<see cref="IDriveStripHost"/> — 하단 바 한 줄과 드라이브 줄(A22).
///  · <see cref="IFileOpenTarget"/> — 셸이 "이 파일 네가 열래?"를 먼저 물어보는 지점(A24 유지).
///
/// <b>워커 수명(A42)</b>: 자식 뷰의 워커·재생·구독은 자식이 <c>Unloaded</c>에서 스스로 정리한다.
/// 그래서 자식 교체는 "이전 자식을 비주얼 트리에서 떼는 것"이 곧 정리다 —
/// <see cref="DetachChild"/>가 하단 바 조각을 먼저 떼고(셸 하단 바 트리에 있으므로 자동으로
/// 사라지지 않는다) 그다음 센터를 비운다. 이 뷰가 내려갈 때(Unloaded)도 같은 정리를 한 번 더 한다.
/// </summary>
public sealed partial class AllReadableView : UserControl, IContentStateSource, IContentInfoProvider,
    IBottomBarProvider, IDriveStripHost, ICloseGuard, IFileOpenTarget
{
    /// <summary>자식 후보(파일 모듈만) — 모듈이 등록 시점에 추려 넘겨준다.</summary>
    private readonly IReadOnlyList<IModule> _children;

    /// <summary>자식들의 확장자 합집합 — 열기 대화상자 필터에 쓴다(오버레이 필터는 모듈이 노출).</summary>
    private readonly IReadOnlyList<string> _extensions;

    private UIElement? _childView;   // 지금 센터에 얹힌 자식 뷰 (null = 빈 상태)
    private string? _filePath;       // 지금 보고 있는 파일
    private bool _driveStripShown;   // 셸이 지정한 드라이브 줄 표시 여부 (A22)
    private bool _childDirty;        // 자식이 알린 마지막 미저장 상태 (A37 — 자식 교체 시 되돌리기용)

    /// <summary>자식이 파일을 열면(첫 로드·자식 내부 ◀/▶ 탐색 포함) 셸에 그대로 중계한다.</summary>
    public event Action<string>? ContentOpened;

    /// <summary>자식(문서 편집)의 미저장 상태 변화를 셸에 중계한다 — 창 제목 ● 표시(A37).</summary>
    public event Action<bool>? UnsavedChanged;

    public AllReadableView(OpenContext context, IReadOnlyList<IModule> children)
    {
        InitializeComponent();
        _children = children;
        _extensions = AllReadableRouting.UnionExtensions(children);

        // 빈 상태에서만 스스로 포커스를 잡는다 — 자식이 있으면 자식이 자기 Loaded에서 잡는다
        // (문서 편집기의 커서를 뺏으면 안 된다).
        Loaded += (_, _) =>
        {
            if (_childView is null) Focus(FocusState.Programmatic);
        };
        // A59 워커 수명: 이 뷰가 내려가면(모듈 전환·창 닫기) 자식도 반드시 함께 내린다.
        // 자식은 Unloaded에서 워커·libvlc·구독을 정리하므로, 트리에서 떼는 것이 곧 정리다.
        Unloaded += (_, _) => DetachChild();

        if (context.FilePath is { } path && File.Exists(path))
            TryOpenFile(path);
        UpdateBars();
    }

    // ---------- 셸 계약: 파일 열기 (A59 / A24) ----------

    /// <summary>
    /// 파일을 이 뷰 안에서 연다(<see cref="IFileOpenTarget"/>). 담당 자식 모듈이 없으면 false —
    /// 셸이 평소 라우팅으로 넘어가 그 파일의 전용 모듈로 연다.
    /// 미저장 가드(A37)는 셸이 이 호출 전에 이미 통과시킨다.
    /// </summary>
    public bool TryOpenFile(string path)
    {
        if (AllReadableRouting.ResolveChild(_children, path) is not { } module) return false;
        ShowChild(module, OpenContext.ForFile(path));
        return true;
    }

    /// <summary>자식 뷰를 만들어 센터와 하단 바에 얹는다. 이전 자식은 먼저 완전히 정리한다.</summary>
    private void ShowChild(IModule module, OpenContext context)
    {
        DetachChild();

        if (module.CreateView(context) is not UIElement view) return; // 계약 위반 방어 — 빈 상태 유지
        _childView = view;
        _filePath = context.FilePath;

        ChildHost.Content = view;
        // 셸이 모듈 뷰에 하는 것과 같은 배선(모듈 전환 로직의 재사용 가능한 부분) —
        // 다만 셸에만 있는 것(창 제목·아이콘·인스턴스 상태·오버레이)은 셸이 계속 담당한다.
        ChildBarHost.Content = (view as IBottomBarProvider)?.TakeBottomBar() as UIElement;
        if (view is IContentStateSource source) source.ContentOpened += OnChildContentOpened;
        if (view is ICloseGuard guard) guard.UnsavedChanged += OnChildUnsavedChanged;
        UpdateBars();
    }

    /// <summary>
    /// 지금 자식을 떼고 정리한다. 순서가 중요하다:
    /// ① 이벤트 구독 해제 → ② 하단 바 조각 제거(셸 하단 바 트리에 있어 센터를 비워도 남는다)
    /// → ③ 센터 비우기(자식 Unloaded → 워커·재생·구독 정리) → ④ 미저장 표시 되돌리기.
    /// 자식이 없어도 안전하다(멱등) — Unloaded와 교체 경로 양쪽에서 불린다.
    /// </summary>
    private void DetachChild()
    {
        if (_childView is IContentStateSource source) source.ContentOpened -= OnChildContentOpened;
        if (_childView is ICloseGuard guard) guard.UnsavedChanged -= OnChildUnsavedChanged;
        ChildBarHost.Content = null;
        ChildHost.Content = null;
        _childView = null;

        if (_childDirty)
        {
            // 자식이 사라졌으니 창 제목의 ●도 함께 내려야 한다(셸은 지금 뷰의 상태만 본다).
            _childDirty = false;
            UnsavedChanged?.Invoke(false);
        }
    }

    /// <summary>자식이 파일을 열었다(첫 로드·자식 내부 탐색). 셸에 중계해 오버레이·탐색기를 맞춘다.</summary>
    private void OnChildContentOpened(string path)
    {
        // 계약상 UI 스레드 보장이 없다 — 표시를 만지므로 디스패치한다.
        if (DispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => OnChildContentOpened(path));
            return;
        }
        _filePath = path;
        UpdateBars();
        ContentOpened?.Invoke(path);
    }

    /// <summary>자식의 미저장 상태 변화를 그대로 셸로 올린다(A37).</summary>
    private void OnChildUnsavedChanged(bool dirty)
    {
        _childDirty = dirty;
        UnsavedChanged?.Invoke(dirty);
    }

    // ---------- 셸 계약: 하단 바 · 드라이브 줄 ----------

    /// <summary>하단 바 한 줄을 뷰에서 떼어 셸 하단 바에 얹는다(v0.21.0 통합 방식, 전 모듈 공통).</summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(StatusBar);
        StatusBar.Background = null; // 셸 바가 배경·여백을 제공한다
        StatusBar.Padding = new Thickness(0, 2, 0, 2);
        return StatusBar;
    }

    /// <summary>
    /// A22: 셸이 만든 공용 드라이브 줄을 자체 바 슬롯에 끼운다.
    /// 자식에게 넘기지 않는다 — 드라이브 줄은 "파일이 열려 있지 않을 때"만 보이는데
    /// 그때는 자식이 없어 자체 바가 떠 있기 때문이다.
    /// </summary>
    public void AttachDriveStrip(object strip) => DriveStripHost.Content = strip as UIElement;

    /// <summary>드라이브 줄과 파일명은 같은 칸을 나눠 쓴다 — 줄이 뜨는 동안에는 비켜준다(A22).</summary>
    public void ShowDriveStrip(bool show)
    {
        _driveStripShown = show;
        UpdateBars();
    }

    /// <summary>자식 유무에 따라 하단 바 두 줄(자식 바 / 자체 바)과 센터 안내 문구를 맞바꾼다.</summary>
    private void UpdateBars()
    {
        var hasChild = _childView is not null;
        ChildBarHost.Visibility = hasChild ? Visibility.Visible : Visibility.Collapsed;
        OwnBar.Visibility = hasChild ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderText.Visibility = hasChild ? Visibility.Collapsed : Visibility.Visible;

        var showStrip = !hasChild && _driveStripShown;
        DriveStripHost.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.Visibility = showStrip ? Visibility.Collapsed : Visibility.Visible;
        FileNameText.Text = _filePath is { } path ? Path.GetFileName(path) : "No file open";
    }

    // ---------- 셸 계약: 우측 정보 오버레이 ----------

    /// <summary>
    /// 우측 정보 오버레이 내용은 지금 자식의 것을 그대로 쓴다(영상=미디어 정보, 사진=EXIF …).
    /// 자식이 없거나 정보를 내놓지 않으면 null — 셸이 파일 기본 정보로 대신한다.
    /// </summary>
    public async Task<string?> GetContentInfoAsync() =>
        _childView is IContentInfoProvider provider ? await provider.GetContentInfoAsync() : null;

    // ---------- 셸 계약: 미저장 가드 (A37) ----------

    public bool HasUnsavedChanges => _childView is ICloseGuard { HasUnsavedChanges: true };

    public Task<bool> ConfirmCloseAsync() =>
        _childView is ICloseGuard guard ? guard.ConfirmCloseAsync() : Task.FromResult(true);

    // ---------- 자체 바 동작 (빈 상태 전용) ----------

    private void OnOpenButtonClick(object sender, RoutedEventArgs e) => _ = PickAndOpenAsync();

    /// <summary>열기 대화상자 — 필터는 자식 모듈 전체의 확장자 합집합.</summary>
    private async Task PickAndOpenAsync()
    {
        // Window 객체 없이 파일 선택기를 띄우려면 XamlRoot 경유로 HWND를 얻어야 한다(전 모듈 공통).
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;
        var hwnd = Win32Interop.GetWindowFromWindowId(environment.AppWindowId);

        // 시작 위치는 문서 라이브러리 — 형식이 섞여 있어 특정 라이브러리를 고를 근거가 없다
        // (문서·압축 모듈이 이미 쓰는 값). 실제 시작 폴더 개념은 셸의 lastFolder가 담당한다.
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in _extensions)
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, hwnd);

        if (await picker.PickSingleFileAsync() is { } file)
            TryOpenFile(file.Path);
    }

    private void OnFullScreenButtonClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnFullScreenInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullScreen();
    }

    /// <summary>Escape는 전체화면일 때만 소비한다 — 아니면 흘려보낸다(자식·셸이 쓸 수 있게).</summary>
    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsFullScreen())
        {
            args.Handled = false;
            return;
        }
        args.Handled = true;
        ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;

        var appWindow = AppWindow.GetFromWindowId(environment.AppWindowId);
        appWindow.SetPresenter(appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
    }

    private bool IsFullScreen()
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return false;
        return AppWindow.GetFromWindowId(environment.AppWindowId).Presenter.Kind
            == AppWindowPresenterKind.FullScreen;
    }
}
