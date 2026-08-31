using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
