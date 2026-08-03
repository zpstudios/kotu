using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinUtil.Core.Contracts;

namespace WinUtil.Module.Image;

/// <summary>
/// 이미지 뷰어 화면. 폴더 내 ←/→ 탐색, 줌/팬, 회전(R), 휴지통 삭제(Delete),
/// 전체화면(F11/더블클릭), 하단 상태바를 제공한다.
/// </summary>
public sealed partial class ImageViewerView : UserControl
{
    private ImageFolderNavigator? _navigator;
    private int _userRotation;   // R 키 누적 회전 (0/90/180/270)
    private int _exifRotation;   // EXIF orientation에서 읽은 회전
    private uint _pixelWidth;
    private uint _pixelHeight;

    public ImageViewerView(OpenContext context)
    {
        InitializeComponent();

        // 키 입력을 받기 위해 로드 시 포커스 확보 (IsTabStop은 XAML에서 설정)
        Loaded += (_, _) => Focus(FocusState.Programmatic);

        if (context.FilePath is { } path && File.Exists(path))
        {
            _navigator = ImageFolderNavigator.Create(path);
            _ = LoadCurrentAsync();
        }
        else
        {
            PlaceholderText.Visibility = Visibility.Visible;
            UpdateStatusBar();
        }
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
        }
        catch (Exception ex)
        {
            ImageControl.Source = null;
            FileNameText.Text = $"로드 실패: {Path.GetFileName(path)} ({ex.Message})";
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
            FileNameText.Text = $"삭제 실패: {Path.GetFileName(path)} ({ex.Message})";
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
            FileNameText.Text = "열린 파일 없음";
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

    private void OnRotateButtonClick(object sender, RoutedEventArgs e) => RotateClockwise();
}
