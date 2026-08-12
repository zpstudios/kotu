using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace KOTU.Input;

/// <summary>
/// A34(v0.112.0): 버튼 핫키 공용 지원 — 키 등록 · 툴팁 "(키)" 표기 · 입력 통과 판정이 한곳에 모인다.
/// KOTU.Core는 UI 프레임워크 비의존(net8.0)이라 공용 어셈블리에 둘 수 없어,
/// 각 UI 프로젝트가 이 파일 하나를 csproj의 Compile Link로 공유한다
/// (어셈블리마다 internal 사본이 생기므로 타입 이름 충돌은 없다).
///
/// 통과 규칙은 A32/A84가 이미 쓰던 판정을 그대로 재사용한다:
/// ① 텍스트 입력 컨트롤에 포커스가 있으면 단독 문자 키를 삼키지 않는다(에디터 타이핑 우선).
/// ② A34가 하나 더한다 — 탐색기 파일 리스트(셸이 PassThroughTag를 건 표면)에 포커스가 있으면
///    리스트의 타이핑 탐색(첫 글자 점프)이 우선이라 역시 삼키지 않는다.
/// 셸의 모듈 전환 키(A32의 숫자·`)는 ①만 적용한다 — 파일 리스트를 보면서도 모듈은 바꿀 수 있어야 한다.
/// </summary>
internal static class HotkeySupport
{
    /// <summary>
    /// 핫키를 삼키면 안 되는 표면 표시값. 셸이 탐색기 아이콘 그리드 · 리스트 · 폴더 트리에 Tag로 건다.
    /// 모듈은 이 값을 직접 읽지 않고 ShouldPassThrough를 통해서만 본다.
    /// </summary>
    internal const string PassThroughTag = "kotu.hotkey.passthrough";

    /// <summary>비주얼 트리 상향 탐색 상한 — 비정상 트리에서의 무한 순회 방어.</summary>
    private const int MaxAncestorDepth = 64;

    /// <summary>툴팁 표기 규칙(A34): "설명 (키)". 표기를 키에서 만들어 둘이 어긋날 수 없게 한다.</summary>
    internal static string Tip(string description, VirtualKey key) => $"{description} ({Label(key)})";

    /// <summary>표기 문자. 문자 키는 VirtualKey 이름이 곧 글자다(VirtualKey.F → "F").</summary>
    internal static string Label(VirtualKey key) => key.ToString();

    /// <summary>
    /// 버튼에 핫키를 걸고 툴팁 표기까지 한 번에 맞춘다 — 이 호출이 그 버튼 툴팁의 유일한 출처다.
    /// scope는 액셀러레이터를 얹을 요소(모듈 뷰 자신) — 키 스코프는 창 전체(XamlRoot)라
    /// 하단 바가 셸로 옮겨간 뒤에도 그대로 듣는다.
    /// </summary>
    internal static void Bind(UIElement scope, Control button, VirtualKey key, string description, Action action)
    {
        ToolTipService.SetToolTip(button, Tip(description, key));
        Register(scope, button, key, action);
    }

    /// <summary>
    /// 툴팁을 호출부가 상태에 따라 직접 갱신하는 버튼용(Fit · 바 크기처럼 표시가 변하는 것) — 키만 건다.
    /// 호출부는 툴팁을 만들 때 반드시 Tip(...)을 써서 같은 키 상수로 표기를 조립해야 한다.
    /// </summary>
    internal static void Register(UIElement scope, Control button, VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, args) =>
        {
            // 통과(삼키지 않음): 텍스트 입력·파일 리스트 포커스이거나, 버튼이 지금 눌릴 수 없는 상태.
            // KeyboardAcceleratorInvokedEventArgs.Handled는 기본값이 true라 명시적으로 되돌린다
            // (셸 MainWindow.AddShortcut의 A32 통과 분기와 같은 처리).
            if (ShouldPassThrough(scope) || button is not { IsEnabled: true, Visibility: Visibility.Visible })
            {
                args.Handled = false;
                return;
            }
            args.Handled = true;
            action();
        };
        scope.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>A34 버튼 핫키를 흘려보내야 하는 상태인지(텍스트 입력 또는 파일 리스트 포커스).</summary>
    internal static bool ShouldPassThrough(UIElement scope)
    {
        if (scope.XamlRoot is not { } root) return false;
        var focused = FocusManager.GetFocusedElement(root);
        return IsTextInput(focused) || IsInPassThroughSurface(focused as DependencyObject);
    }

    /// <summary>포커스가 텍스트 입력 컨트롤에 있는지 — A32/A84가 쓰던 판정 그대로(셸도 이걸 부른다).</summary>
    internal static bool IsTextInputFocused(UIElement scope)
        => scope.XamlRoot is { } root && IsTextInput(FocusManager.GetFocusedElement(root));

    private static bool IsTextInput(object? focused)
        => focused is TextBox or PasswordBox or RichEditBox;

    /// <summary>포커스 요소가 통과 표시된 표면(탐색기 리스트·트리) 안에 있는지 — 조상까지 훑는다.</summary>
    private static bool IsInPassThroughSurface(DependencyObject? element)
    {
        var node = element;
        for (var depth = 0; node is not null && depth < MaxAncestorDepth; depth++)
        {
            if (node is FrameworkElement fe && Equals(fe.Tag, PassThroughTag)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}
