using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace KOTU.Ui;

/// <summary>
/// A300: 하단 바 이퀄라이저·비주얼라이저 버튼의 <b>코드 조립 아이콘</b> 팩토리.
/// 글리프(EQ = E9E9 / 비주얼라이저 = E8D6)가 기능을 드러내지 못한다는 사용자 보고로,
/// A260 계보의 코드 조립 도형(렌더를 산술로 통제 — 글리프 코드포인트 실재 여부에 안 걸린다)으로
/// 교체한다. FitIcons(A298)와 같은 소스 링크 공유 파일이다 — 현재 사용처는 오디오 모듈뿐이지만
/// (영상 하단 바에는 EQ·비주얼라이저 버튼이 없다 — 우측군은 자막·Fit), 디자인의 정본을
/// 한곳에 두어 훗날 다른 모듈이 얻어도 갈라질 여지를 없앤다.
///
/// <b>인스턴스 공유 금지(v0.174.1 실사고)</b>: WinUI 요소는 부모가 하나뿐이라 만들어 둔
/// UIElement를 여러 버튼에 물리면 런타임에 앱이 죽는다. 그래서 이 팩토리도 FitIcons처럼
/// 정적 캐시 없이 <b>호출할 때마다 새 인스턴스</b>를 만들어 돌려준다.
///
/// <b>비활성 회색(A299 관용구)</b>: Rectangle의 Fill은 Foreground 상속을 받지 않아,
/// 한 번 박은 브러시는 버튼의 Disabled 상태를 따라가지 않는다(A299에서 고친 결함과 같은 축).
/// 그래서 도형 옆에 상속을 받는 빈 TextBlock(0×0 — 레이아웃 무영향)을 심고, 그 실효
/// Foreground 변화를 RegisterPropertyChangedCallback으로 모든 막대 Fill에 옮긴다
/// (FitIcons.BuildOriginalRatioBox가 테두리 브러시에 쓰는 것과 같은 관용구 — 매번 원천에서
/// 다시 읽으므로 활성↔비활성 왕복에서 대칭 복원되고, 감시 대상이 아이콘 내부 요소라 수명이
/// 같아 해제할 것도 없다). 하드코딩 색이 없으므로 라이트/다크 양쪽에서 성립한다.
/// </summary>
internal static class MediaIcons
{
    /// <summary>
    /// A300: 이퀄라이저 = <b>세로 슬라이더 3개</b>(트랙 막대 3 + 노브 높이 상이) — 믹서/EQ의
    /// 보편 도상. 16×16 칸(32×32 버튼의 내용 칸 26px 안 — FontSize 18 글리프 상당) 좌표:
    /// 트랙은 폭 2·높이 14(y=1..15), x = 2·7·12(중심 3·8·13 — 전체가 x=2..14로 칸 중앙).
    /// 노브는 폭 6·높이 3, 트랙 중심 정렬(x = 0·5·10), y = 8(낮음)·2(높음)·5(중간) —
    /// 높이가 서로 달라야 "조절 중인 밴드"로 읽힌다. 전부 축정렬 사각형이라 저해상도에서
    /// 뭉개지지 않는다(A260 도형과 같은 근거).
    /// </summary>
    internal static Grid BuildEqualizerIcon()
    {
        return Assemble(new[]
        {
            Bar(2, 1, 2, 14), Bar(7, 1, 2, 14), Bar(12, 1, 2, 14),
            Bar(0, 8, 6, 3), Bar(5, 2, 6, 3), Bar(10, 5, 6, 3),
        });
    }

    /// <summary>
    /// A300: 비주얼라이저 = <b>높이가 다른 세로 막대 5개</b>(스펙트럼 바) — 음파의 그래프화라는
    /// 보편 도상. 16×16 칸 좌표: 막대 폭 2·간격 1(x = 1·4·7·10·13 — 전체 x=1..15로 칸 중앙),
    /// 밑변은 y=15로 정렬(그래프의 바닥축), 높이 5·9·13·7·10(y = 10·6·2·8·5) —
    /// 단조 증가가 아니라 오르내리는 배열이라 정적 그래프가 아닌 스펙트럼으로 읽힌다.
    /// </summary>
    internal static Grid BuildVisualizerIcon()
    {
        return Assemble(new[]
        {
            Bar(1, 10, 2, 5), Bar(4, 6, 2, 9), Bar(7, 2, 2, 13),
            Bar(10, 8, 2, 7), Bar(13, 5, 2, 10),
        });
    }

    /// <summary>
    /// 막대들을 16×16 Canvas에 앉히고 전경 동기 장치(위 A299 관용구)를 붙여 완성한다.
    /// 초기 Fill은 InitialIconBrush() — 트리에 붙기 전 상속 전경색이 확정되지 않은 구간의
    /// 값이고, 붙는 즉시 Loaded 동기화가 센티널과 같은 브러시로 덮는다(FitIcons와 동일 구조).
    /// Brush 인스턴스 공유는 안전하다(부모가 하나뿐인 것은 Geometry·UIElement — FitIcons 주석).
    /// </summary>
    private static Grid Assemble(Rectangle[] bars)
    {
        // 상속 전경색 센티널: Rectangle은 Foreground가 없어 상속을 못 받으므로, 받는 요소를
        // 하나 심어 원천으로 쓴다. 0×0이라 레이아웃·렌더에 영향이 없고 Visible이라 상속은 산다.
        var sentinel = new TextBlock { Text = string.Empty, Width = 0, Height = 0 };
        var canvas = new Canvas { Width = 16, Height = 16 };
        var initial = InitialIconBrush() ?? sentinel.Foreground;
        foreach (var bar in bars)
        {
            bar.Fill = initial;
            canvas.Children.Add(bar);
        }
        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(sentinel);
        root.Children.Add(canvas);
        sentinel.RegisterPropertyChangedCallback(
            TextBlock.ForegroundProperty,
            (sender, _) =>
            {
                foreach (var bar in bars) bar.Fill = ((TextBlock)sender).Foreground;
            });
        root.Loaded += (_, _) =>
        {
            foreach (var bar in bars) bar.Fill = sentinel.Foreground;
        };
        return root;
    }

    /// <summary>Canvas 좌표(x·y)와 크기(w·h)로 막대 하나를 만든다 — 호출마다 새 인스턴스.</summary>
    private static Rectangle Bar(double x, double y, double w, double h)
    {
        var rect = new Rectangle { Width = w, Height = h };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        return rect;
    }

    /// <summary>
    /// 아이콘 초기 브러시 — FitIcons.OriginalRatioBoxBrush와 같은 관용구(ThemeResource를 XAML
    /// 참조로 걸면 키가 없을 때 런타임 파스 실패로 앱이 죽으므로 코드에서 감싸 가져오고,
    /// 실패하면 null을 돌려 호출부가 TextBlock 기본 Foreground를 쓰게 한다).
    /// A299 동기화 이후로는 트리에 붙기 전 한순간에만 쓰이는 값이다.
    /// </summary>
    private static Brush? InitialIconBrush()
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
}
