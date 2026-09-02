using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace KOTU.Ui;

/// <summary>
/// A300: 하단 바 버튼의 <b>코드 조립 아이콘</b> 팩토리.
/// 글리프(EQ = E9E9 / 비주얼라이저 = E8D6)가 기능을 드러내지 못한다는 사용자 보고로,
/// A260 계보의 코드 조립 도형(렌더를 산술로 통제 — 글리프 코드포인트 실재 여부에 안 걸린다)으로
/// 교체한다. FitIcons(A298)와 같은 소스 링크 공유 파일이다 — 디자인의 정본을
/// 한곳에 두어 소비처가 늘어도 갈라질 여지를 없앤다.
/// 사용처: 오디오 모듈(EQ·비주얼라이저 — 영상 하단 바에는 그 두 버튼이 없다. 우측군은 자막·Fit)과
/// <b>셸 하단 바</b>(A305 배치 2의 사이드바 접기 + A312의 전체화면 해제 — 1개 3상태 모드 버튼의
/// 얼굴 ⓐ·ⓒ. KOTU.App도 이 파일을 소스 링크로 받는다)와
/// <b>영상·오디오 하단 바의 루프 버튼 "반복 안함" 얼굴</b>(A318 — 글리프 위에 금지 사선을
/// 얹는 합성이라 순수 도형 조립은 아니지만, 디자인 정본을 한 파일에 두는 이유는 같다).
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
    /// A305(배치 2): 사이드바 접힘 = <b>창 테두리 + 양옆으로 밀려난 굵은 세로띠 + 넓은 중앙 블록</b>.
    /// 셸 하단 바의 "Hide side panels" 버튼(모드1 → 모드2) 아이콘이다 — 좌/우 패널이 화면
    /// 가장자리로 접혀 사라지고 콘텐츠가 가운데를 전부 가져간 모습을 그린다.
    /// 글리프를 쓰지 않는 이유: 저장소에 사이드바·패널 계열 글리프 사용례가 0건이라(현재 쓰는
    /// 것은 E700·E740·E76B/E76C·E8E5 계열뿐) 코드포인트 실재를 증빙할 수 없다 — A300과 같은 판단.
    /// 16×16 칸(32×32 버튼의 내용 칸 26px = 테두리 1×2 + Padding 2×2를 뺀 값 안) 좌표:
    /// 창은 x = 0..16 · y = 2..14(칸 높이 12 — 위아래 여백 2로 세로 중앙),
    /// 위/아래 테두리는 두께 1(y = 2·13), 접힌 좌/우 패널은 두께 <b>2</b>의 세로띠(x = 0·14)라
    /// 가로 테두리보다 굵어 "얇게 눌린 패널"로 읽힌다. 중앙 콘텐츠 블록은 x = 4..12 · y = 5..11
    /// (좌우 여백 2·상하 여백 2로 완전 대칭). 전부 축정렬 정수 좌표 사각형이라 저해상도에서
    /// 뭉개지지 않는다(A260·A300 도형과 같은 근거).
    /// </summary>
    internal static Grid BuildSidePanelsHiddenIcon()
    {
        return Assemble(new[]
        {
            Bar(0, 2, 16, 1), Bar(0, 13, 16, 1), // 창 위/아래 테두리
            Bar(0, 2, 2, 12), Bar(14, 2, 2, 12), // 가장자리로 접힌 좌/우 패널
            Bar(4, 5, 8, 6),                     // 가운데를 다 차지한 콘텐츠
        });
    }

    /// <summary>
    /// A312: 전체화면 해제 = <b>겹친 창 2개</b>(앞 창의 빈 테두리 + 뒤로 물러난 이전 크기 창의
    /// 노출부) — Windows 캡션의 "이전 크기로 복원"과 같은 보편 도상이라 "화면을 꽉 채운 상태에서
    /// 창으로 돌아간다"로 읽힌다. 셸 모드 버튼의 얼굴 ⓒ(Exit full screen — 전체화면에서만)이다.
    /// 글리프를 쓰지 않는 이유: E740(FullScreen)의 짝 E73F(BackToWindow)는 저장소 사용례가
    /// 0건이라 코드포인트 실재를 증빙할 수 없다(A300·A305 배치 2의 확정 판단 — 이 파일의 다른
    /// 아이콘들과 같은 근거). 화살표 계열 도상은 대각선이 필요해 축정렬 사각형 조립으로는
    /// 성립하지 않는다 — 겹친 창 도상은 전부 직선이라 이 팩토리 문법에 맞는다.
    /// 16×16 칸 좌표(전부 축정렬 정수 — 저해상도에서 뭉개지지 않는다):
    /// 앞 창(현재로 돌아올 창)은 x = 1..11 · y = 5..15의 10×10 빈 테두리(두께 1 × 4변).
    /// 뒤 창(밀려나 있던 이전 화면)은 x = 5..15 · y = 1..11의 10×10 중 앞 창에 가리지 않는
    /// 노출부만: 위 변 전체(y=1) + 오른 변 전체(x=14) + 왼 변 위 토막(y=1..5) + 아래 변
    /// 오른 토막(x=11..15) — 네 토막이 이어져 뒤 창 윤곽으로 읽힌다.
    /// </summary>
    internal static Grid BuildExitFullScreenIcon()
    {
        return Assemble(new[]
        {
            Bar(1, 5, 10, 1), Bar(1, 14, 10, 1), // 앞 창 위/아래 변
            Bar(1, 5, 1, 10), Bar(10, 5, 1, 10), // 앞 창 왼/오른 변
            Bar(5, 1, 10, 1), Bar(14, 1, 1, 10), // 뒤 창 위 변·오른 변
            Bar(5, 1, 1, 4), Bar(11, 10, 4, 1),  // 뒤 창 왼 변 토막·아래 변 토막
        });
    }

    /// <summary>
    /// A318: 루프 <b>"반복 안함"</b> 얼굴 = <b>반복 글리프 + 그 위에 겹친 금지 사선</b>.
    /// 종전 표지는 같은 글리프를 Opacity 0.4로 흐리게 그린 것이라 사용자가 "이 버튼 지금
    /// 못 누르나?"로 읽었다(실기기 보고). 그래서 <b>밝기·전경색은 다른 두 상태와 똑같이 두고</b>
    /// (= 활성으로 보인다) 뜻만 형상으로 옮긴다.
    /// 글리프를 통째로 도형으로 다시 그리지 않는 이유: 목록 루프(E8EE)·한 파일 루프(E8ED)가
    /// FontIcon이라 선 굵기·비율이 어긋난다. 세 상태가 <b>같은 도상 가족</b>으로 남아야 서로
    /// 비교돼 읽히므로, 호출부가 쓰는 그 글리프를 인자로 받아 그대로 얹고 사선만 더한다.
    ///
    /// <b>사선을 Path 채움 사각띠로 그리는 이유</b>: RotateTransform은 렌더 변환이라 저해상도에서
    /// 위치가 픽셀 격자에서 밀리고, 회전 중심 계산이 글꼴 크기에 얽힌다. 대신 FitIcons와 같은
    /// PathGeometry 조립(닫힌 네 꼭짓점 = 기울어진 띠)으로 좌표를 <b>산술로 확정</b>한다.
    /// 대각선이라 축정렬은 불가능하지만(A260·A300 도형과 다른 점), 띠 두께를 <b>2</b>로 잡아
    /// 글리프 획(18px 기준 대략 1.3)보다 굵게 만들어 16px대에서도 사선이 먼저 읽히게 했다.
    /// 좌표는 17.4×17.4 칸의 (0.7,0.7)→(16.7,16.7) 중심선에 두께 2를 수직으로 편 것이다
    /// (왼쪽 위 → 오른쪽 아래 — Segoe 계열 "끔" 표지의 방향). 글리프 잉크보다 살짝 넘치게
    /// 뻗어 "위에 그은 선"으로 읽힌다.
    ///
    /// <b>전경 동기(A299 관용구)</b>: Path.Fill도 Rectangle.Fill과 같아 상속을 못 받으므로
    /// 이 파일의 센티널 장치를 그대로 쓴다(버튼이 비활성이면 사선도 함께 회색이 된다).
    /// FontIcon 쪽은 Foreground 상속이 살아 있어 손댈 것이 없다.
    ///
    /// <b>인스턴스 공유 금지(v0.174.1 실사고)</b>: 호출마다 새 Grid·새 FontIcon·새 Path·
    /// <b>새 Geometry</b>를 만든다(정적 캐시 없음). A255 당시 "빗금 도형은 Geometry 공유 크래시
    /// 함정이라 기각"으로 남아 있던 판단은 <b>공유</b>가 원인이었고, 이 팩토리처럼 매번 새로
    /// 조립하면 성립한다 — 상태가 바뀔 때마다 UpdateLoopButton이 이 메서드를 다시 부른다.
    /// </summary>
    /// <param name="glyph">목록 루프 상태가 쓰는 것과 같은 반복 글리프(도상 가족 유지).</param>
    internal static Grid BuildLoopOffIcon(string glyph)
    {
        // 상속 전경색 센티널 — Assemble과 같은 이유·같은 관용구(0×0이라 레이아웃 무영향).
        var sentinel = new TextBlock { Text = string.Empty, Width = 0, Height = 0 };
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = LoopGlyphFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var slash = new Path
        {
            Data = BuildSlashGeometry(),
            Stretch = Stretch.None, // 도형 좌표 그대로 — 칸에 맞춰 늘리면 두께가 흐트러진다
            Fill = InitialIconBrush() ?? sentinel.Foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(sentinel);
        root.Children.Add(icon);
        root.Children.Add(slash); // 마지막 = 글리프 위에 그려진다
        sentinel.RegisterPropertyChangedCallback(
            TextBlock.ForegroundProperty,
            (sender, _) => slash.Fill = ((TextBlock)sender).Foreground);
        root.Loaded += (_, _) => slash.Fill = sentinel.Foreground;
        return root;
    }

    /// <summary>
    /// 루프 글리프의 글꼴 크기 — 하단 바 세 상태가 같은 값이어야 크기가 튀지 않는다
    /// (호출부 XAML·UpdateLoopButton의 FontSize 18과 같은 값).
    /// </summary>
    private const double LoopGlyphFontSize = 18;

    /// <summary>
    /// 금지 사선 한 줄 = 닫힌 네 꼭짓점의 기울어진 띠(호출마다 새 Geometry — 부모가 하나뿐이다).
    /// 중심선 (0.7,0.7)→(16.7,16.7)에 두께 2를 수직(법선 (1,-1)/루트2 방향으로 ±0.7)으로 편 값이라
    /// 네 꼭짓점이 (0,1.4)·(1.4,0)·(17.4,16)·(16,17.4)가 된다 — 경계 상자는 17.4 정사각이다.
    /// </summary>
    private static PathGeometry BuildSlashGeometry()
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(0, 1.4),
            IsClosed = true,
            Segments = new PathSegmentCollection(),
        };
        figure.Segments.Add(new LineSegment { Point = new Point(1.4, 0) });
        figure.Segments.Add(new LineSegment { Point = new Point(17.4, 16) });
        figure.Segments.Add(new LineSegment { Point = new Point(16, 17.4) });
        var geometry = new PathGeometry { Figures = new PathFigureCollection() };
        geometry.Figures.Add(figure);
        return geometry;
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
