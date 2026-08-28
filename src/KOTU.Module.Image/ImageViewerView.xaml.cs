using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;
using KOTU.Core.Threading;
using KOTU.Input;

namespace KOTU.Module.Image;

/// <summary>
/// 이미지 뷰어 화면. 폴더 내 ←/→ 탐색, 줌/팬(A148 마우스 드래그), 회전(R), 휴지통 삭제(Delete),
/// 전체화면 더블클릭 토글, 하단 상태바를 제공한다(F11·⛶ 버튼은 A151에서 셸 모드 체계로 이관).
/// 폴더 스캔·파일 읽기·디코드(WIC 메타데이터/Magick)는 뷰 전용 워커(A42)에서 직렬로 돌고
/// UI 스레드는 비트맵 표시만 한다 — 빠른 ←/→ 연타의 낡은 결과는 적용 직전의 현재 파일
/// 재검증이 버린다(A194 — 이웃 선읽기 캐시 히트는 대기 없이 완료돼 직렬 순서만으로는 부족).
/// A194: 표시 완료 후 양옆 이웃 각 1장을 같은 워커로 선읽기해 항해 체감 지연을 줄인다.
/// A211 배치 2(v0.221.0): 인쇄 공급자(<see cref="IPrintPageProvider"/>) — 보고 있는 사진 1장을
/// 인쇄 가능 영역 안 contain으로 담은 1페이지를 셸 PrintHost에 넘긴다(Ctrl+P·하단 바 버튼).
/// </summary>
public sealed partial class ImageViewerView : UserControl, IContentStateSource, IContentInfoProvider,
    IBottomBarProvider, IDriveStripHost, ITrayStatusProvider, IPrintPageProvider
{
    /// <summary>트레이 아이콘 표시 값이 바뀌었다(A54) — 파일 열기/전환/실패, 회전(A191) 시점.</summary>
    public event Action? TrayStatusChanged;

    /// <summary>
    /// 트레이 아이콘 내용: 열림 = <b>해상도 2줄</b>(위 = 가로 px, 아래 = 세로 px — 예 "4032"/"3024"),
    /// 유휴 = "IMG".
    /// <para>
    /// A191(A54 원안 교체): 원안의 확장자 · 용량은 A137 ②가 작업표시줄 32px 아이콘에 같은 값을
    /// 그리게 되면서 중복이 됐다. 32px 값은 셸이 경로에서 직접 계산하므로(MainWindow.OpenFileIconInfo)
    /// 이 메서드를 바꿔도 그쪽은 종전 그대로다 — 중복만 사라진다.
    /// </para>
    /// 값은 <b>지금 화면에 보이는 축</b> 기준이다: EXIF 회전과 R 키 누적 회전이 90°/270°면
    /// 가로·세로를 맞바꾼다(<see cref="RotationSwapsAxes"/> — 레이아웃 판정과 같은 축).
    /// 아직 해상도를 못 구했으면(로드 실패·메타데이터 미지원) 그 줄이 "—"가 된다.
    /// </summary>
    public TrayStatus GetTrayStatus()
    {
        if (_navigator?.Current is null) return TrayStatus.Idle("IMG");
        var (width, height) = RotationSwapsAxes
            ? (_pixelHeight, _pixelWidth)
            : (_pixelWidth, _pixelHeight);
        return TrayStatus.Open(TrayFormat.Pixels(width), TrayFormat.Pixels(height));
    }

    /// <summary>
    /// 전체화면은 더블클릭 토글(뷰 고유)과 셸 모드 체계(A151 — F11·⛶ 버튼 대체)가 담당한다.
    /// 상태바를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(v0.27.0 — 동영상 v0.21.0과 같은 통합).
    /// 셸 바가 배경·여백을 제공하므로 자체 배경과 패딩은 걷어낸다. 컨트롤 필드 참조는 유효 유지.
    /// </summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(StatusBar);
        StatusBar.Background = null;
        StatusBar.Padding = new Thickness(0, 2, 0, 2);
        return StatusBar;
    }

    /// <summary>
    /// A22(v0.108.0): 셸이 만든 공용 드라이브 줄을 하단 바 슬롯에 끼운다.
    /// v0.47.0의 모듈별 드라이브 텍스트(DriveInfoText)를 대체한다.
    /// </summary>
    public void AttachDriveStrip(object strip) => DriveStripHost.Content = strip as UIElement;

    /// <summary>
    /// 드라이브 줄과 파일명·메타는 같은 칸을 나눠 쓴다 — 줄이 뜨는 동안(파일 없음)에는 비켜준다.
    /// </summary>
    public void ShowDriveStrip(bool show)
    {
        DriveStripHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FileInfoPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>이미지를 열거나 ←/→로 바꿀 때 셸에 알린다(v0.25.0 — 탐색기·오버레이 동기화).</summary>
    public event Action<string>? ContentOpened;

    private ImageFolderNavigator? _navigator;
    private int _openSeq;        // OpenPath 경쟁 방지: 느린 폴더 스캔이 최신 열기를 덮지 않게
    private int _userRotation;   // R 키 누적 회전 (0/90/180/270)
    private int _exifRotation;   // EXIF orientation에서 읽은 회전
    private uint _pixelWidth;
    private uint _pixelHeight;
    // A149: A9의 미리 조립된 한 덩어리(_metaText)를 조각으로 쪼갰다 — 표시 순서(해상도·확장자·용량·
    // 순번·EXIF)가 조립 순서와 달라졌고, 순번은 탐색 때마다 바뀌어 조립 시점에 확정할 수 없다.
    private string _sizeText = string.Empty; // 용량 (예: "2.41 MB")
    private string _kindText = string.Empty; // 종류 = 확장자 + 비트뎁스 (예: "JPG 24-bit")
    private string _exifText = string.Empty; // A9 EXIF 요약 — 순번 뒤에 이어 붙인다(정보 손실 금지)
    private ModuleWorker? _worker; // 폴더 스캔·파일 읽기·디코드 전용(A42) — 뷰별 분리

    /// <summary>
    /// A211 배치 2: 인쇄 페이지의 비트맵 재료 = <b>지금 표시 중인 이미지의 인코딩 바이트</b>
    /// (일반 경로는 원본 파일 바이트, psd는 Magick이 만든 PNG 바이트). 표시 상태와 한 몸이라
    /// <c>ImageControl.Source</c>와 <b>같은 자리에서 함께 세우고 함께 비운다</b>.
    /// <para>
    /// 표시용 <c>BitmapImage</c>를 인쇄 페이지의 Image에 그대로 물리지 않는 이유(소스 공유 판정):
    /// ① 저장소에 <b>하나의 ImageSource를 두 Image가 나눠 쓰는 선례가 0건</b>이다 — BitmapImage
    ///    생성 지점 전수(PdfPane·ThumbnailExplorer·ExplorerPane·BrandAssets·BrandSpinner·
    ///    SponsorAds·이 파일)가 소비처마다 새로 만든다. CLAUDE.md §3 "저장소 안에 실제로 쓰이고
    ///    있는 형태만 복제한다"에 걸린다. ② WinUI DependencyObject 공유 실패는 <b>런타임에만</b>
    ///    드러난다(v0.174.1 실사례 — 공유 Geometry를 PathIcon.Data에 걸어 앱이 죽었고 CI는 컴파일만
    ///    하므로 못 잡았다). 인쇄 경로는 실기기에서만 검증되므로 같은 종류의 도박을 하지 않는다.
    /// </para>
    /// <para>
    /// 인쇄 시점에 파일을 다시 읽지 않는 이유: psd(Magick 경로)는 원본 바이트를 WIC가 못 읽어
    /// 재현이 안 되고, 보던 파일이 그새 지워졌을 수도 있다. 메모리는 인코딩 바이트 1장분 —
    /// A194 선읽기 캐시가 이미 같은 성질로 2장분을 허용한 예산 안이다.
    /// </para>
    /// </summary>
    private byte[]? _printBytes;

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU image worker");

    /// <summary>
    /// 이웃 선읽기 캐시 (A194): 경로 → ReadImageFile 결과의 Task. <b>현재 파일의 양옆 각 1장,
    /// 최대 2건</b>만 담고 양옆을 벗어나면 즉시 버린다(PrunePreloadCache) — 메모리는 인코딩
    /// 원본 바이트 그대로라 대형 이미지 2장 분량까지 허용(등재 확정). Task를 담는 이유:
    /// 선읽기가 아직 워커 큐에 있을 때 항해가 오면 표시 경로가 <b>같은 Task를 await</b>해
    /// 중복 디코드 없이 결과를 받는다(직렬 큐라 표시보다 먼저 완료된다). 표시는 항상
    /// LoadCurrentAsync가 캐시를 조회하는 단방향 — 선읽기 완료가 화면을 직접 만지지 않는다.
    /// 모든 접근은 UI 스레드에서만(항해·선읽기 후속부 전부 UI 문맥 — 경쟁 없음).
    /// </summary>
    private readonly Dictionary<string, Task<(byte[] Data, uint Width, uint Height, int ExifRotation,
        string Size, string Kind, string Exif)>> _preloadCache = new(StringComparer.OrdinalIgnoreCase);

    public ImageViewerView(OpenContext context)
    {
        InitializeComponent();
        SetupHotkeys(); // A34: 하단 바 버튼 핫키 + 툴팁 표기

        // 키 입력을 받기 위해 로드 시 포커스 확보 (IsTabStop은 XAML에서 설정)
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        Unloaded += (_, _) =>
        {
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
            _preloadCache.Clear(); // A194 — 뷰 언로드 = 선읽기 캐시 무효화(대형 바이트를 붙들지 않는다)
            _printBytes = null;    // A211 — 인쇄 재료도 같은 이유로 놓는다(뷰가 내려가면 인쇄 대상도 없다)
        };

        // A98: 휠 = 줌(사진 특례 — Ctrl 없이 휠 단독으로도, Ctrl+휠도 동일). 내장 Ctrl+휠 줌은
        // ScrollViewer 자신이 처리하므로 그보다 먼저(버블 경로상 앞서는 콘텐츠 프레젠터에서)
        // 가로채야 막고 대체할 수 있다 — 프레젠터는 템플릿 적용 후에야 존재하므로 Loaded에서 배선한다.
        // 구 휠 이전/다음 탐색(v0.41.0)은 휠 단독=줌이 대체 — 탐색은 ←/→ 키·하단 바 버튼으로 유지.
        Scroller.Loaded += (_, _) => HookZoomWheel();

        // A149: 줌 레벨 표시(ZoomText)의 단일 출처는 Scroller.ZoomFactor다 — 휠 줌·Fit·핀치·
        // ChangeView 어느 경로로 바뀌든 여기서 한 번에 받는다(모듈 최초의 ViewChanged 구독).
        // 팬(A148)은 매 프레임 ChangeView를 걸어 이 이벤트가 폭주하므로 UpdateZoomText가
        // "직전 표시 문자열과 같으면 조기 반환"으로 과다 대입을 막는다.
        Scroller.ViewChanged += OnScrollerViewChanged;

        // A161(v0.174.0): 이미지 표면 우클릭 메뉴("Set as desktop background") — 메뉴를 코드에서
        // 만들어 거는 것은 저장소 관례다(ExplorerPane.MakeSurfaceMenu·ThumbnailExplorer 동일).
        Scroller.ContextFlyout = MakeSurfaceMenu();

        if (context.FilePath is { } path && File.Exists(path))
        {
            OpenPath(path);
        }
        else
        {
            PlaceholderText.Visibility = Visibility.Visible;
            UpdateStatusBar();
        }
    }

    // ---------- 파일 열기 (버튼/드래그&드롭/초기 컨텍스트) ----------

    private async void OpenPath(string path)
    {
        // 폴더 스캔 + 자연 정렬은 대형 폴더·네트워크 드라이브에서 느릴 수 있다 — 워커에서.
        var seq = ++_openSeq;
        ImageFolderNavigator navigator;
        try
        {
            navigator = await Worker.Run(_ => ImageFolderNavigator.Create(path));
        }
        catch (OperationCanceledException)
        {
            return; // 뷰가 내려가며 워커가 닫힘
        }
        catch (Exception ex)
        {
            PlaceholderText.Text = "Cannot read the folder: " + ex.Message;
            PlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        if (seq != _openSeq) return; // 그새 다른 파일이 열렸다 — 이 결과는 버린다.
        _navigator = navigator;
        _preloadCache.Clear(); // A194 — 파일·폴더 전환 = 옛 목록 기준의 선읽기 캐시 무효화
        PlaceholderText.Visibility = Visibility.Collapsed;
        _ = LoadCurrentAsync();
    }

    // A99: 모듈 열기 버튼·O 키·파일 대화상자(PickAndOpenAsync)는 제거 — 파일 열기는
    // 셸 S4 'Open file'(A90)로 일원화됐다.
    // 드래그&드롭은 종전대로 창 수준(MainWindow)에서 확장자 라우팅으로 일괄 처리한다.

    // ---------- 휠 줌 (A98 — 휠 단독·Ctrl+휠, A84 Shift+휠 대체) ----------

    private ScrollContentPresenter? _zoomPresenter; // 휠·팬 가로채기 지점 (Scroller 템플릿 로드 후 탐색)

    /// <summary>
    /// ScrollViewer 콘텐츠 프레젠터에 휠 핸들러를 단다. 버블 순서상 프레젠터가
    /// ScrollViewer보다 먼저 이벤트를 받으므로 여기서 Handled 처리하면 내장 Ctrl+휠 줌 대신
    /// 우리 수동 줌(노치당 10%)이 일관되게 적용된다. 핀치 줌은 ZoomMode=Enabled 그대로 유지된다.
    /// 뷰어 콘텐츠 위에서만 동작한다는 규칙(리스트/그리드 무동작 — A84에서 확립,
    /// A98도 유지)은 배선 지점 자체가 보장한다.
    /// A148: 드래그 팬도 같은 요소에 건다 — 배선 지점이 같아야 "콘텐츠 위에서만" 규칙이
    /// 두 제스처에 똑같이 적용되고, 캡처 대상과 좌표 기준이 흩어지지 않는다.
    /// </summary>
    private void HookZoomWheel()
    {
        if (_zoomPresenter is not null) return; // Loaded 재진입(전체화면 왕복 등) 중복 배선 방지
        _zoomPresenter = FindPresenter(Scroller);
        if (_zoomPresenter is null) return;
        _zoomPresenter.PointerWheelChanged += OnContentWheel;
        _zoomPresenter.PointerPressed += OnContentPointerPressed;
        _zoomPresenter.PointerMoved += OnContentPointerMoved;
        // 놓기와 캡처 상실은 같은 종료 처리로 모은다(VideoPlayerView.xaml.cs:237-240 선례) —
        // 창 비활성화·다른 요소의 캡처 탈취 등으로 Released가 아예 안 오는 경로가 있다.
        _zoomPresenter.PointerReleased += OnContentPointerReleased;
        _zoomPresenter.PointerCaptureLost += OnContentPointerReleased;
    }

    /// <summary>
    /// 뷰어 콘텐츠 위 휠(A98): 휠 단독·Ctrl+휠 = 줌(사진 특례 — Ctrl 없이도 줌).
    /// Shift+휠 줌(A84)은 폐기 — Shift는 기본 처리에 양보한다.
    /// </summary>
    private void OnContentWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)) return;
        e.Handled = true; // ScrollViewer 기본 처리(스크롤·내장 Ctrl+휠 줌)보다 먼저 소비
        ZoomAtPointer(e);
    }

    /// <summary>포인터 위치를 고정점으로 노치당 10% 줌. 범위는 XAML MinZoomFactor 0.1~MaxZoomFactor 8 그대로.</summary>
    private void ZoomAtPointer(PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Scroller).Properties.MouseWheelDelta;
        if (delta == 0) return;
        var oldZoom = Scroller.ZoomFactor;
        var newZoom = (float)Math.Clamp(oldZoom * Math.Pow(1.1, delta / 120.0),
            Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        if (Math.Abs(newZoom - oldZoom) < 0.0001f) return;

        // 포인터 아래 콘텐츠 지점이 화면에서 움직이지 않게 오프셋을 배율 변화만큼 이동
        var pt = e.GetCurrentPoint(Scroller).Position;
        var ratio = newZoom / oldZoom;
        Scroller.ChangeView(
            (Scroller.HorizontalOffset + pt.X) * ratio - pt.X,
            (Scroller.VerticalOffset + pt.Y) * ratio - pt.Y,
            newZoom, disableAnimation: true);
    }

    /// <summary>Scroller 템플릿에서 콘텐츠 프레젠터를 찾는다(휠 가로채기 지점).</summary>
    private static ScrollContentPresenter? FindPresenter(DependencyObject root)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollContentPresenter presenter) return presenter;
            if (FindPresenter(child) is { } nested) return nested;
        }
        return null;
    }

    // ---------- 드래그 투 스크롤 / 팬 (A148) ----------

    /// <summary>
    /// 이만큼(px) 넘게 움직이기 전에는 "클릭"으로 본다 — 더블클릭 전체화면(OnDoubleTapped)을
    /// 손떨림으로 잃지 않게 하는 임계. 넘는 순간부터 팬이 활성화된다.
    /// </summary>
    private const double PanThresholdPixels = 4;

    /// <summary>
    /// 팬이 끝난 뒤 이 시간(ms) 안에 오는 더블탭은 삼킨다. 팬 중에는 Pressed/Released를
    /// Handled 처리하지 않으므로(셸 경로 보존) 제스처 인식기가 더블탭을 만들어 낼 수 있는데,
    /// 밀어서 보다가 손을 뗀 직후의 전체화면 전환은 사고에 가깝다. 값은 A131 더블클릭 창(500ms)
    /// 보다 약간 짧게 잡아 "드래그 후 곧바로 의도한 더블클릭"까지 막지는 않는다.
    /// </summary>
    private const int PanDoubleTapSuppressMs = 400;

    private bool _panTracking;  // 좌버튼 눌림으로 캡처를 잡았다(임계 미만이면 아직 클릭 취급)
    private bool _panActive;    // 임계를 넘겨 실제로 팬 중이다
    private double _panOriginX; // 눌림 시점 포인터 좌표(Scroller 기준)
    private double _panOriginY;
    private double _panStartHorizontal; // 눌림 시점 오프셋 — 이동량은 여기에 더해 계산한다
    private double _panStartVertical;
    private DateTime _panEndedAt = DateTime.MinValue; // 실제 팬이 끝난 시각(억제 창 기준, A131 관용구)

    /// <summary>
    /// 좌버튼 드래그로 확대된 이미지를 밀어서 본다(A148). 마우스 전용 —
    /// 터치·펜은 ScrollViewer 내장 패닝이 이미 처리하므로 뺏지 않는다.
    /// 스크롤 여지가 없으면(축소·창맞춤 상태) 캡처조차 하지 않고 빠져 더블클릭 전체화면 경로를
    /// 원형 그대로 남긴다. Handled는 세우지 않는다 — 셸의 마우스 뒤로가기(A112)·홀드 취소(A58)·
    /// 경계 버튼 이동(A86)이 handledEventsToo로 살아 있긴 하지만, 더블탭 제스처 인식은
    /// Pressed/Released를 보므로 여기서 소비하면 예측이 어려워진다.
    /// <para>
    /// 커서를 grab/grabbing으로 바꾸는 것은 이번 배치에서 하지 않는다(후속 등재 후보):
    /// ProtectedCursor가 protected라 컨트롤 상속이 필요하고(MainWindow.xaml.cs:1013-1014 주석 —
    /// SponsorCard가 그래서 Grid를 상속한다), 저장소에 grab 계열 InputSystemCursorShape 선례가
    /// 없다. 여기서는 프레젠터가 ScrollViewer 템플릿 요소라 상속으로 감쌀 수도 없다.
    /// </para>
    /// </summary>
    private void OnContentPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_zoomPresenter is null) return;
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        // 왼쪽 눌림 "전이"만 태운다 — A112(MainWindow.xaml.cs:1880-1881)·A131(ExplorerPane.xaml.cs:1047-1048)과 같은 관용구.
        var point = e.GetCurrentPoint(Scroller);
        if (point.Properties.PointerUpdateKind
            != Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed) return;
        // 밀 여지가 없으면 무동작(등재문 ⓒ) — 창맞춤·축소 상태에서 클릭이 팬으로 오인되지 않는다.
        if (Scroller.ScrollableWidth <= 0 && Scroller.ScrollableHeight <= 0) return;
        if (!_zoomPresenter.CapturePointer(e.Pointer)) return; // 캡처 실패 = 상태를 만들지 않는다

        _panTracking = true;
        _panActive = false;
        _panOriginX = point.Position.X;
        _panOriginY = point.Position.Y;
        _panStartHorizontal = Scroller.HorizontalOffset;
        _panStartVertical = Scroller.VerticalOffset;
    }

    /// <summary>
    /// 콘텐츠가 손을 따라오게 오프셋을 반대로 민다. 임계를 넘긴 뒤에만 Handled를 세운다 —
    /// 그 전엔 아직 "클릭"이라 아무 것도 소비하지 않는다.
    /// 좌표 기준은 Scroller(뷰포트) — 팬 중에도 위치가 변하지 않는 요소여야 이동량이 누적되지 않는다.
    /// </summary>
    private void OnContentPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panTracking) return;
        var position = e.GetCurrentPoint(Scroller).Position;
        var dx = position.X - _panOriginX;
        var dy = position.Y - _panOriginY;
        if (!_panActive)
        {
            // 맨해튼 거리 — 임계 판정에 제곱근을 쓸 이유가 없다(4px는 손떨림 상한일 뿐).
            if (Math.Abs(dx) + Math.Abs(dy) <= PanThresholdPixels) return;
            _panActive = true;
        }

        e.Handled = true;
        // 배율은 null = 무변경. 애니메이션을 끄지 않으면 이동이 손보다 늦게 따라와 미끄러진다.
        Scroller.ChangeView(_panStartHorizontal - dx, _panStartVertical - dy, null,
            disableAnimation: true);
    }

    /// <summary>
    /// 놓기·캡처 상실 공용 종료. 실제로 팬이 일어났을 때만 더블탭 억제 창을 켠다 —
    /// 임계 미만의 단순 클릭은 종전대로 더블클릭 전체화면이 그대로 성립해야 한다.
    /// Handled는 세우지 않는다(셸 경로 보존).
    /// </summary>
    private void OnContentPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panTracking) return;
        if (_panActive) _panEndedAt = DateTime.UtcNow;
        _panTracking = false;
        _panActive = false;
        _panOriginX = 0;
        _panOriginY = 0;
        _panStartHorizontal = 0;
        _panStartVertical = 0;
        // 캡처가 이미 풀린 뒤(PointerCaptureLost)라면 무동작이다 — 두 경로를 나눌 필요가 없다.
        _zoomPresenter?.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>방금 팬으로 끝난 제스처인가 — 시간 비교는 A131(ExplorerPane.xaml.cs:1060-1062) 관용구 그대로.</summary>
    private bool IsPanSuppressingDoubleTap() =>
        (DateTime.UtcNow - _panEndedAt).TotalMilliseconds < PanDoubleTapSuppressMs;

    // ---------- 표면 우클릭 메뉴 / 바탕화면 배경 지정 (A161) ----------

    /// <summary>
    /// 이미지 표면 우클릭 메뉴. 항목은 하나뿐이고, 파일이 열려 있을 때만 활성이다 —
    /// 활성 판정을 Opening 한 곳에 모으는 것은 ExplorerPane·ThumbnailExplorer의 MakeSurfaceMenu 관용구다.
    /// <para>
    /// 배선 지점이 <c>Scroller</c>인 이유: 휠 줌·팬(A148)을 건 콘텐츠 프레젠터에는
    /// ContextFlyout이 없어 컨텍스트 요청이 여기까지 올라온다 = "뷰어 콘텐츠 표면 위에서만"
    /// 규칙이 그대로 성립한다(플레이스홀더 텍스트는 형제 요소라 조상이 아니다 = 파일 없을 때
    /// 그 위 우클릭은 메뉴 자체가 뜨지 않는다 — 등재문 ⓑ의 두 허용 동작 중 하나).
    /// </para>
    /// <para>
    /// A148 팬과 충돌하지 않는다: 팬은 <b>좌버튼 눌림 전이</b>만 태우므로(OnContentPointerPressed의
    /// PointerUpdateKind 검사) 캡처 중에 우버튼 눌림이 프레젠터로 와도 그냥 흘러간다. 더블탭
    /// 전체화면(OnDoubleTapped)도 좌버튼 경로라 무관하고, 셸의 마우스 뒤로가기(A112)는 XButton1이라 겹치지 않는다.
    /// </para>
    /// </summary>
    private MenuFlyout MakeSurfaceMenu()
    {
        var setWallpaper = new MenuFlyoutItem
        {
            Text = "Set as desktop background",
            Icon = new FontIcon { Glyph = "\uE8B9" }, // Picture — ImageModule.IconGlyph와 같은 값
        };
        setWallpaper.Click += async (_, _) => await SetAsWallpaperAsync();

        var flyout = new MenuFlyout();
        flyout.Items.Add(setWallpaper);
        // 훅 배선 여부는 보지 않는다(A124 선례와 같다): 셸이 첫 창보다 먼저 배선하므로 미배선은
        // 이론상 도달 불가이고, 만약 비어 있어도 TrySet이 false를 돌려 실패 문구로 접힌다.
        flyout.Opening += (_, _) => setWallpaper.IsEnabled = _navigator?.Current is not null;
        return flyout;
    }

    /// <summary>
    /// 현재 이미지를 바탕화면 배경으로 건다(A161).
    /// <para>
    /// 변환은 <b>항상 PNG</b>다 — SPI_SETDESKWALLPAPER가 못 읽는 형식(psd·webp·ico 등)이 있어
    /// 원본을 그대로 넘길 수 없다. 변환기는 이 모듈이 이미 쓰는 Magick.NET(LoadViaMagickAsync와
    /// 같은 MagickFormat.Png)이라 형식을 가리지 않는다.
    /// </para>
    /// <para>
    /// 화면에서 보고 있는 <b>회전(R 키)·EXIF 회전·줌은 반영하지 않는다 — 원본 그대로</b> 건다
    /// (A161 확정). 그 값들은 뷰의 표시 변환일 뿐 파일 내용이 아니기 때문이다.
    /// </para>
    /// 변환·파일 쓰기는 뷰 전용 워커(A42)에서 돌고, 셸 훅이 하는 레지스트리 쓰기·P/Invoke도
    /// 같은 워커에서 이어진다 — UI 스레드는 결과 문구만 대입받는다.
    /// 안내는 이 모듈의 기존 관용구인 하단 바 파일명 칸(DeleteCurrentAsync의 실패 표기와 같다).
    /// 다음 열기·탐색에서 UpdateStatusBar가 파일명으로 되돌린다.
    /// </summary>
    private async Task SetAsWallpaperAsync()
    {
        if (_navigator?.Current is not { } path) return;

        bool applied;
        try
        {
            applied = await Worker.Run(_ =>
                KOTU.Core.Integration.DesktopWallpaperHook.TrySet(WriteWallpaperPng(path)));
        }
        catch (OperationCanceledException)
        {
            return; // 뷰가 내려가며 워커가 닫혔다 — 안내할 화면도 없다
        }
        catch (Exception ex)
        {
            // 훅은 던지지 않으므로 여기 오는 것은 변환·파일 쓰기 실패뿐이다(손상 파일·디스크·권한).
            FileNameText.Text =
                $"Failed to set as desktop background: {Path.GetFileName(path)} ({ex.Message})";
            return;
        }

        FileNameText.Text = applied
            ? $"Set as desktop background: {Path.GetFileName(path)}"
            : $"Failed to set as desktop background: {Path.GetFileName(path)}";
    }

    /// <summary>
    /// 워커 스레드: 원본을 PNG로 변환해 <c>%AppData%\KOTU\wallpaper.png</c>에 덮어쓰고 그 경로를 돌려준다.
    /// 파일명이 고정이라 누적되지 않는다 — Windows가 지정 시점에 자체 캐시로 복사해 두므로
    /// 다음 지정에서 이 파일을 다시 써도 현재 배경이 깨지지 않는다.
    /// 경로 조립은 설정 파일(KOTU.Core.Settings.JsonSettingsService.DefaultPath)과 같은 관용구이고,
    /// 폴더명은 <b>현재 브랜드 "KOTU"만</b> 쓴다(구 브랜드 폴더는 청소 대상일 뿐 쓰기 대상이 아니다).
    /// 바이트 변환은 LoadViaMagickAsync가 이미 쓰는 ToByteArray(MagickFormat.Png) 그대로다.
    /// </summary>
    private static string WriteWallpaperPng(string sourcePath)
    {
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KOTU", "wallpaper.png");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var magick = new ImageMagick.MagickImage(sourcePath);
        File.WriteAllBytes(target, magick.ToByteArray(ImageMagick.MagickFormat.Png));
        return target;
    }

    // ---------- 이미지 로드 ----------

    private async Task LoadCurrentAsync()
    {
        var path = _navigator?.Current;
        if (path is null)
        {
            ImageControl.Source = null;
            _printBytes = null; // A211 — 표시가 비면 인쇄 재료도 비운다(CanPrintNow false)
            PlaceholderText.Visibility = Visibility.Visible;
            UpdateStatusBar();
            TrayStatusChanged?.Invoke(); // A54: 볼 파일이 없어졌다 → 유휴("IMG")로
            return;
        }

        try
        {
            if (NeedsMagickDecode(path))
            {
                await LoadViaMagickAsync(path);
                return;
            }

            // 파일 읽기와 메타데이터 디코드(해상도·EXIF orientation·A9 메타 요약)는 워커에서(A42).
            // BitmapImage는 EXIF 회전을 자동 반영하지 않으므로 여기서 읽어 RotateTransform에 합산한다.
            // A194: 이웃 선읽기 캐시에 있으면(완료·진행 중 모두 Task) 그 결과를 받아 디코드를
            // 생략한다 — 진행 중이면 같은 Task를 await(직렬 큐라 새로 넣는 것보다 먼저 끝난다).
            var (data, width, height, exifRotation, size, kind, exif) =
                _preloadCache.TryGetValue(path, out var preloaded)
                    ? await preloaded
                    : await Worker.Run(_ => ReadImageFile(path));
            // A194: 캐시 히트는 대기 없이 완료될 수 있어 종전 직렬 큐의 "요청 순서 = 적용 순서"가
            // 깨질 수 있다 — Magick 경로와 같은 현재 파일 재검증으로 낡은 결과를 버린다
            // (필드 대입 전이라 직전 파일 메타가 새 화면에 섞이지 않는다).
            if (_navigator?.Current != path) return;

            _pixelWidth = width;
            _pixelHeight = height;
            _exifRotation = exifRotation;
            _sizeText = size;
            _kindText = kind;
            _exifText = exif;

            var bitmap = new BitmapImage(); // GIF 애니메이션은 BitmapImage 기본 지원
            using (var stream = new MemoryStream(data))
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            if (_navigator?.Current != path) return; // A194 — SetSourceAsync 대기 중의 항해도 폐기

            ImageControl.Source = bitmap;
            _printBytes = data; // A211 — 인쇄 재료(표시 소스와 한 몸으로 세운다)
            PlaceholderText.Visibility = Visibility.Collapsed;
            _userRotation = 0;
            ApplyRotation();
            Scroller.ChangeView(0, 0, 1.0f, disableAnimation: true); // 줌 초기화(창맞춤)
            UpdateStatusBar();
            ContentOpened?.Invoke(path); // 셸 동기화 (v0.25.0)
            // A54 → A191: 트레이 = 해상도 2줄. 이 발화는 _pixelWidth/_pixelHeight와 _exifRotation이
            // 모두 확정된 뒤라야 한다(위 대입 순서 유지 — 앞서 쏘면 직전 파일 해상도가 그려진다).
            TrayStatusChanged?.Invoke();
            SchedulePreloads(); // A194 — 표시 완료 후에야 이웃 선읽기(표시 로드보다 후순위)
        }
        catch (Exception ex)
        {
            ImageControl.Source = null;
            _printBytes = null; // A211 — 로드 실패도 "인쇄할 그림 없음"이다(표시 소스와 같은 처리)
            FileNameText.Text = $"Failed to load: {Path.GetFileName(path)} ({ex.Message})";
            // 이전 이미지 메타가 남지 않게 조각을 전부 비운다. A149에서 해상도가 MetaText로
            // 옮겨졌으므로 픽셀 크기도 함께 비워야 실패한 파일에 직전 파일의 해상도가 붙지 않는다.
            _sizeText = string.Empty;
            _kindText = string.Empty;
            _exifText = string.Empty;
            _pixelWidth = 0;
            _pixelHeight = 0;
            var failedMeta = BuildMetaText(); // 남는 건 순번뿐 — 실패해도 "몇 번째"는 유효하다
            MetaText.Text = failedMeta;
            ToolTipService.SetToolTip(MetaText, failedMeta.Length > 0 ? failedMeta : null);
            UpdateZoomText(); // A149: 보여 줄 이미지가 없으니 배율 표기도 비운다(위에서 Source=null)
            UpdatePrintButton(); // A211 — 이 경로는 UpdateStatusBar를 타지 않는다(파일명 칸에 오류 문구를 남겨야 해서)
            TrayStatusChanged?.Invoke();
        }
    }

    // ---------- Magick.NET 디코드 경로 (v0.34.0) ----------

    /// <summary>WIC(BitmapImage)가 못 읽어 Magick.NET으로 디코드해야 하는 포맷.</summary>
    private static bool NeedsMagickDecode(string path) =>
        Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// psd 등을 Magick.NET으로 PNG 바이트로 변환해 표시한다. psd는 첫 이미지가
    /// 병합(composite) 미리보기라 레이어 펼침 없이 그대로 쓴다. 디코드는 워커에서(A42).
    /// </summary>
    private async Task LoadViaMagickAsync(string path)
    {
        var (png, width, height, size, kind) = await Worker.Run(_ =>
        {
            using var magick = new ImageMagick.MagickImage(path);
            // A9: 비트뎁스 = 채널당 비트 × 채널 수 (Q8 빌드라 채널당 8비트 상한 — 근사값)
            var bitDepth = (uint)(magick.Depth * magick.ChannelCount);
            return (magick.ToByteArray(ImageMagick.MagickFormat.Png), magick.Width, magick.Height,
                FormatSize(new FileInfo(path).Length), FormatKind(path, bitDepth));
        });

        if (_navigator?.Current != path) return; // 그새 다른 파일로 이동함

        _pixelWidth = width;
        _pixelHeight = height;
        _exifRotation = 0; // Magick 디코드 경로에서는 EXIF 회전을 별도 적용하지 않는다
        _sizeText = size;
        _kindText = kind;
        _exifText = string.Empty; // 이 경로는 EXIF를 읽지 않는다(psd 등) — 이전 파일 것이 남지 않게 비운다
        using var stream = new MemoryStream(png);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

        ImageControl.Source = bitmap;
        _printBytes = png; // A211 — psd도 이 시점엔 PNG 바이트라 인쇄 파이프가 같다(BitmapImage 디코드 가능)
        PlaceholderText.Visibility = Visibility.Collapsed;
        _userRotation = 0;
        ApplyRotation();
        Scroller.ChangeView(0, 0, 1.0f, disableAnimation: true);
        UpdateStatusBar();
        ContentOpened?.Invoke(path);
        TrayStatusChanged?.Invoke(); // A54 → A191: 트레이 = 해상도 2줄(Magick 경로도 크기를 먼저 대입했다)
        SchedulePreloads(); // A194 — psd의 이웃이 일반 이미지면 선읽기가 그대로 통한다
    }

    /// <summary>
    /// 워커 스레드: 파일 전체를 읽고 WIC로 해상도·EXIF 회전과 하단 바 메타 조각(A9 —
    /// 용량·종류(확장자·비트뎁스)·EXIF 요약)을 뽑는다. WinRT 비동기는
    /// 전용 스레드라 동기 대기해도 UI 교착이 없다. 메타데이터 실패는 0으로 두고 표시는 계속.
    /// A149: 조립된 한 줄 대신 조각으로 돌려준다 — 표시 순서에 순번이 끼어들고(탐색마다 변한다)
    /// 해상도는 이미 별도 필드라, 조립은 표시 시점(BuildMetaText)에서만 한다.
    /// </summary>
    private static (byte[] Data, uint Width, uint Height, int ExifRotation,
        string Size, string Kind, string Exif) ReadImageFile(string path)
    {
        var data = File.ReadAllBytes(path);
        uint width = 0, height = 0;
        var exifRotation = 0;
        var size = FormatSize(data.LongLength);
        string kind;
        var exifSummary = string.Empty;
        try
        {
            using var stream = new MemoryStream(data).AsRandomAccessStream();
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            width = decoder.PixelWidth;
            height = decoder.PixelHeight;
            exifRotation = ReadExifRotation(decoder);
            kind = FormatKind(path, ReadBitDepth(decoder));
            exifSummary = ReadExifSummary(decoder);
        }
        catch
        {
            // 메타데이터를 못 읽어도 표시는 계속 시도한다 — 용량·확장자만이라도 보여준다.
            kind = FormatKind(path, 0);
        }
        return (data, width, height, exifRotation, size, kind, exifSummary);
    }

    // ---------- 이웃 선읽기 (A194) ----------

    /// <summary>
    /// 표시 완료 후 현재 파일의 양옆 각 1장을 선읽기한다 — 워커 큐에 표시 로드 <b>뒤</b>에 들어가
    /// 자연히 후순위다(직렬 큐 성질). 먼저 양옆을 벗어난 캐시를 즉시 버린다(등재 확정: 최대 2건).
    /// 다음 파일을 이전보다 먼저 넣는다 — 앞으로 넘기는 항해가 더 흔하다.
    /// </summary>
    private void SchedulePreloads()
    {
        PrunePreloadCache();
        if (_navigator is not { } nav) return;
        if (nav.PeekNext is { } next) _ = PreloadAsync(next);
        if (nav.PeekPrevious is { } previous) _ = PreloadAsync(previous);
    }

    /// <summary>
    /// 이웃 1장의 선읽기: ReadImageFile을 워커에 넣고 <b>Task째</b> 캐시에 담는다(캐시 doc 참고).
    /// 화면은 절대 만지지 않는다 — 결과 소비는 LoadCurrentAsync의 캐시 조회 단방향뿐이다.
    /// 제외: 이미 캐시에 있는 경로(중복 디코드 방지), Magick 디코드 경로(psd — 표시가 다른
    /// 경로라 조회처가 없다), 클라우드 전용 placeholder(A175 — 내용을 읽는 순간 하이드레이션.
    /// Navigator 엔트리에는 placeholder 정보가 없어 파일 Attributes로 판정한다 —
    /// ExplorerListing.IsCloudPlaceholder 단일 원본 재사용. 속성 읽기는 하이드레이션을 유발하지
    /// 않는다). 실패(디코드 예외·취소)는 조용히 캐시에서 걷어낸다(등재 확정 — 캐시에 안 남김).
    /// 완료 후 재검증: 그새 항해가 진행돼 양옆이 아니게 됐으면 낡은 결과를 즉시 버린다.
    /// </summary>
    private async Task PreloadAsync(string path)
    {
        if (_preloadCache.ContainsKey(path)) return;
        if (NeedsMagickDecode(path)) return;
        try
        {
            if (ExplorerListing.IsCloudPlaceholder(File.GetAttributes(path))) return;
        }
        catch
        {
            return; // 속성도 못 읽는 파일(그새 소실 등) — 선읽기 포기(표시 경로가 자기 오류를 띄운다)
        }

        var task = Worker.Run(_ => ReadImageFile(path));
        _preloadCache[path] = task;
        try
        {
            await task;
        }
        catch
        {
            _preloadCache.Remove(path); // 디코드 실패·워커 닫힘 — 조용히 무시, 캐시에 안 남김
            return;
        }
        PrunePreloadCache(); // 선읽는 사이 항해가 진행됐으면 여기서 걸러진다(낡은 결과 폐기)
    }

    /// <summary>양옆(PeekNext·PeekPrevious)이 아닌 캐시 항목을 전부 버린다 — 진행 중 Task도 함께
    /// (결과는 도착해도 담기지 않는다). 항해 불능 상태(목록 없음)면 전부 버린다.</summary>
    private void PrunePreloadCache()
    {
        if (_preloadCache.Count == 0) return;
        if (_navigator is not { } nav || nav.Current is null)
        {
            _preloadCache.Clear();
            return;
        }
        var next = nav.PeekNext;
        var previous = nav.PeekPrevious;
        foreach (var stale in _preloadCache.Keys
                     .Where(k => !string.Equals(k, next, StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(k, previous, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            _preloadCache.Remove(stale);
    }

    // ---------- 하단 바 메타 요약 구성 (A9) ----------

    /// <summary>파일 크기를 자릿수에 맞는 단위로. (예: 812 B / 34.2 KB / 2.41 MB / 1.2 GB)</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
        >= 1L << 20 => $"{bytes / 1024.0 / 1024.0:0.##} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>종류 표기: 확장자 대문자 + 비트뎁스. (예: "JPG 24-bit", 뎁스 없으면 "JPG")</summary>
    private static string FormatKind(string path, uint bitDepth)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return bitDepth > 0 ? $"{ext} {bitDepth}-bit" : ext;
    }

    /// <summary>픽셀당 비트 수(System.Image.BitDepth). 코덱이 지원 안 하면 0.</summary>
    private static uint ReadBitDepth(BitmapDecoder decoder)
    {
        try
        {
            var props = decoder.BitmapProperties.GetPropertiesAsync(
                new[] { "System.Image.BitDepth" }).AsTask().GetAwaiter().GetResult();
            if (props.TryGetValue("System.Image.BitDepth", out var v) && v.Value is uint depth)
                return depth;
        }
        catch
        {
            // BMP/GIF 등 속성 저장소 미지원 코덱은 무시.
        }
        return 0;
    }

    /// <summary>
    /// EXIF 요약 한 줄: 촬영일 · 카메라(제조사 모델) · 노출(셔터 f/조리개 ISO 초점거리).
    /// 정보 오버레이(ImageQuickInfo.BuildRows — A200에서 단일 빌더로 이관)의 여러 줄 표기를
    /// 하단 바용 인라인으로 압축한 것.
    /// EXIF 미지원 포맷·손상 파일은 빈 문자열.
    /// </summary>
    private static string ReadExifSummary(BitmapDecoder decoder)
    {
        try
        {
            var props = decoder.BitmapProperties.GetPropertiesAsync(new[]
            {
                "System.Photo.DateTaken", "System.Photo.CameraManufacturer",
                "System.Photo.CameraModel", "System.Photo.ExposureTime",
                "System.Photo.FNumber", "System.Photo.ISOSpeed", "System.Photo.FocalLength",
            }).AsTask().GetAwaiter().GetResult();

            var parts = new List<string>();
            if (Get(props, "System.Photo.DateTaken") is DateTimeOffset taken)
                parts.Add(taken.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));

            var maker = Get(props, "System.Photo.CameraManufacturer") as string;
            var model = Get(props, "System.Photo.CameraModel") as string;
            var camera = $"{maker} {model}".Trim();
            if (camera.Length > 0) parts.Add(camera);

            var exposure = new List<string>();
            if (Get(props, "System.Photo.ExposureTime") is double sec and > 0)
                exposure.Add(sec >= 1 ? $"{sec:0.#}s" : $"1/{Math.Round(1 / sec)}s");
            if (Get(props, "System.Photo.FNumber") is double f and > 0)
                exposure.Add($"f/{f:0.#}");
            if (Get(props, "System.Photo.ISOSpeed") is ushort iso)
                exposure.Add($"ISO {iso}");
            if (Get(props, "System.Photo.FocalLength") is double mm and > 0)
                exposure.Add($"{mm:0.#}mm");
            if (exposure.Count > 0) parts.Add(string.Join(" ", exposure));

            return string.Join("  ·  ", parts);
        }
        catch
        {
            return string.Empty; // EXIF 미지원 포맷(BMP/GIF 등)·손상 파일
        }
    }

    /// <summary>EXIF orientation → 시계방향 회전 각도. 미러링 값은 회전만 근사 적용(TODO: 반전 처리).</summary>
    private static int ReadExifRotation(BitmapDecoder decoder)
    {
        try
        {
            var props = decoder.BitmapProperties.GetPropertiesAsync(
                new[] { "System.Photo.Orientation" }).AsTask().GetAwaiter().GetResult();
            if (props.TryGetValue("System.Photo.Orientation", out var value) &&
                value.Value is ushort orientation)
            {
                return orientation switch
                {
                    3 or 4 => 180,
                    5 or 6 => 90,
                    7 or 8 => 270,
                    _ => 0,
                };
            }
        }
        catch
        {
            // BMP/GIF/ICO 등 orientation 미지원 포맷은 무시.
        }
        return 0;
    }

    // ---------- 회전 / 창맞춤 ----------

    /// <summary>
    /// 지금 화면에 적용 중인 총 회전 각도(도, 0/90/180/270) = EXIF 회전 + R 키 누적 회전.
    /// <b>이 뷰에서 회전 각의 단일 원본</b>이다 — 표시 변환(<see cref="ApplyRotation"/>),
    /// 축 교환 판정(<see cref="RotationSwapsAxes"/>), 인쇄 페이지 변환
    /// (<see cref="CreatePrintPageAsync"/>)이 모두 이 한 값을 본다(A211 배치 2에서 이름을 붙였다 —
    /// 식 자체는 v0.29.0부터 무변경이고 계산 결과도 종전과 같다).
    /// </summary>
    private int TotalRotation => (_exifRotation + _userRotation) % 360;

    private void ApplyRotation()
    {
        RotationTransform.Angle = TotalRotation;
        UpdateFit();
    }

    /// <summary>
    /// 표시 축이 원본 디코드 축과 뒤바뀐 상태인가 = 총 회전(EXIF + R 키 누적)이 90°/270°인가.
    /// 레이아웃 제한(<see cref="UpdateFit"/>)과 트레이 해상도 2줄(A191), 인쇄 맞춤 계산
    /// (A211 배치 2)이 <b>같은 한 판정</b>을 봐야 "보이는 모양"과 "표기"가 어긋나지 않는다.
    /// </summary>
    private bool RotationSwapsAxes => TotalRotation % 180 != 0;

    // ---------- 보기 모드 (A83: 100% / Contain / Fit width / Fit height — 3모듈 공통) ----------

    /// <summary>
    /// Contain = 긴 변이 잘리지 않게 창에 맞춤(기본). <b>축소만</b> 한다 — 뷰포트보다 클 때만
    /// 줄이고 작은 이미지는 원본 크기 그대로다(A83 확정. MaxWidth/MaxHeight 제한이므로
    /// 확대 경로가 애초에 없다 — 구 이름 Fit에서 의미 변화 없음).
    /// FitWidth/FitHeight = 해당 축을 꽉 채움(반대 축은 스크롤, 확대·축소 양방향).
    /// ActualSize = 실제 픽셀 1:1. ←/→ 탐색 간에도 선택한 모드를 유지한다.
    /// </summary>
    private enum FitMode { Contain, FitWidth, FitHeight, ActualSize }

    private FitMode _fitMode = FitMode.Contain;

    /// <summary>
    /// A30 규격(A111에서 이미지에도 도입): Fit 버튼 본체가 표시·재적용할 마지막 옵션.
    /// A83 이후 100%도 플라이아웃 옵션이라 ActualSize까지 들어온다(1:1 별도 버튼은 없어졌다).
    /// 영속화하지 않는다 — 이미지 모듈은 종전대로 세션 안에서만 유지한다(A83 5항: 현행 유지).
    /// </summary>
    private FitMode _lastFitOption = FitMode.Contain;

    /// <summary>현재 보기 모드를 이미지 레이아웃에 적용한다(창맞춤은 뷰포트 크기로 제한).</summary>
    private void UpdateFit()
    {
        if (Scroller.ViewportWidth <= 0 || Scroller.ViewportHeight <= 0) return;

        var vw = Scroller.ViewportWidth;
        var vh = Scroller.ViewportHeight;
        // 90°/270° 회전 시 레이아웃 축이 바뀌므로 가로·세로 제한을 맞바꿔 적용한다.
        var swapped = RotationSwapsAxes;

        ImageControl.ClearValue(FrameworkElement.WidthProperty);
        ImageControl.ClearValue(FrameworkElement.HeightProperty);
        ImageControl.MaxWidth = double.PositiveInfinity;
        ImageControl.MaxHeight = double.PositiveInfinity;

        switch (_fitMode)
        {
            case FitMode.Contain:
                // 상한만 건다 = 큰 이미지는 줄고 작은 이미지는 그대로(A83 "축소만").
                ImageControl.MaxWidth = swapped ? vh : vw;
                ImageControl.MaxHeight = swapped ? vw : vh;
                break;
            case FitMode.FitWidth:
                // 명시 크기(Uniform이 비율 유지)로 작은 이미지도 좌우를 채운다.
                if (swapped) ImageControl.Height = vw;
                else ImageControl.Width = vw;
                break;
            case FitMode.FitHeight:
                if (swapped) ImageControl.Width = vh;
                else ImageControl.Height = vh;
                break;
            case FitMode.ActualSize:
                // 논리 픽셀 = 물리 픽셀 / 배율 → 화면에서 실제 픽셀 1:1로 보인다.
                if (_pixelWidth > 0 && _pixelHeight > 0)
                {
                    var scale = XamlRoot?.RasterizationScale ?? 1.0;
                    ImageControl.Width = _pixelWidth / scale;
                    ImageControl.Height = _pixelHeight / scale;
                }
                break;
        }
    }

    /// <summary>
    /// 보기 모드를 바꾼다. Fit은 ZoomFactor가 아니라 이미지의 Width/MaxWidth로 구현되므로
    /// 여기서 배율을 1.0으로 되돌린다 — 그래서 A149 줌 표시(ZoomText)는 어떤 Fit 모드에서든
    /// 100%가 정상이다(자세한 이유는 UpdateZoomText 주석).
    /// </summary>
    private void SetFitMode(FitMode mode)
    {
        _fitMode = mode;
        Scroller.ChangeView(0, 0, 1.0f, disableAnimation: true); // 줌 초기화 후 모드 적용
        UpdateFit();
    }

    /// <summary>
    /// A30 규격: Fit 버튼 본체 내용(4옵션 아이콘)과 툴팁을 마지막 옵션에 맞춘다.
    /// A144: 본체가 SplitButton에서 일반 Button(32×32)이 됐다 — 화살표는 별도
    /// DropDownButton(FitOptionsButton, 플라이아웃 전담·A34 키 없음·툴팁은 XAML 고정)이라
    /// 이 메서드는 종전대로 본체(FitButton)만 만진다.
    /// A143: 100%도 아이콘이 됐다 — 종전 "1:1" 텍스트(FontSize 13) 대신 PathIcon(부록 B 69).
    /// A184: 그 PathIcon 도형을 글자 "1:1" 형상에서 꺾쇠 프레임으로 바꿨다.
    /// A231(3차): 도형 자체를 폐기하고 <b>소형 텍스트 "100%"</b>로 갔다 — 2026-08-25
    /// "무슨 아이콘인지 알 수 없다"는 사용자 재보고 때문이다. 본체는 Button이라 Content에
    /// TextBlock을 넣을 수 있다(문서 모듈 A145의 비활성 표시와 같은 방식 — IconElement만 받는
    /// MenuFlyoutItem.Icon과 다르다).
    /// A253(4차): 옵션 이름을 "100%" → <b>"Original"</b>로 바꾸고(플라이아웃 항목 텍스트),
    /// 본체 상태 표시는 그 약자 "OR" 두 글자로 갔다(2026-08-27 사용자 지시).
    /// A260(5차·확정): 그 "OR"을 <b>테두리 상자 안의 "1:1"</b>로 바꾼다(2026-08-27 사용자 지시 —
    /// 약자보다 배율 기호가 즉시 읽힌다. A143의 "1:1" 글자가 상자를 얻어 돌아온 형태다).
    /// 조립은 <see cref="BuildOriginalRatioBox"/> 한 곳 — 본체는 Button이라 Content에 임의
    /// UIElement를 넣을 수 있다(IconElement만 받는 MenuFlyoutItem.Icon과 다르다).
    /// 툴팁 "Original size"·항목 이름 "Original"·A 키 표기는 A253 그대로 무변경.
    /// 세 모듈(이미지·문서·영상) 동형이라 함께 고칠 것.
    /// </summary>
    private void UpdateFitButton()
    {
        (object content, string tip) = _lastFitOption switch
        {
            FitMode.FitWidth =>
                ((object)new FontIcon { Glyph = "\uE8AB", FontSize = 18 }, "Fit width"),
            FitMode.FitHeight =>
                (new FontIcon { Glyph = "\uE8CB", FontSize = 18 }, "Fit height"),
            FitMode.ActualSize =>
                (BuildOriginalRatioBox(), "Original size"),
            _ => (new FontIcon { Glyph = "\uE9A6", FontSize = 18 },
                "Contain - the whole image fits, never enlarged"),
        };
        FitButton.Content = content;
        ToolTipService.SetToolTip(FitButton, FitTip(tip)); // A34: 표기는 키 상수에서
    }

    /// <summary>
    /// A260: 원본 배율(Original) 본체 표시 = 테두리 상자 안의 "1:1". 호출할 때마다 <b>새
    /// 인스턴스</b>를 만든다 — 만들어 둔 UIElement를 돌려쓰면 두 번째 부모 붙이기에서 죽는다
    /// (v0.174.1 실사례의 일반형). 세 모듈이 각자 같은 메서드를 가진다(모듈 간 공유 금지).
    /// 치수 근거: 32×32 버튼의 내용 칸 26px(BottomBarButtonStyle의 테두리 1 + Padding 2를 뺀 값)
    /// 안에 테두리 2 + 좌우 Padding 4 + 글자 "1:1"(9px에서 대략 13) = 대략 19라 여유가 있다
    /// (실기기에서 잘리면 FontSize 8로 내릴 것).
    /// </summary>
    private static Border BuildOriginalRatioBox()
    {
        var label = new TextBlock
        {
            Text = "1:1",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // 테두리 색은 글자와 같은 계열 — 조회 실패면 그 TextBlock의 기본 브러시로 떨어진다.
            BorderBrush = OriginalRatioBoxBrush() ?? label.Foreground,
            Child = label,
        };
    }

    /// <summary>
    /// A260: 상자 테두리 브러시 조회. XAML의 ThemeResource 참조는 키가 없을 때 런타임 파스
    /// 실패로 앱이 죽으므로 코드에서 감싸 가져온다(DriveStrip.ThemeBrush와 같은 관용구 —
    /// 인덱서는 키가 없으면 던진다). 실패하면 null을 돌려 호출부가 TextBlock 기본 Foreground를
    /// 쓰게 한다. Brush 인스턴스는 공유해도 안전하다(부모가 하나뿐인 것은 Geometry·UIElement).
    /// </summary>
    private static Brush? OriginalRatioBoxBrush()
    {
        try
        {
            if (Application.Current.Resources["TextFillColorPrimaryBrush"] is Brush brush) return brush;
        }
        catch
        {
            // 키 없음 — 호출부 폴백
        }
        return null;
    }

    /// <summary>
    /// 본체 툴팁 = "지금 표시 중인 옵션 (F) · Original (A)" — 1:1 버튼이 사라져도 A 키 표기가
    /// 남게 병합한다(A111). 두 표기 모두 키 상수에서 조립한다(A34 표기 규칙).
    /// A253: 뒤쪽 표기어가 "100%" → "Original"(플라이아웃 항목 이름과 같은 말로 통일).
    /// </summary>
    private static string FitTip(string description) =>
        $"{HotkeySupport.Tip(description, FitKey)} · {HotkeySupport.Tip("Original", ActualSizeKey)}";

    /// <summary>
    /// A230(v0.234.0) → A249(v0.246.0, 정면 반전): Fit 조절기 2개(본체 + 화살표)를 빈 상태에서
    /// 접던 것을 되돌려 <b>표시는 늘 유지하고 활성만</b> 여닫는다(2026-08-27 사용자 지시 —
    /// "현재 모듈에서 쓰는 버튼류는 항상 표시, 사용 불가면 비활성으로만"). A145의 "항상 보이되
    /// 비활성" 원칙이 이 조절기에도 다시 적용된 상태다.
    /// 판정 축은 A230 그대로 = "볼 파일이 있는가"(<c>_navigator.Current</c>), 유일한 호출 지점도
    /// 그대로 <see cref="UpdateStatusBar"/>의 두 분기다.
    /// A·F 키 차단은 가시성이 아니라 여기서 세우는 IsEnabled가 진다 — HotkeySupport의 통과
    /// 게이트가 버튼의 <c>IsEnabled</c>와 <c>Visibility</c>를 <b>둘 다</b> 보기 때문에
    /// (Shared/HotkeySupport.cs:61) 비활성만으로도 키가 새지 않는다. 그래서 A230이 "부모가 아니라
    /// 버튼 각각을 접는다"고 못 박았던 근거(가시성 게이트)는 IsEnabled 게이트가 그대로 대체한다.
    /// </summary>
    private void SetFitControlEnabled(bool enabled)
    {
        FitButton.IsEnabled = enabled;
        FitOptionsButton.IsEnabled = enabled;
    }

    /// <summary>
    /// A251(v0.246.0): 회전 버튼 활성 = <b>볼 그림이 있는가</b>(Fit 조절기와 같은 축 —
    /// <c>_navigator.Current</c>). 종전에는 IsEnabled 관리가 전무해 빈 상태(중앙 = 셸 썸네일
    /// 탐색기)에서도 활성이었고, 클릭·R 키가 <c>_userRotation</c>만 무의미하게 돌렸다
    /// (다음 로드에서 리셋되는 죽은 활성 버튼). A249 정책의 이미지 적용분이다.
    /// R 키는 <see cref="HotkeySupport"/>의 IsEnabled 게이트가 자동으로 막는다(Bind 등록 —
    /// 비활성이면 args.Handled를 false로 되돌려 그대로 흘려보낸다).
    /// 두 분기(파일 있음·없음)에 공통이라 <see cref="UpdatePrintButton"/>과 나란히 분기 앞에서
    /// 한 번만 부른다 — 판정은 이 메서드가 같은 축을 직접 읽는다.
    /// </summary>
    private void UpdateRotateButton() => RotateButton.IsEnabled = _navigator?.Current is not null;

    /// <summary>A30 규격: 플라이아웃에서 옵션 선택 — 즉시 적용하고 버튼 표시를 그 옵션으로 바꾼다.</summary>
    private void SelectFitOption(FitMode option)
    {
        _lastFitOption = option;
        SetFitMode(option);
        UpdateFitButton();
    }

    /// <summary>A30 규격: 본체 클릭 = 버튼에 표시된 마지막 옵션 재적용
    /// (A144: SplitButton 본체 → 일반 Button — 시그니처만 RoutedEventArgs로 바뀌었다).</summary>
    private void OnFitClick(object sender, RoutedEventArgs e) => SetFitMode(_lastFitOption);

    private void OnFitActualSizeClick(object sender, RoutedEventArgs e) => SelectFitOption(FitMode.ActualSize);

    private void OnFitContainClick(object sender, RoutedEventArgs e) => SelectFitOption(FitMode.Contain);

    private void OnFitWidthClick(object sender, RoutedEventArgs e) => SelectFitOption(FitMode.FitWidth);

    private void OnFitHeightClick(object sender, RoutedEventArgs e) => SelectFitOption(FitMode.FitHeight);

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFit();

    private void RotateClockwise()
    {
        _userRotation = (_userRotation + 90) % 360;
        ApplyRotation();
        // A191: 한 번 돌 때마다 트레이의 가로·세로 표기가 뒤바뀐다(총 회전 90°/270° ↔ 0°/180°).
        // 두 번 눌러 180°가 된 경우처럼 표기가 그대로면 셸의 ComposeKey 선비교가 재합성을 막는다 —
        // 여기서 조건을 따지지 않는 이유다(정사각 이미지도 같은 방어에 걸린다).
        TrayStatusChanged?.Invoke();
    }

    // ---------- 탐색 / 삭제 ----------

    private async Task MoveAsync(bool forward)
    {
        if (_navigator is null) return;
        var moved = forward ? _navigator.MoveNext() : _navigator.MovePrevious();
        if (moved) await LoadCurrentAsync(); // 끝에서는 순환하지 않고 멈춤
    }

    private async Task DeleteCurrentAsync()
    {
        var path = _navigator?.Current;
        if (_navigator is null || path is null) return;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await file.DeleteAsync(StorageDeleteOption.Default); // 휴지통으로
            _navigator.Remove(path);
            _preloadCache.Remove(path); // A194 — 삭제된 파일의 선읽기 잔재 제거(이어지는 표시는
                                        // 이웃 캐시 히트가 그대로 통한다 — LoadCurrentAsync 조회)
            await LoadCurrentAsync(); // 다음(마지막이었다면 이전) 이미지 표시
        }
        catch (Exception ex)
        {
            FileNameText.Text = $"Failed to delete: {Path.GetFileName(path)} ({ex.Message})";
        }
    }

    // ---------- 전체화면 ----------
    // A151: F11/Esc 액셀러레이터·⛶ 버튼은 제거 — 전체화면은 셸의 3단 모드 체계
    // (Enter 순환·Alt+Enter·Esc·모드 버튼)가 담당한다. 이 뷰에는 더블클릭 토글만 남는다
    // (뷰 고유 입력 — 셸이 AppWindow.Changed로 프레젠터 변화를 보고 모드를 동기화한다).

    private void ToggleFullScreen()
    {
        // Window 객체 없이 XamlRoot 경유로 AppWindow에 접근한다.
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;

        var appWindow = AppWindow.GetFromWindowId(environment.AppWindowId);
        appWindow.SetPresenter(appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
    }

    // ---------- 상태바 ----------

    private void UpdateStatusBar()
    {
        // A211: 인쇄 버튼 활성은 아래 두 분기(파일 있음·없음)에 공통이라 분기 앞에서 한 번 갱신한다.
        // 이 메서드는 표시 상태가 확정된 뒤에만 불린다(Source·_printBytes 대입 다음) — 호출 지점
        // 전수: 생성자 빈 상태 · LoadCurrentAsync 성공/빈 상태 · LoadViaMagickAsync 성공.
        // 예외는 로드 실패 경로 하나뿐이고 거기서는 직접 부른다(그쪽 주석 참조).
        // A230 → A249: Fit 조절기의 활성 전환도 이 메서드의 두 분기가 겸한다 — 판정 축은 이미
        // 여기 있는 "현재 파일 경로"(_navigator.Current) 하나뿐이라 전환 지점을 새로 만들지 않는다.
        // (A230은 같은 자리에서 Visibility를 여닫았고, A249가 그것을 IsEnabled로 되돌렸다.)
        // 로드 실패(위 예외 경로)는 파일 자체는 열려 있고 ←/→ 탐색도 유효하므로 조절기를 끄지
        // 않는다 — 인쇄 버튼(CanPrintNow = 표시 중인 비트맵 축)과 판정 축이 다른 자리다.
        // A251: 회전 버튼도 두 분기 공통이라 인쇄 버튼과 나란히 여기서 한 번 갱신한다.
        UpdatePrintButton();
        UpdateRotateButton();
        var path = _navigator?.Current;
        if (path is null)
        {
            SetFitControlEnabled(false); // A249: 빈 상태 = 중앙이 셸 썸네일 탐색기 — 보이되 비활성
            FileNameText.Text = "No file open";
            ZoomText.Text = string.Empty;
            MetaText.Text = string.Empty;
            ToolTipService.SetToolTip(MetaText, null);
            return;
        }

        SetFitControlEnabled(true); // A249: 볼 파일이 있다 — 조절기 활성
        FileNameText.Text = Path.GetFileName(path);
        // A149: 파일명 오른쪽에 나머지 정보를 한 덩어리로. 좁으면 말줄임되므로 전체는 툴팁으로.
        var meta = BuildMetaText();
        MetaText.Text = meta;
        ToolTipService.SetToolTip(MetaText, meta.Length > 0 ? meta : null);
        // ChangeView가 실제 변화를 만들지 않으면(이미 100%였다) ViewChanged가 안 올 수 있어
        // 파일이 바뀔 때는 여기서도 한 번 맞춘다.
        UpdateZoomText();
        // A22(v0.108.0): 드라이브 표시는 파일이 열려 있을 때가 아니라 없을 때만 —
        // 여기서 조회하던 v0.47.0 텍스트는 셸이 주입하는 공용 드라이브 줄로 대체됐다.
    }

    /// <summary>
    /// A149 확정 순서: 해상도 · 확장자(비트뎁스) · 용량 · 순번, 그 뒤에 A9의 EXIF 요약.
    /// 파일명은 별도 TextBlock(FileNameText)이라 여기 들어오지 않는다 — 좁은 폭에서 파일명이
    /// 먼저 살아남아야 하기 때문(XAML의 Auto/* 배분).
    /// 빈 조각은 건너뛴다 = 구분자만 남는 "  ·    ·  " 같은 모양이 생기지 않는다.
    /// </summary>
    private string BuildMetaText()
    {
        var parts = new List<string>();
        if (_pixelWidth > 0) parts.Add($"{_pixelWidth}×{_pixelHeight}");
        if (_kindText.Length > 0) parts.Add(_kindText);
        if (_sizeText.Length > 0) parts.Add(_sizeText);
        if (PositionText() is { Length: > 0 } position) parts.Add(position);
        if (_exifText.Length > 0) parts.Add(_exifText);
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// A149: 현재 배율 표시. 출처는 Scroller.ZoomFactor 하나뿐이다(전용 필드를 두지 않는다).
    /// <para>
    /// Fit 버튼 본체 표기와 겹쳐 보이지만 역할이 다르다 — 버튼은 <b>모드</b>(1:1 / Contain /
    /// Fit width / Fit height), 여기는 <b>실제 배율</b>이다. Fit이 ZoomFactor가 아니라
    /// Width/MaxWidth로 구현돼 있어(UpdateFit) SetFitMode가 배율을 1.0으로 되돌리므로
    /// "Fit width인데 100%"가 정상이다 — 모순이 아니니 뒤집지 말 것.
    /// </para>
    /// 팬(A148)·휠 줌은 매 프레임 ViewChanged를 부르므로 값이 같으면 대입하지 않는다.
    /// </summary>
    private void UpdateZoomText()
    {
        // 판정 기준은 "표시 중인 비트맵이 있는가" — 파일 없음(경로 null)과 로드 실패를 한 조건으로
        // 덮는다(둘 다 Source를 null로 만든다). 실패 뒤 창 크기 변경 같은 늦은 ViewChanged가
        // 배율을 되살리지 않게 하려면 경로가 아니라 이 상태를 봐야 한다.
        var text = ImageControl.Source is null
            ? string.Empty
            : $"{Scroller.ZoomFactor * 100:0}%";
        if (ZoomText.Text == text) return;
        ZoomText.Text = text;
    }

    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        UpdateZoomText();

    private string PositionText() =>
        _navigator is { Count: > 0 } nav ? $"{nav.CurrentIndex + 1}/{nav.Count}" : string.Empty;

    // ---------- 입력 핸들러 ----------

    private async void OnPreviousInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await MoveAsync(forward: false);
    }

    private async void OnNextInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await MoveAsync(forward: true);
    }

    private async void OnDeleteInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await DeleteCurrentAsync();
    }

    /// <summary>
    /// 더블클릭 = 전체화면 토글. 이 핸들러는 UserControl <b>루트</b>에 걸려 있어(XAML) 팬을
    /// 배선한 콘텐츠 프레젠터와 층이 다르다 — 프레젠터에서 Handled를 세워도 여기까지 오므로
    /// 방금 팬이 있었으면 시간 창으로 막는다(A148). 삼킬 때도 Handled는 세운다:
    /// 여기서 통과시키면 상위(셸)의 다른 더블탭 해석이 뒤늦게 붙을 수 있다.
    /// </summary>
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (IsPanSuppressingDoubleTap()) return; // 밀어서 보다 손 뗀 직후 — 전체화면 전환은 사고다
        ToggleFullScreen();
    }

    private void OnRotateButtonClick(object sender, RoutedEventArgs e) => RotateClockwise();

    // ---------- 하단 바 버튼 핫키 (A34) ----------

    /// <summary>Fit 키 — 툴팁 표기(UpdateFitButton)와 액셀러레이터가 이 한 값을 함께 쓴다.</summary>
    private const VirtualKey FitKey = VirtualKey.F;

    /// <summary>
    /// 100%(1:1) 키 — A111에서 1:1 버튼이 사라진 뒤로도 A는 그대로 100% 적용이다(A107 확정:
    /// 문자 핫키 전부 유지). 대상만 Fit 버튼의 100% 옵션 적용 액션으로 옮겼다.
    /// </summary>
    private const VirtualKey ActualSizeKey = VirtualKey.A;

    /// <summary>
    /// A34: 하단 바 버튼에 단독 문자 키를 걸고 툴팁 "(키)" 표기까지 같은 호출에서 만든다 —
    /// 키와 표기가 어긋날 수 없다. 텍스트 입력·탐색기 파일 리스트에 포커스가 있으면
    /// HotkeySupport가 키를 삼키지 않고 통과시킨다(A32/A84 통과 규칙 재사용).
    /// R(회전)은 v0.29.0부터 있던 키를 XAML 액셀러레이터에서 여기로 옮긴 것 — 의미는 그대로다.
    /// A(100%)·F(Fit)는 영상·문서 모듈과 같은 뜻으로 통일한 키 — A111부터 둘 다 Fit 버튼에 건다
    /// (버튼이 하나로 합쳐졌을 뿐, 키 동작은 무변경). 툴팁은 상태를 따라가므로 UpdateFitButton()이
    /// 두 키 표기를 함께 만든다(Bind 대신 Register + 자체 툴팁).
    /// <para>
    /// A249/A251: 세 키(R·A·F) 모두 빈 상태에서 <b>버튼 비활성</b>만으로 차단된다 —
    /// HotkeySupport의 통과 게이트가 버튼의 IsEnabled와 Visibility를 둘 다 보기 때문이다
    /// (Shared/HotkeySupport.cs:61). 버튼을 접지 않아도 키는 새지 않는다.
    /// </para>
    /// </summary>
    private void SetupHotkeys()
    {
        HotkeySupport.Bind(this, RotateButton, VirtualKey.R,
            "Rotate 90° clockwise", RotateClockwise);
        HotkeySupport.Register(this, FitButton, ActualSizeKey,
            () => SelectFitOption(FitMode.ActualSize));
        HotkeySupport.Register(this, FitButton, FitKey, () => SetFitMode(_lastFitOption));
        UpdateFitButton(); // Fit 툴팁은 표시 상태를 따라가므로 초기값도 여기서 만든다
    }

    // ---------- 인쇄 (A211 배치 2, v0.221.0 — 계약 = IPrintPageProvider, 소비자 = 셸 PrintHost) ----------
    // 사양 단일 원본 = docs/A211-print-research.md §3-2 + 부록 B 78(이미지 = contain 고정·배치 옵션 없음).
    // 이 절이 하는 일은 셋뿐이다: ① "지금 인쇄 가능한가"를 표시 상태에서 답한다 ② 1페이지짜리
    // 인쇄 전용 요소를 매 호출 새로 조립한다 ③ 하단 바 버튼 클릭을 셸로 흘린다.
    // 대화상자·미리보기·페이지 수명은 전부 셸(PrintHost)이 갖는다 — 여기서 인쇄 API를 만지지 않는다.

    /// <summary>
    /// 모듈 하단 바 인쇄 버튼(PrintButton) → 셸 인쇄 단일 경로(MainWindow.RequestPrint).
    /// 셸이 ShowModule에서 구독한다 — 뷰는 셸을 모른 채 신호만 쏜다(계약 규정).
    /// </summary>
    public event Action? PrintRequested;

    /// <summary>
    /// 지금 인쇄할 그림이 있는가 = <b>표시 중인 비트맵</b>(첫 항)과 <b>그 재료</b>(둘째 항)가 함께 있는가.
    /// 첫 항의 기준은 <see cref="UpdateZoomText"/>가 쓰는 것과 같다(<c>ImageControl.Source</c>) —
    /// "파일 없음"과 "로드 실패"를 한 조건으로 덮는 이 뷰의 관용구다. 둘째 항은 페이지를 실제로
    /// 만들 수 있는지(<see cref="_printBytes"/>)이고, 두 값은 늘 같은 자리에서 함께 세워지고 비워진다.
    /// 빈 뷰(파일 없음)는 false — 셸 Ctrl+P·하단 바 버튼 둘 다 잠잠해진다.
    /// </summary>
    public bool CanPrintNow => ImageControl.Source is not null && _printBytes is { Length: > 0 };

    /// <summary>OS 인쇄 큐·대화상자에 뜰 작업 이름 = 파일 이름. 비면 셸이 앱 이름으로 대체한다.</summary>
    public string PrintJobName =>
        _navigator?.Current is { } path ? Path.GetFileName(path) : string.Empty;

    /// <summary>
    /// 이미지는 언제나 <b>1페이지</b>다(부록 B 78 — 배치 옵션 UI 없음, 여러 장 모아 찍기 없음).
    /// 규격(용지·해상도)에 따라 달라질 여지가 없어 <paramref name="spec"/>은 보지 않는다.
    /// 인쇄할 그림이 없으면 0 — 셸이 안내 페이지 1장으로 대체한다(계약).
    /// </summary>
    public int GetPrintPageCount(PrintPageSpec spec) => CanPrintNow ? 1 : 0;

    /// <summary>
    /// 인쇄 페이지 1장을 새로 조립한다 — 용지 크기의 <c>Canvas</c>(흰 배경) 위에 새 <c>Image</c> 하나.
    /// 미리보기(GetPreviewPage)와 본인쇄(AddPages)가 <b>같은 이 메서드</b>를 타므로 호출마다
    /// 새 인스턴스를 만든다(v0.174.1 교훈 — WinUI 요소는 부모가 하나뿐이다. 넘긴 참조는 셸이 쓰고 버린다).
    /// <para>
    /// 맞춤(contain)은 <b>용지 전체가 아니라 인쇄 가능 영역(Imageable)</b> 기준이다 — 용지 기준으로
    /// 잡으면 프린터가 물리적으로 못 찍는 가장자리에서 그림이 잘린다. 배율은 상한을 두지 않는다:
    /// 부록 B 78의 "여백 안 최대"가 확정 사양이고, 원본 크기(DPI 반영) 배치는 그 자리에서 기각됐다.
    /// 화면 Contain(A83)이 "축소만"인 것과 다른데, 그쪽은 작은 그림이 화면에서 뭉개지지 않게 하는
    /// 화면 전용 결정이라 종이에는 그대로 옮기지 않는다(의도된 차이 — 뒤집지 말 것).
    /// </para>
    /// <para>
    /// 회전은 화면과 <b>같은 한 값</b>(<see cref="TotalRotation"/>)을 그대로 옮긴다. 90°/270°에서는
    /// 종이 위에서 차지하는 축이 뒤바뀌므로(<see cref="RotationSwapsAxes"/>) 맞춤 계산의 폭·높이를
    /// 맞바꾼다 — 이 교환을 빼먹으면 세로 사진이 잘리는 고전 버그가 된다.
    /// 요소의 <b>레이아웃</b> 크기는 회전 전 축 그대로다(RotateTransform은 RenderTransform이라
    /// 레이아웃을 바꾸지 않는다). 그래서 회전 후 축으로 구한 배율을 원본 축에 곱해 크기를 주고,
    /// 배치는 <b>Canvas 절대 좌표</b>로 한다: 90°/270°에서는 회전 전 상자가 영역보다 넓을 수 있는데
    /// (예: 가로 사진을 세로 용지에 눕혀 찍을 때) Canvas는 자식을 항상 <b>원하는 크기 그대로</b>
    /// 배치해 잘림 여지가 없다(Grid의 정렬 배치는 슬롯보다 큰 자식에서 레이아웃 잘림 규칙에
    /// 걸릴 수 있다 — 그 규칙에 기대지 않는다). 좌표는 회전 중심(요소 중앙)이 영역 중앙에 오도록
    /// 잡으므로, 회전 후 그림은 인쇄 가능 영역 한가운데에 앉는다.
    /// </para>
    /// <para>
    /// 비동기: 계약이 Task를 돌려주는 이유가 여기 있다 — <b>디코드가 끝난 뒤에야</b> 반환한다.
    /// 먼저 반환하면 미리보기·인쇄물이 빈 페이지가 된다. 디코드 폭은 줄이지 않는다(DecodePixelWidth
    /// 미지정 = 원본 해상도) — 종이 해상도는 프린터가 정하고, 여기서 줄이면 되돌릴 수 없다.
    /// 그래서 <paramref name="spec"/>의 DpiX/DpiY도 보지 않는다(그 값은 PDF처럼 <b>우리가</b>
    /// 래스터화하는 공급자용이다 — 조사 §1-ⓒ).
    /// </para>
    /// null을 돌려주면 셸이 안내 페이지로 대체한다(인쇄 파이프는 계속 간다) — 예외를 던지지 않는다.
    /// </summary>
    public async Task<object?> CreatePrintPageAsync(int pageNumber, PrintPageSpec spec)
    {
        if (pageNumber != 1) return null;                       // 1페이지 고정 — 그 밖의 요청은 규격 위반
        if (_printBytes is not { Length: > 0 } bytes) return null;

        // 매 호출 새 BitmapImage — 표시용 소스를 나눠 쓰지 않는다(_printBytes 주석의 공유 판정).
        // 스트림 → BitmapImage 변환은 LoadCurrentAsync와 같은 관용구다(dispose 시점 포함).
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(bytes))
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

        // 원본 픽셀 크기: 표시 경로가 WIC 헤더에서 확정해 둔 값이 정본이고, 그걸 못 읽은 파일
        // (메타데이터 미지원·손상)은 방금 디코드한 비트맵 크기로 대신한다.
        var sourceWidth = _pixelWidth > 0 ? (double)_pixelWidth : bitmap.PixelWidth;
        var sourceHeight = _pixelHeight > 0 ? (double)_pixelHeight : bitmap.PixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        // 인쇄 가능 영역. 규격이 영역을 안 주면(0 이하 — 이상 규격 방어) 용지 전체를 영역으로 본다
        // (셸 PrintHost의 예비 규격도 같은 형: 용지 = 영역).
        var areaWidth = spec.ImageableWidth > 0 ? spec.ImageableWidth : spec.PageWidth;
        var areaHeight = spec.ImageableHeight > 0 ? spec.ImageableHeight : spec.PageHeight;
        var areaX = spec.ImageableWidth > 0 ? spec.ImageableX : 0;
        var areaY = spec.ImageableHeight > 0 ? spec.ImageableY : 0;
        if (areaWidth <= 0 || areaHeight <= 0) return null;

        // 종이 위에서 차지하는 축(회전 반영) 기준으로 배율을 구한다 — 90°/270°면 폭·높이 교환.
        var swapped = RotationSwapsAxes;
        var printedWidth = swapped ? sourceHeight : sourceWidth;
        var printedHeight = swapped ? sourceWidth : sourceHeight;
        var scale = Math.Min(areaWidth / printedWidth, areaHeight / printedHeight);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) return null;

        var layoutWidth = sourceWidth * scale;   // 회전 전(레이아웃) 크기
        var layoutHeight = sourceHeight * scale;
        // 타입 이름 주의: 이 네임스페이스(KOTU.Module.Image) 안에서 `Image`는 네임스페이스로 먼저
        // 해석되므로 XAML 요소 타입은 반드시 완전 이름으로 적는다(SponsorAds.Apply와 같은 형).
        var image = new Microsoft.UI.Xaml.Controls.Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            Width = layoutWidth,
            Height = layoutHeight,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), // 요소 중앙을 회전 중심으로
            RenderTransform = new RotateTransform { Angle = TotalRotation },
        };
        // 회전 중심(= 요소 중앙)을 인쇄 가능 영역 중앙에 맞춘다 → 회전 후 그림이 영역 한가운데.
        Canvas.SetLeft(image, areaX + ((areaWidth - layoutWidth) / 2));
        Canvas.SetTop(image, areaY + ((areaHeight - layoutHeight) / 2));

        // 색은 명시 지정(흰 종이) — 테마 브러시는 다크 테마에서 검정으로 풀려 온 페이지가 잉크가 된다(계약 규칙).
        var page = new Canvas
        {
            Width = spec.PageWidth,
            Height = spec.PageHeight,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        page.Children.Add(image);
        return page;
    }

    /// <summary>
    /// 인쇄 버튼 활성 = <see cref="CanPrintNow"/>(부록 B 78 규격 — 인쇄할 콘텐츠가 없으면 비활성).
    /// 셸 Ctrl+P는 이 버튼을 보지 않고 같은 속성을 직접 물으므로(MainWindow.RequestPrint)
    /// 버튼 표기와 키 동작이 어긋날 수 없다.
    /// </summary>
    private void UpdatePrintButton() => PrintButton.IsEnabled = CanPrintNow;

    /// <summary>버튼 클릭 = 셸에 인쇄 요청 신호 1발(배선은 셸이 — 계약 문서 규정).</summary>
    private void OnPrintButtonClick(object sender, RoutedEventArgs e) => PrintRequested?.Invoke();

    // ---------- Ctrl 정보 오버레이 (v0.25.0) ----------

    /// <summary>
    /// 파일·해상도 + EXIF. A150에서 라벨·값 행 목록으로 이식했고, A200에서 행 구성 전체를
    /// <see cref="ImageQuickInfo.BuildRows"/>(단일 빌더)로 이관했다 — 셸의 썸네일 선택 조회와
    /// 같은 출력을 내기 위함(두 경로 표시 불일치 금지). EXIF는 표시 키 전부 나열·값 없으면
    /// 빈칸(A150 "행 생략"의 반전 — 상세 규칙은 ImageQuickInfo 주석), 미지원 포맷은 기본 정보만.
    /// 해상도도 빌더가 디코더 헤더에서 직접 읽는다(_pixelWidth와 같은 원천 값 — decoder.PixelWidth).
    /// </summary>
    public async Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync()
    {
        var path = _navigator?.Current;
        if (path is null) return null;

        // 파일 크기·EXIF 조회는 파일 I/O — 워커에서 만들어 결과 목록만 받는다(A42).
        try
        {
            return await Worker.Run(_ => ImageQuickInfo.BuildRows(path));
        }
        catch
        {
            return null; // 오버레이 정보는 부가 기능
        }
    }

    private static object? Get(IDictionary<string, Windows.Graphics.Imaging.BitmapTypedValue> props, string key) =>
        props.TryGetValue(key, out var v) ? v.Value : null;
}
