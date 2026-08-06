using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUtil.Core.Contracts;

namespace WinUtil.Module.Image;

/// <summary>
/// 이미지 뷰어 화면. 폴더 내 ←/→ 탐색, 줌/팬, 회전(R), 휴지통 삭제(Delete),
/// 전체화면(F11/더블클릭), 하단 상태바를 제공한다.
/// </summary>
public sealed partial class ImageViewerView : UserControl, IContentStateSource, IContentInfoProvider
{
    /// <summary>이미지를 열거나 ←/→로 바꿀 때 셸에 알린다(v0.25.0 — 탐색기·오버레이 동기화).</summary>
    public event Action<string>? ContentOpened;

    private ImageFolderNavigator? _navigator;
    private int _openSeq;        // OpenPath 경쟁 방지: 느린 폴더 스캔이 최신 열기를 덮지 않게
    private int _userRotation;   // R 키 누적 회전 (0/90/180/270)
    private int _exifRotation;   // EXIF orientation에서 읽은 회전
    private uint _pixelWidth;
    private uint _pixelHeight;

    public ImageViewerView(OpenContext context)
    {
        InitializeComponent();

        // 키 입력을 받기 위해 로드 시 포커스 확보 (IsTabStop은 XAML에서 설정)
        Loaded += (_, _) => Focus(FocusState.Programmatic);

        // 휠 = 이전/다음 (일반 뷰어 관례). ScrollViewer가 휠을 먼저 소비하므로 handledEventsToo로 받는다.
        Scroller.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler(OnScrollerWheel), handledEventsToo: true);

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
        // 폴더 스캔 + 자연 정렬은 대형 폴더·네트워크 드라이브에서 느릴 수 있다 — UI 스레드 밖에서.
        var seq = ++_openSeq;
        ImageFolderNavigator navigator;
        try
        {
            navigator = await Task.Run(() => ImageFolderNavigator.Create(path));
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

    private async Task PickAndOpenAsync()
    {
        // Window 객체 없이 파일 선택기를 띄우려면 XamlRoot 경유로 HWND를 얻어야 한다.
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;
        var hwnd = Win32Interop.GetWindowFromWindowId(environment.AppWindowId);

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in ImageFolderNavigator.SupportedExtensions)
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, hwnd);

        if (await picker.PickSingleFileAsync() is { } file)
            OpenPath(file.Path);
    }

    private void OnOpenButtonClick(object sender, RoutedEventArgs e) => _ = PickAndOpenAsync();
    // 드래그&드롭은 창 수준(MainWindow)에서 확장자 라우팅으로 일괄 처리한다.

    /// <summary>휠: 기본은 이전/다음 탐색. Ctrl+휠(줌)과 확대 상태(팬 스크롤)에서는 개입하지 않는다.</summary>
    private async void OnScrollerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        if (Scroller.ZoomFactor > 1.001f) return;

        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        await MoveAsync(forward: delta < 0);
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
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);

            _pixelWidth = 0;
            _pixelHeight = 0;
            _exifRotation = 0;
            try
            {
                // 해상도와 EXIF orientation 확인. BitmapImage는 EXIF 회전을
                // 자동 반영하지 않으므로 여기서 읽어 RotateTransform에 합산한다.
                var decoder = await BitmapDecoder.CreateAsync(stream);
                _pixelWidth = decoder.PixelWidth;
                _pixelHeight = decoder.PixelHeight;
                _exifRotation = await ReadExifRotationAsync(decoder);
            }
            catch
            {
                // 메타데이터를 못 읽어도 표시는 계속 시도한다.
            }

            stream.Seek(0);
            var bitmap = new BitmapImage(); // GIF 애니메이션은 BitmapImage 기본 지원
            await bitmap.SetSourceAsync(stream);

            ImageControl.Source = bitmap;
            PlaceholderText.Visibility = Visibility.Collapsed;
            _userRotation = 0;
            ApplyRotation();
            Scroller.ChangeView(0, 0, 1.0f, disableAnimation: true); // 줌 초기화(창맞춤)
            UpdateStatusBar();
            ContentOpened?.Invoke(path); // 셸 동기화 (v0.25.0)
        }
        catch (Exception ex)
        {
            ImageControl.Source = null;
            FileNameText.Text = $"Failed to load: {Path.GetFileName(path)} ({ex.Message})";
            InfoText.Text = PositionText();
        }
    }

    /// <summary>EXIF orientation → 시계방향 회전 각도. 미러링 값은 회전만 근사 적용(TODO: 반전 처리).</summary>
    private static async Task<int> ReadExifRotationAsync(BitmapDecoder decoder)
    {
        try
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync(
                new[] { "System.Photo.Orientation" });
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

    /// <summary>창맞춤: 뷰포트 크기로 이미지 레이아웃을 제한(작은 이미지는 원본 크기 유지).</summary>
    private void UpdateFit()
    {
        if (Scroller.ViewportWidth <= 0 || Scroller.ViewportHeight <= 0) return;

        // 90°/270° 회전 시 가로·세로 제한을 맞바꿔 회전 후에도 창에 들어오게 한다.
        var swapped = (_exifRotation + _userRotation) % 180 != 0;
        ImageControl.MaxWidth = swapped ? Scroller.ViewportHeight : Scroller.ViewportWidth;
        ImageControl.MaxHeight = swapped ? Scroller.ViewportWidth : Scroller.ViewportHeight;
    }

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
            return;
        }

        FileNameText.Text = Path.GetFileName(path);
        var resolution = _pixelWidth > 0 ? $"{_pixelWidth}×{_pixelHeight}  ·  " : string.Empty;
        InfoText.Text = resolution + PositionText();
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

    private void OnRotateInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RotateClockwise();
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

    // ---------- Ctrl 정보 오버레이 (v0.25.0) ----------

    /// <summary>파일·해상도 + EXIF(촬영일·카메라·노출·조리개·ISO·초점거리). 미지원 포맷은 기본 정보만.</summary>
    public async Task<string?> GetContentInfoAsync()
    {
        var path = _navigator?.Current;
        if (path is null) return null;

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
        if (_pixelWidth > 0)
            lines.Add($"{_pixelWidth}×{_pixelHeight} px");

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var props = await decoder.BitmapProperties.GetPropertiesAsync(new[]
            {
                "System.Photo.DateTaken", "System.Photo.CameraManufacturer",
                "System.Photo.CameraModel", "System.Photo.ExposureTime",
                "System.Photo.FNumber", "System.Photo.ISOSpeed", "System.Photo.FocalLength",
            });

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
