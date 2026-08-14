using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using KOTU.Core.Contracts;
using KOTU.Core.Threading;
using KOTU.Input;

namespace KOTU.Module.Image;

/// <summary>
/// 이미지 뷰어 화면. 폴더 내 ←/→ 탐색, 줌/팬, 회전(R), 휴지통 삭제(Delete),
/// 전체화면(F11/더블클릭), 하단 상태바를 제공한다.
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
    /// 전체화면은 F11/더블클릭/⛶ 버튼(v0.29.0 — Enter는 A86/v0.121.0에서 셸 오버레이 일괄 토글로 이관).
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
    private string _metaText = string.Empty; // A9: 용량 · 종류(확장자·비트뎁스) · EXIF 요약
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

    private ScrollContentPresenter? _zoomPresenter; // 휠 가로채기 지점 (Scroller 템플릿 로드 후 탐색)

    /// <summary>
    /// ScrollViewer 콘텐츠 프레젠터에 휠 핸들러를 단다. 버블 순서상 프레젠터가
    /// ScrollViewer보다 먼저 이벤트를 받으므로 여기서 Handled 처리하면 내장 Ctrl+휠 줌 대신
    /// 우리 수동 줌(노치당 10%)이 일관되게 적용된다. 핀치 줌은 ZoomMode=Enabled 그대로 유지된다.
    /// 뷰어 콘텐츠 위에서만 동작한다는 규칙(리스트/그리드 무동작 — A84에서 확립,
    /// A98도 유지)은 배선 지점 자체가 보장한다.
    /// </summary>
    private void HookZoomWheel()
    {
        if (_zoomPresenter is not null) return; // Loaded 재진입(전체화면 왕복 등) 중복 배선 방지
        _zoomPresenter = FindPresenter(Scroller);
        if (_zoomPresenter is not null)
            _zoomPresenter.PointerWheelChanged += OnContentWheel;
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
            var (data, width, height, exifRotation, meta) = await Worker.Run(_ => ReadImageFile(path));

            _pixelWidth = width;
            _pixelHeight = height;
            _exifRotation = exifRotation;
            _metaText = meta;

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
            InfoText.Text = PositionText();
            _metaText = string.Empty; // 이전 이미지 메타가 남지 않게
            MetaText.Text = string.Empty;
            ToolTipService.SetToolTip(MetaText, null);
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
        var (png, width, height, meta) = await Worker.Run(_ =>
        {
            using var magick = new ImageMagick.MagickImage(path);
            // A9: 비트뎁스 = 채널당 비트 × 채널 수 (Q8 빌드라 채널당 8비트 상한 — 근사값)
            var bitDepth = (uint)(magick.Depth * magick.ChannelCount);
            var metaText = string.Join("  ·  ",
                FormatSize(new FileInfo(path).Length),
                FormatKind(path, bitDepth));
            return (magick.ToByteArray(ImageMagick.MagickFormat.Png), magick.Width, magick.Height, metaText);
        });

        if (_navigator?.Current != path) return; // 그새 다른 파일로 이동함

        _pixelWidth = width;
        _pixelHeight = height;
        _exifRotation = 0; // Magick 디코드 경로에서는 EXIF 회전을 별도 적용하지 않는다
        _metaText = meta;
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
    /// 워커 스레드: 파일 전체를 읽고 WIC로 해상도·EXIF 회전과 하단 바 메타 요약(A9 —
    /// 용량·종류(확장자·비트뎁스)·EXIF 요약)을 뽑는다. WinRT 비동기는
    /// 전용 스레드라 동기 대기해도 UI 교착이 없다. 메타데이터 실패는 0으로 두고 표시는 계속.
    /// </summary>
    private static (byte[] Data, uint Width, uint Height, int ExifRotation, string Meta) ReadImageFile(string path)
    {
        var data = File.ReadAllBytes(path);
        uint width = 0, height = 0;
        var exifRotation = 0;
        var meta = new List<string> { FormatSize(data.LongLength) };
        try
        {
            using var stream = new MemoryStream(data).AsRandomAccessStream();
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            width = decoder.PixelWidth;
            height = decoder.PixelHeight;
            exifRotation = ReadExifRotation(decoder);
            meta.Add(FormatKind(path, ReadBitDepth(decoder)));
            if (ReadExifSummary(decoder) is { Length: > 0 } exif)
                meta.Add(exif);
        }
        catch
        {
            // 메타데이터를 못 읽어도 표시는 계속 시도한다 — 용량·확장자만이라도 보여준다.
            meta.Add(FormatKind(path, 0));
        }
        return (data, width, height, exifRotation, string.Join("  ·  ", meta));
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

    private void SetFitMode(FitMode mode)
    {
        _fitMode = mode;
        Scroller.ChangeView(0, 0, 1.0f, disableAnimation: true); // 줌 초기화 후 모드 적용
        UpdateFit();
    }

    /// <summary>
    /// A30 규격: Fit 버튼 본체 내용(1:1 텍스트 / Contain·좌우·상하 아이콘)과 툴팁을 마지막 옵션에 맞춘다.
    /// 100%는 검증된 글리프가 없어 기존 1:1 버튼의 표기(FontSize 13 텍스트)를 그대로 승계한다.
    /// </summary>
    private void UpdateFitButton()
    {
        (object content, string tip) = _lastFitOption switch
        {
            FitMode.FitWidth =>
                ((object)new FontIcon { Glyph = "\uE8AB", FontSize = 18 }, "Fit width"),
            FitMode.FitHeight =>
                (new FontIcon { Glyph = "\uE8CB", FontSize = 18 }, "Fit height"),
            FitMode.ActualSize => (new TextBlock
            {
                Text = "1:1",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, "Actual size"),
            _ => (new FontIcon { Glyph = "\uE9A6", FontSize = 18 },
                "Contain - the whole image fits, never enlarged"),
        };
        FitButton.Content = content;
        ToolTipService.SetToolTip(FitButton, FitTip(tip)); // A34: 표기는 키 상수에서
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

    /// <summary>A30 규격: 본체 클릭 = 버튼에 표시된 마지막 옵션 재적용.</summary>
    private void OnFitClick(SplitButton sender, SplitButtonClickEventArgs args) => SetFitMode(_lastFitOption);

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
            InfoText.Text = string.Empty;
            MetaText.Text = string.Empty;
            ToolTipService.SetToolTip(MetaText, null);
            return;
        }

        FileNameText.Text = Path.GetFileName(path);
        // A9: 용량·종류(확장자·비트뎁스)·EXIF 요약. 좁으면 말줄임되므로 전체는 툴팁으로.
        MetaText.Text = _metaText;
        ToolTipService.SetToolTip(MetaText, _metaText.Length > 0 ? _metaText : null);
        var resolution = _pixelWidth > 0 ? $"{_pixelWidth}×{_pixelHeight}  ·  " : string.Empty;
        InfoText.Text = resolution + PositionText();
        // A22(v0.108.0): 드라이브 표시는 파일이 열려 있을 때가 아니라 없을 때만 —
        // 여기서 조회하던 v0.47.0 텍스트는 셸이 주입하는 공용 드라이브 줄로 대체됐다.
    }

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

    private void OnFullScreenInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullScreen();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ToggleFullScreen();
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsFullScreen()) return; // 전체화면이 아닐 땐 Esc에 개입하지 않는다
        args.Handled = true;
        ToggleFullScreen();
    }

    private bool IsFullScreen()
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return false;
        return AppWindow.GetFromWindowId(environment.AppWindowId).Presenter.Kind
            == AppWindowPresenterKind.FullScreen;
    }

    private void OnRotateButtonClick(object sender, RoutedEventArgs e) => RotateClockwise();

    private void OnFullScreenButtonClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

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

    /// <summary>파일·해상도 + EXIF(촬영일·카메라·노출·조리개·ISO·초점거리). 미지원 포맷은 기본 정보만.</summary>
    public async Task<string?> GetContentInfoAsync()
    {
        var path = _navigator?.Current;
        if (path is null) return null;

        // 파일 크기·EXIF 조회는 파일 I/O — 워커에서 만들어 결과 문자열만 받는다(A42).
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

    /// <summary>워커 스레드: 정보 오버레이 텍스트 구성(WinRT 비동기는 동기 대기 — 전용 스레드).</summary>
    private static string BuildContentInfo(string path, uint pixelWidth, uint pixelHeight)
    {
        var lines = new List<string> { Path.GetFileName(path) };
        try
        {
            var info = new FileInfo(path);
            lines.Add($"{info.Length / 1024.0 / 1024.0:0.##} MB · {info.LastWriteTime:yyyy-MM-dd HH:mm}");
        }
        catch
        {
            // 크기·날짜는 없어도 된다.
        }
        if (pixelWidth > 0)
            lines.Add($"{pixelWidth}×{pixelHeight} px");

        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            using var stream = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            var props = decoder.BitmapProperties.GetPropertiesAsync(new[]
            {
                "System.Photo.DateTaken", "System.Photo.CameraManufacturer",
                "System.Photo.CameraModel", "System.Photo.ExposureTime",
                "System.Photo.FNumber", "System.Photo.ISOSpeed", "System.Photo.FocalLength",
            }).AsTask().GetAwaiter().GetResult();

            if (Get(props, "System.Photo.DateTaken") is DateTimeOffset taken)
                lines.Add($"Taken {taken.LocalDateTime:yyyy-MM-dd HH:mm}");

            var maker = Get(props, "System.Photo.CameraManufacturer") as string;
            var model = Get(props, "System.Photo.CameraModel") as string;
            if (!string.IsNullOrWhiteSpace(maker) || !string.IsNullOrWhiteSpace(model))
                lines.Add($"{maker} {model}".Trim());

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
                lines.Add(string.Join(" · ", exposure));
        }
        catch
        {
            // EXIF 미지원 포맷(BMP/GIF 등)·손상 파일은 기본 정보만.
        }

        return string.Join("\n", lines);
    }

    private static object? Get(IDictionary<string, Windows.Graphics.Imaging.BitmapTypedValue> props, string key) =>
        props.TryGetValue(key, out var v) ? v.Value : null;
}
