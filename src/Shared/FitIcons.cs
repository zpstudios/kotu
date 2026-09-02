using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KOTU.Ui;

/// <summary>
/// A298: 하단 바 Fit 조절기의 <b>원본 배율(Original) 표시</b> 조립을 한곳으로 모은 팩토리.
/// A260(v0.260.0)이 이미지·영상·문서 세 모듈에 <b>같은 코드를 세 벌</b> 복제해 두었던 것을
/// 소스 링크 공유 한 벌로 바꾼 것이다 — 디자인이 모듈마다 갈라질 여지를 구조에서 없앤다.
/// KOTU.Core는 UI 프레임워크 비의존(net8.0)이라 공용 어셈블리에 둘 수 없어,
/// 각 UI 프로젝트가 이 파일 하나를 csproj의 Compile Link로 공유한다
/// (HotkeySupport.cs와 같은 방식 — 어셈블리마다 internal 사본이 생기므로 타입 이름 충돌은 없다.
/// 모듈끼리 서로를 참조하지 않으므로 순환 참조도 생기지 않는다).
///
/// <b>인스턴스 공유 금지(v0.174.1 실사고)</b>: WinUI 요소·Geometry는 부모가 하나뿐이라
/// 만들어 둔 UIElement를 여러 버튼에 물리면 런타임에 앱이 죽는다. 그래서 이 팩토리는
/// 만든 것을 정적 필드에 캐시하지 않고 <b>호출할 때마다 새 인스턴스</b>를 만들어 돌려준다
/// (아래 메서드에 정적 상태가 없다는 것이 그 근거다).
/// Brush는 예외 — 부모가 하나뿐인 것은 Geometry·UIElement이고 Brush는 공유해도 안전하다.
///
/// <b>A310: 판본이 둘이다(본체 상자 · 플라이아웃 아이콘).</b> 같은 뜻의 표시인데도
/// A298이 본체만 이 파일로 모으고 플라이아웃은 각 XAML의 인라인 도형으로 남겨 두어
/// 두 그림이 실제로 달랐다(사용자 보고, 3표면). 하나로 합칠 수는 없다 —
/// <c>MenuFlyoutItem.Icon</c>은 <c>IconElement</c>만 받고 파생이 막혀 있어
/// 본체가 쓰는 <c>Border</c>를 그대로 못 꽂는다. 그래서 <b>같은 치수표를 공유하는 두
/// 판본</b>을 이 파일에 나란히 두고, 디자인을 고칠 곳을 여전히 한 파일로 유지한다.
/// </summary>
internal static class FitIcons
{
    /// <summary>
    /// A260: 원본 배율(Original) 본체 표시 = 테두리 상자 안의 "1:1".
    /// 치수 근거: 32x32 버튼의 내용 칸 26px(BottomBarButtonStyle의 테두리 1 + Padding 2를 뺀 값)
    /// 안에 테두리 2 + 좌우 Padding 4 + 글자 "1:1"(9px에서 대략 13) = 대략 19라 여유가 있다
    /// (실기기에서 잘리면 FontSize 8로 내릴 것). 치수·글자·모서리는 A260 그대로다(회귀 없음).
    ///
    /// A299: 비활성일 때 <b>글자만 회색이 되고 테두리는 정색으로 남던</b> 결함을 고친다.
    /// 원인 = 글자는 브러시를 <b>상속</b>받는다(버튼 템플릿의 Disabled 상태가 ContentPresenter의
    /// Foreground를 ButtonForegroundDisabled로 바꾸면 자식 TextBlock의 실효 Foreground가 따라
    /// 바뀐다). 반면 테두리는 A260이 TextFillColorPrimaryBrush를 <b>한 번 박아 둔</b> 고정값이라
    /// 상태를 따라가지 않았다.
    /// 수리 = 테두리 브러시를 <b>글자의 실효 Foreground와 같은 원천</b>에 묶는다 —
    /// RegisterPropertyChangedCallback으로 글자의 Foreground 변화를 그대로 테두리에 옮긴다
    /// (ArchiveView가 StatusText.Text를 감시하는 것과 같은 관용구).
    /// 매번 원천에서 <b>다시 읽어</b> 대입하므로 활성 → 비활성 → 활성 왕복에서 값이 대칭
    /// 복원된다(직전 값을 어디에도 기억해 두지 않아 누수가 생길 자리가 없다).
    /// 감시 대상이 이 상자 안의 TextBlock이라 상자와 수명이 같다 — 외부·정적 이벤트를 구독하지
    /// 않으므로 해제할 것도 없다.
    /// 하드코딩 회색을 쓰지 않고 버튼이 실제로 쓰는 전경색을 그대로 따라가므로 라이트/다크 양쪽에서
    /// 성립한다. Loaded 한 줄은 트리에 붙는 순간(상속값이 확정되는 시점)의 초기 동기화다.
    /// </summary>
    internal static Border BuildOriginalRatioBox()
    {
        var label = new TextBlock
        {
            Text = "1:1",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // 초기값은 A260 그대로 — 트리에 붙기 전이라 상속 전경색이 아직 확정되지 않은 구간의
            // 값이다. 붙는 즉시 아래 두 줄이 글자와 같은 브러시로 덮는다(A299).
            BorderBrush = OriginalRatioBoxBrush() ?? label.Foreground,
            Child = label,
        };
        label.RegisterPropertyChangedCallback(
            TextBlock.ForegroundProperty,
            (sender, _) => box.BorderBrush = ((TextBlock)sender).Foreground);
        box.Loaded += (_, _) => box.BorderBrush = label.Foreground;
        return box;
    }

    /// <summary>
    /// A310: 같은 "원본 배율" 표시의 <b>플라이아웃 판본</b> — Fit 옵션 플라이아웃의 "Original"
    /// 항목에 꽂는다. 위 본체 상자(Border + 글자)를 <b>도형으로 다시 그린 것</b>이다.
    ///
    /// <b>왜 팩토리를 나눴나</b>: <c>MenuFlyoutItem.Icon</c>의 타입은 <c>IconElement</c>이고
    /// 파생이 막혀 있어(FontIcon·PathIcon·BitmapIcon·ImageIcon·IconSourceElement 뿐)
    /// 본체가 쓰는 <c>Border</c>를 그대로 넣을 수 없다. 본체는 Button이라 Content에 임의
    /// UIElement를 넣을 수 있는 것과 다른 자리다. 그래서 "같은 팩토리 산출물"이 아니라
    /// <b>같은 치수표를 공유하는 두 판본</b>으로 간다.
    ///
    /// <b>A310 이전 도형과 무엇이 달랐나</b>(각 XAML의 인라인 Data 문자열 · A260):
    /// ① 상자가 <b>정사각 14×14</b>였다 — 본체 상자는 대략 18.4×14의 가로로 긴 사각형이다
    /// ② 모서리가 <b>각졌다</b> — 본체는 CornerRadius 2의 둥근 모서리다
    /// ③ 테두리 두께가 <b>1.5</b>였다 — 본체는 1이다
    /// ④ 안쪽 "1" 둘이 <b>민 세로 막대</b>였다 — 본체의 실제 글자 "1"은 왼쪽 위 깃발이 달렸다
    /// ⑤ 콜론 두 점이 글자 <b>한가운데</b> 대칭이었다 — 실제 활자는 아래 점이 밑선에 앉아
    ///    전체가 아래로 치우친다.
    /// 사용자가 "위쪽 1:1과 아래쪽 1:1이 미세하게 다르다"고 본 것이 이 다섯 축이다.
    ///
    /// <b>치수표(본체 실측 → 이 도형)</b>: 본체 상자는 테두리 1×2 + Padding 2×2 + 9px "1:1"
    /// 글자폭 대략 12.4 = 폭 대략 18.4, 줄높이 대략 12 + 테두리 1×2 = 높이 14다(가로세로 대략 1.31:1).
    /// 이 도형의 칸은 <b>16×12</b>(1.33:1)로 그 비율을 옮긴 것이다.
    /// 16을 넘지 않게 잡은 이유 = MenuFlyoutItem의 아이콘 칸이 16이라, 자연 크기가 그 안이면
    /// 축소·잘림 없이 그대로 앉는다(칸이 Viewbox든 고정 폭이든 결과가 같다).
    /// 좌표를 정수·0.05 단위로 맞춰 100% 배율에서 테두리가 픽셀 격자에 떨어진다.
    /// 글자 높이 5.5(칸 높이의 46% — 본체는 대략 45%) · 글자 전체 폭 8.8(칸 폭의 55% — 본체도 대략 55%).
    ///
    /// <b>비활성 회색(A299 축)</b>: 여기엔 A299의 전경 동기 장치가 <b>필요 없다</b>.
    /// 본체의 결함은 테두리(Border.BorderBrush)가 글자의 상속 전경색을 따라가지 않는 데서
    /// 났는데, 이 판본은 테두리도 글자도 한 Geometry의 <b>같은 채움</b>이고 PathIcon의 채움은
    /// Foreground를 그대로 쓴다 — 항목이 비활성이면 템플릿이 아이콘 칸 전경색을 회색으로
    /// 바꾸므로 도형 전체가 함께 회색이 된다.
    ///
    /// <b>인스턴스 공유 금지(v0.174.1)</b>: Geometry도 부모가 하나뿐이다. 이 메서드는 정적
    /// 캐시 없이 호출마다 <b>새 Geometry와 새 PathIcon</b>을 만든다 — 항목마다 따로 부를 것.
    /// </summary>
    internal static PathIcon BuildOriginalRatioIcon()
    {
        var geometry = new PathGeometry { Figures = new PathFigureCollection() };

        // ① 바깥 테두리 = 둥근 사각 링(칸 16×12 · 두께 1 · 모서리 바깥 2/안쪽 1).
        //    바깥 사각은 시계 방향, 안쪽 사각은 반시계 방향으로 감는다 — 짝홀·비영 어느 채움
        //    규칙에서도 가운데가 뚫리므로 FillRule을 지정할 필요가 없다(A260 도형의 근거 승계).
        geometry.Figures.Add(Figure(2, 0,
            LineTo(14, 0), ArcTo(16, 2, 2, SweepDirection.Clockwise),
            LineTo(16, 10), ArcTo(14, 12, 2, SweepDirection.Clockwise),
            LineTo(2, 12), ArcTo(0, 10, 2, SweepDirection.Clockwise),
            LineTo(0, 2), ArcTo(2, 0, 2, SweepDirection.Clockwise)));
        geometry.Figures.Add(Figure(2, 1,
            ArcTo(1, 2, 1, SweepDirection.Counterclockwise),
            LineTo(1, 10), ArcTo(2, 11, 1, SweepDirection.Counterclockwise),
            LineTo(14, 11), ArcTo(15, 10, 1, SweepDirection.Counterclockwise),
            LineTo(15, 2), ArcTo(14, 1, 1, SweepDirection.Counterclockwise)));

        // ② 글자 "1" 둘(왼쪽 x=3.6 · 오른쪽 x=10.0) — 안쪽 여백은 좌우 대칭이다.
        geometry.Figures.Add(DigitOne(3.6));
        geometry.Figures.Add(DigitOne(10.0));

        // ③ 콜론 두 점(1×1) — 아래 점은 글자 밑선 9.25에 앉고 위 점은 x높이 근처(5.3)다.
        //    두 점의 한가운데가 글자 한가운데보다 아래로 내려오는 것이 실제 활자의 모양이라,
        //    본체의 진짜 콜론과 같은 자리에 보인다.
        geometry.Figures.Add(Dot(7.5, 5.3));
        geometry.Figures.Add(Dot(7.5, 8.25));

        return new PathIcon { Data = geometry };
    }

    /// <summary>
    /// 숫자 "1" 하나 — 세로 획(폭 1 · 윗변 3.75에서 밑선 9.25까지) + <b>왼쪽 위 깃발</b>이고
    /// 밑받침은 없다(Segoe UI 숫자 1의 형태). 잉크 폭 2.4로, x는 그 왼쪽 끝이다.
    /// 꼭짓점 순서는 시계 방향 한 바퀴 = 획 윗변 → 오른쪽 → 밑변 → 획 왼쪽(깃발이 붙는
    /// 높이까지) → 깃발 아랫변 → 깃발 끝. 닫으면서 깃발 윗변이 꼭짓점으로 되돌아간다.
    /// </summary>
    private static PathFigure DigitOne(double x) => Figure(x + 1.4, 3.75,
        LineTo(x + 2.4, 3.75),
        LineTo(x + 2.4, 9.25),
        LineTo(x + 1.4, 9.25),
        LineTo(x + 1.4, 4.95),
        LineTo(x, 6.0),
        LineTo(x, 5.1));

    /// <summary>콜론 점 하나 = 1×1 정사각(x·y는 왼쪽 위 모서리) — 호출마다 새 인스턴스.</summary>
    private static PathFigure Dot(double x, double y) => Figure(x, y,
        LineTo(x + 1, y), LineTo(x + 1, y + 1), LineTo(x, y + 1));

    /// <summary>
    /// 닫힌 도형 하나를 만든다(시작점 + 선·호 나열). 컬렉션은 <b>명시적으로</b> 새로 만들어
    /// 대입한다 — WinUI의 도형 컬렉션 속성은 기본값을 가정하지 않는 편이 안전하다
    /// (HardwareView의 <c>new PointCollection()</c>과 같은 관용구).
    /// </summary>
    private static PathFigure Figure(double x, double y, params PathSegment[] segments)
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(x, y),
            IsClosed = true,
            Segments = new PathSegmentCollection(),
        };
        foreach (var segment in segments) figure.Segments.Add(segment);
        return figure;
    }

    /// <summary>직선 한 마디 — 호출마다 새 인스턴스(세그먼트도 부모가 하나뿐이다).</summary>
    private static LineSegment LineTo(double x, double y) => new() { Point = new Point(x, y) };

    /// <summary>
    /// 원호 한 마디(정원 — 가로세로 반지름이 같다). 모서리 하나가 90도라 큰 호가 될 일이
    /// 없으므로 IsLargeArc는 기본값(false) 그대로 둔다.
    /// </summary>
    private static ArcSegment ArcTo(double x, double y, double radius, SweepDirection direction) => new()
    {
        Point = new Point(x, y),
        Size = new Size(radius, radius),
        SweepDirection = direction,
    };

    /// <summary>
    /// A260: 상자 테두리 브러시의 <b>초기</b> 조회. XAML의 ThemeResource 참조는 키가 없을 때 런타임
    /// 파스 실패로 앱이 죽으므로 코드에서 감싸 가져온다(DriveStrip.ThemeBrush와 같은 관용구 —
    /// 인덱서는 키가 없으면 던진다). 실패하면 null을 돌려 호출부가 TextBlock 기본 Foreground를
    /// 쓰게 한다. A299 이후로는 트리에 붙기 전 한순간에만 쓰이는 값이다.
    /// Brush 인스턴스는 공유해도 안전하다(부모가 하나뿐인 것은 Geometry·UIElement).
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
}
