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
using KOTU.Core.Threading;
using KOTU.Input;

namespace KOTU.Module.Image;

/// <summary>
/// 이미지 뷰어 화면. 폴더 내 ←/→ 탐색, 줌/팬(A148 마우스 드래그), 회전(R), 휴지통 삭제(Delete),
/// 전체화면 더블클릭 토글, 하단 상태바를 제공한다(F11·⛶ 버튼은 A151에서 셸 모드 체계로 이관).
/// 폴더 스캔·파일 읽기·디코드(WIC 메타데이터/Magick)는 뷰 전용 워커(A42)에서 직렬로 돌고
/// UI 스레드는 비트맵 표시만 한다 — 직렬이라 빠른 ←/→ 연타에도 적용 순서가 요청 순서와 같다.
/// </summary>
public sealed partial class ImageViewerView : UserControl, IContentStateSource, IContentInfoProvider,
    IBottomBarProvider, IDriveStripHost, ITrayStatusProvider
{
    /// <summary>트레이 아이콘 표시 값이 바뀌었다(A54) — 파일 열기/전환/실패 시점.</summary>
    public event Action? TrayStatusChanged;

    /// <summary>
    /// 트레이 아이콘 내용(A54): 열림 = 확장자 · 용량, 유휴 = "IMG".
    /// 용량은 표시 중인 파일에서 그때그때 읽는다 — 이벤트 기반 호출이라 빈도가 낮다.
    /// </summary>
    public TrayStatus GetTrayStatus()
    {
        if (_navigator?.Current is not { } path) return TrayStatus.Idle("IMG");
        long bytes = -1;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch
        {
            // 크기를 못 읽으면 그 줄만 "—"가 된다.
        }
        return TrayStatus.Open(TrayFormat.Extension(path), TrayFormat.Size(bytes));
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

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU image worker");

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
            var (data, width, height, exifRotation, size, kind, exif) =
                await Worker.Run(_ => ReadImageFile(path));

            _pixelWidth = width;
            _pixelHeight = height;
            _exifRotation = exifRotation;
            _sizeText = size;
            _kindText = kind;
            _exifText = exif;

            var bitmap = new BitmapImage(); // GIF 애니메이션은 BitmapImage 기본 지원
            using (var stream = new MemoryStream(data))
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

            ImageControl.Source = bitmap;
            PlaceholderText.Visibility = Visibility.Collapsed;
            _userRotation = 0;
            ApplyRotation();
            Scroller.ChangeView(0, 0, 1.0f, disableAnimation: true); // 줌 초기화(창맞춤)
            UpdateStatusBar();
            ContentOpened?.Invoke(path); // 셸 동기화 (v0.25.0)
            TrayStatusChanged?.Invoke(); // A54: 트레이 = 확장자 · 용량
        }
        catch (Exception ex)
        {
            ImageControl.Source = null;
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
        PlaceholderText.Visibility = Visibility.Collapsed;
        _userRotation = 0;
        ApplyRotation();
        Scroller.ChangeView(0, 0, 1.0f, disableAnimation: true);
        UpdateStatusBar();
        ContentOpened?.Invoke(path);
        TrayStatusChanged?.Invoke(); // A54
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
    /// 정보 오버레이(BuildContentInfo)의 여러 줄 표기를 하단 바용 인라인으로 압축한 것.
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

    private void ApplyRotation()
    {
        RotationTransform.Angle = (_exifRotation + _userRotation) % 360;
        UpdateFit();
    }

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
        var swapped = (_exifRotation + _userRotation) % 180 != 0;

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
    /// ⚠️ v0.174.1: PathIcon 인스턴스(UIElement)만이 아니라 **Geometry도 공유 금지** — WinUI
    /// Geometry는 부모가 하나뿐이라 공유 인스턴스를 PathIcon.Data에 걸면 XamlParseException
    /// ("Failed to assign to property")으로 앱이 죽는다(실기기 크래시 실사례 — 종전엔 App.xaml
    /// 공유 리소스를 봤다). 호출마다 BuildActualSizeIconGeometry()로 새로 만든다.
    /// </summary>
    private void UpdateFitButton()
    {
        (object content, string tip) = _lastFitOption switch
        {
            FitMode.FitWidth =>
                ((object)new FontIcon { Glyph = "\uE8AB", FontSize = 18 }, "Fit width"),
            FitMode.FitHeight =>
                (new FontIcon { Glyph = "\uE8CB", FontSize = 18 }, "Fit height"),
            FitMode.ActualSize => (new PathIcon
            {
                Data = BuildActualSizeIconGeometry(),
            }, "Actual size"),
            _ => (new FontIcon { Glyph = "\uE9A6", FontSize = 18 },
                "Contain - the whole image fits, never enlarged"),
        };
        FitButton.Content = content;
        ToolTipService.SetToolTip(FitButton, FitTip(tip)); // A34: 표기는 키 상수에서
    }

    /// <summary>
    /// A143/v0.174.1: 100% 아이콘 도형(16x16 좌표계 — PathIcon은 스케일하지 않는다). 도형 6개 =
    /// 왼쪽 1(깃발+기둥/밑변)·콜론 점 2개·오른쪽 1(깃발+기둥/밑변). 호출마다 새 인스턴스를 만든다
    /// (Geometry 공유 금지 — 위 UpdateFitButton 주석). 좌표를 바꾸면 이 파일 XAML의 인라인 Data
    /// 문자열과 형제 두 모듈(문서·영상)의 같은 두 곳까지 총 6곳을 함께 고칠 것.
    /// </summary>
    private static Geometry BuildActualSizeIconGeometry()
    {
        static PathFigure Fig(double sx, double sy, params (double X, double Y)[] points)
        {
            var figure = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(sx, sy),
                IsClosed = true,
                IsFilled = true,
                Segments = new PathSegmentCollection(),
            };
            foreach ((double x, double y) in points)
                figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x, y) });
            return figure;
        }

        var geometry = new PathGeometry { Figures = new PathFigureCollection() };
        geometry.Figures.Add(Fig(2.3, 5.0, (4.2, 3.0), (5.0, 3.0), (5.0, 11.4), (3.5, 11.4), (3.5, 5.0)));
        geometry.Figures.Add(Fig(1.8, 11.4, (6.7, 11.4), (6.7, 13.0), (1.8, 13.0)));
        geometry.Figures.Add(Fig(7.4, 6.0, (8.8, 6.0), (8.8, 7.4), (7.4, 7.4)));
        geometry.Figures.Add(Fig(7.4, 9.6, (8.8, 9.6), (8.8, 11.0), (7.4, 11.0)));
        geometry.Figures.Add(Fig(10.5, 5.0, (12.4, 3.0), (13.2, 3.0), (13.2, 11.4), (11.7, 11.4), (11.7, 5.0)));
        geometry.Figures.Add(Fig(10.0, 11.4, (14.9, 11.4), (14.9, 13.0), (10.0, 13.0)));
        return geometry;
    }

    /// <summary>
    /// 본체 툴팁 = "지금 표시 중인 옵션 (F) · 100% (A)" — 1:1 버튼이 사라져도 A 키 표기가
    /// 남게 병합한다(A111). 두 표기 모두 키 상수에서 조립한다(A34 표기 규칙).
    /// </summary>
    private static string FitTip(string description) =>
        $"{HotkeySupport.Tip(description, FitKey)} · {HotkeySupport.Tip("100%", ActualSizeKey)}";

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
        var path = _navigator?.Current;
        if (path is null)
        {
            FileNameText.Text = "No file open";
            ZoomText.Text = string.Empty;
            MetaText.Text = string.Empty;
            ToolTipService.SetToolTip(MetaText, null);
            return;
        }

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

    // ---------- Ctrl 정보 오버레이 (v0.25.0) ----------

    /// <summary>
    /// 파일·해상도 + EXIF. A150에서 라벨·값 행 목록으로 이식하고 항목을 확장했다
    /// (렌즈·프로그램·측광·플래시·화이트밸런스·색공간 — 기존과 같은 BitmapProperties 키 추가).
    /// 값이 없는 행은 생략한다. 미지원 포맷은 기본 정보만.
    /// </summary>
    public async Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync()
    {
        var path = _navigator?.Current;
        if (path is null) return null;

        // 파일 크기·EXIF 조회는 파일 I/O — 워커에서 만들어 결과 목록만 받는다(A42).
        var (width, height) = (_pixelWidth, _pixelHeight);
        try
        {
            return await Worker.Run(_ => BuildContentInfo(path, width, height));
        }
        catch
        {
            return null; // 오버레이 정보는 부가 기능
        }
    }

    /// <summary>워커 스레드: 정보 오버레이 행 목록 구성(WinRT 비동기는 동기 대기 — 전용 스레드).</summary>
    private static IReadOnlyList<ContentInfoItem> BuildContentInfo(string path, uint pixelWidth, uint pixelHeight)
    {
        var rows = new List<ContentInfoItem> { new("File", Path.GetFileName(path)) };
        try
        {
            var info = new FileInfo(path);
            rows.Add(new ContentInfoItem("Size", $"{info.Length / 1024.0 / 1024.0:0.##} MB"));
            rows.Add(new ContentInfoItem("Modified", $"{info.LastWriteTime:yyyy-MM-dd HH:mm}"));
        }
        catch
        {
            // 크기·날짜는 없어도 된다.
        }
        if (pixelWidth > 0)
            rows.Add(new ContentInfoItem("Dimensions", $"{pixelWidth}×{pixelHeight} px"));

        var exif = new List<ContentInfoItem>();
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            using var stream = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            // ⚠️ GPS 키(System.GPS.*)는 넣지 않는다 — 위치는 개인정보라 기본 숨김이 확정됐고
            // (부록 B 69), 표시 토글이 생기기 전에는 수집 자체를 하지 않는 게 가장 안전하다.
            var props = decoder.BitmapProperties.GetPropertiesAsync(new[]
            {
                "System.Photo.DateTaken", "System.Photo.CameraManufacturer",
                "System.Photo.CameraModel", "System.Photo.LensModel",
                "System.Photo.ExposureTime", "System.Photo.FNumber",
                "System.Photo.ISOSpeed", "System.Photo.FocalLength",
                "System.Photo.ExposureProgram", "System.Photo.MeteringMode",
                "System.Photo.Flash", "System.Photo.WhiteBalance", "System.Image.ColorSpace",
            }).AsTask().GetAwaiter().GetResult();

            if (Get(props, "System.Photo.DateTaken") is DateTimeOffset taken)
                exif.Add(new ContentInfoItem("Taken", $"{taken.LocalDateTime:yyyy-MM-dd HH:mm}"));

            var maker = Get(props, "System.Photo.CameraManufacturer") as string;
            var model = Get(props, "System.Photo.CameraModel") as string;
            if (!string.IsNullOrWhiteSpace(maker) || !string.IsNullOrWhiteSpace(model))
                exif.Add(new ContentInfoItem("Camera", $"{maker} {model}".Trim()));

            if (Get(props, "System.Photo.LensModel") is string lens && !string.IsNullOrWhiteSpace(lens))
                exif.Add(new ContentInfoItem("Lens", lens.Trim()));

            // 노출 4요소는 종전과 같은 합성 값 한 행(1/125 s · f/2.8 · ISO 400 · 50 mm).
            var exposure = new List<string>();
            if (Get(props, "System.Photo.ExposureTime") is double sec and > 0)
                exposure.Add(sec >= 1 ? $"{sec:0.#} s" : $"1/{Math.Round(1 / sec)} s");
            if (Get(props, "System.Photo.FNumber") is double f and > 0)
                exposure.Add($"f/{f:0.#}");
            if (Get(props, "System.Photo.ISOSpeed") is ushort iso)
                exposure.Add($"ISO {iso}");
            if (Get(props, "System.Photo.FocalLength") is double mm and > 0)
                exposure.Add($"{mm:0.#} mm");
            if (exposure.Count > 0)
                exif.Add(new ContentInfoItem("Exposure", string.Join(" · ", exposure)));

            // enum류는 영어 문구로 매핑, 미정의 값은 행 생략(수치 노출보다 생략이 안전).
            if (ExposureProgramText(GetUInt(props, "System.Photo.ExposureProgram")) is { } program)
                exif.Add(new ContentInfoItem("Program", program));
            if (MeteringModeText(GetUInt(props, "System.Photo.MeteringMode")) is { } metering)
                exif.Add(new ContentInfoItem("Metering", metering));
            if (GetUInt(props, "System.Photo.Flash") is { } flash)
                exif.Add(new ContentInfoItem("Flash", (flash & 1) != 0 ? "Fired" : "Did not fire"));
            if (WhiteBalanceText(GetUInt(props, "System.Photo.WhiteBalance")) is { } wb)
                exif.Add(new ContentInfoItem("White balance", wb));
            if (ColorSpaceText(GetUInt(props, "System.Image.ColorSpace")) is { } cs)
                exif.Add(new ContentInfoItem("Color space", cs));
        }
        catch
        {
            // EXIF 미지원 포맷(BMP/GIF 등)·손상 파일은 기본 정보만.
        }

        if (exif.Count > 0)
        {
            rows.Add(ContentInfoItem.Separator); // 파일 정보 / 촬영 정보 그룹 구분
            rows.AddRange(exif);
        }
        return rows;
    }

    private static object? Get(IDictionary<string, Windows.Graphics.Imaging.BitmapTypedValue> props, string key) =>
        props.TryGetValue(key, out var v) ? v.Value : null;

    /// <summary>
    /// EXIF 정수 값 안전 변환 — WIC이 키에 따라 Byte/UInt16/UInt32 등으로 boxing하는 폭을
    /// 흡수한다(정확한 폭을 못 박으면 포맷·코덱에 따라 행이 통째로 사라진다).
    /// </summary>
    private static uint? GetUInt(IDictionary<string, Windows.Graphics.Imaging.BitmapTypedValue> props, string key) =>
        Get(props, key) switch
        {
            byte b => b,
            ushort us => us,
            uint u => u,
            short s when s >= 0 => (uint)s,
            int i when i >= 0 => (uint)i,
            _ => null,
        };

    /// <summary>EXIF ExposureProgram → 영어 문구. 미정의 값은 null(행 생략).</summary>
    private static string? ExposureProgramText(uint? v) => v switch
    {
        1 => "Manual",
        2 => "Program",
        3 => "Aperture priority",
        4 => "Shutter priority",
        5 => "Creative",
        6 => "Action",
        7 => "Portrait",
        8 => "Landscape",
        _ => null,
    };

    /// <summary>EXIF MeteringMode → 영어 문구. 미정의 값은 null(행 생략).</summary>
    private static string? MeteringModeText(uint? v) => v switch
    {
        1 => "Average",
        2 => "Center-weighted",
        3 => "Spot",
        4 => "Multi-spot",
        5 => "Pattern",
        6 => "Partial",
        _ => null,
    };

    /// <summary>EXIF WhiteBalance → 영어 문구. 미정의 값은 null(행 생략).</summary>
    private static string? WhiteBalanceText(uint? v) => v switch
    {
        0 => "Auto",
        1 => "Manual",
        _ => null,
    };

    /// <summary>EXIF ColorSpace → 영어 문구. Uncalibrated(0xFFFF)·미정의 값은 행 생략.</summary>
    private static string? ColorSpaceText(uint? v) => v switch
    {
        1 => "sRGB",
        2 => "Adobe RGB",
        _ => null,
    };
}
