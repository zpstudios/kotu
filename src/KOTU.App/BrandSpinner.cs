using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace KOTU.App;

/// <summary>
/// 로딩 인디케이터 ⑤ (A79, v0.119.0). 브랜드 스피너가 켜져 있으면 발바닥 배지를 계속 회전시키고,
/// 꺼져 있거나 에셋이 없으면 <b>지금까지의 <see cref="ProgressRing"/> 그대로</b>다 —
/// 호출부는 어느 쪽인지 알 필요 없이 <see cref="Element"/>를 화면에 넣고
/// <see cref="SetActive"/>만 부른다.
///
/// 회전은 DriveStrip 마퀴와 같은 관용구(Storyboard + DoubleAnimation, EnableDependentAnimation).
/// 애니메이션이 시작되지 않아도 정지된 발바닥이 보일 뿐 기능에는 영향이 없다.
///
/// 적용 위치는 <b>한 곳</b>이다(요구: "살짝씩") — 설정 화면의 파일 연결 토글 진행 표시(A77).
/// 하드웨어 뷰의 첫 로드 링은 현행 유지.
/// </summary>
internal sealed class BrandSpinner
{
    /// <summary>화면에 넣을 요소 — 발바닥 스피너이거나 기본 진행 링이다.</summary>
    public FrameworkElement Element { get; }

    private readonly ProgressRing? _ring;
    private readonly Storyboard? _spin;

    private BrandSpinner(FrameworkElement element, ProgressRing? ring, Storyboard? spin)
    {
        Element = element;
        _ring = ring;
        _spin = spin;
    }

    /// <summary>지정한 한 변 크기의 인디케이터를 만든다.</summary>
    public static BrandSpinner Create(double size)
    {
        if (BrandAssets.IsEnabled(BrandPoint.PawSpinner)
            && BrandAssets.TryGetAsset("spinner.png") is { } path)
        {
            try
            {
                return CreatePaw(path, size);
            }
            catch
            {
                // 깨진 PNG 등 — 조용히 기본 링으로 떨어진다
            }
        }

        var ring = new ProgressRing
        {
            Width = size,
            Height = size,
            IsActive = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new BrandSpinner(ring, ring, null);
    }

    private static BrandSpinner CreatePaw(string path, double size)
    {
        var rotate = new RotateTransform();
        var image = new Image
        {
            Source = new BitmapImage(new Uri(path)),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = rotate,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(1.2)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, rotate);
        Storyboard.SetTargetProperty(animation, "Angle");

        var spin = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        spin.Children.Add(animation);
        return new BrandSpinner(image, null, spin);
    }

    /// <summary>도는 중 / 멈춤. 표시 여부는 호출부가 Element.Visibility로 정한다.</summary>
    public void SetActive(bool active)
    {
        if (_ring is not null)
        {
            _ring.IsActive = active;
            return;
        }
        try
        {
            if (active) _spin?.Begin();
            else _spin?.Stop();
        }
        catch
        {
            // 애니메이션 시작 실패는 치명적이지 않다 — 정지된 그림으로 남는다
        }
    }
}
