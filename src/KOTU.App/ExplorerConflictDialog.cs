using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace KOTU.App;

/// <summary>
/// 이름 충돌 대화상자의 선택지 (A94 3차). Replace = 파일 덮어쓰기 / 폴더 병합,
/// Skip = 건너뛰기(이동이면 원본 잔류), KeepBoth = 기존 "이름 (2)" 규칙 재사용.
/// 취소(Esc·창 닫힘·표시 실패)는 선택지가 아니라 null로 표현한다 — 남은 작업 중단.
/// </summary>
internal enum ConflictChoice
{
    Replace,
    Skip,
    KeepBoth,
}

/// <summary>
/// 이동/복사/붙여넣기의 이름 충돌 대화상자 (A94 3차, v0.150.0 — 윈도우 탐색기 동등).
/// 코드 생성 ContentDialog(XAML 불요). 워커 스레드에서 AskAsync를 부르면 조작 시작 시점에
/// 캡처한 창의 DispatcherQueue로 마셜해 그 창의 XamlRoot에 띄우고, 워커는 TaskCompletionSource를
/// await로만 기다린다(UI 스레드를 .Wait()/.Result로 막지 않는다 — 저장소 교착 금지 관례).
///
/// ContentDialog는 창당 동시 1개(A113 실사례: 저장 흐름과 닫기 가드 충돌)라, 표시 직전을
/// XamlRoot 단위 SemaphoreSlim으로 직렬화한다 — 대화상자가 떠 있는 동안 두 번째 조작이
/// 시작되어도 두 대화상자가 충돌하지 않고 차례로 뜬다. 표시 실패(창이 그새 닫힘 등)는
/// 예외를 잡아 취소(null)로 돌려준다 — 호출부가 그 시점부터 남은 작업을 중단한다.
///
/// 버튼 매핑(구현 시 결정): Primary=Replace, Secondary=Keep both, Close=Skip.
/// 취소는 Esc — ContentDialog에는 제목 옆 X가 없고 3버튼이 전부라, Esc로 닫힌
/// ContentDialogResult.None을 취소로 해석한다. Skip 버튼 클릭과의 구분은
/// CloseButtonClick 플래그(버튼 = Skip) + Esc KeyDown 플래그(Esc = 취소) 둘로 한다 —
/// 어느 한쪽 감지가 어긋나는 환경에서도 "버튼 클릭 확증 없는 None = 취소"로 안전하게 떨어진다.
/// </summary>
internal static class ExplorerConflictDialog
{
    /// <summary>
    /// 창(XamlRoot) 단위 표시 직렬화 게이트 — ContentDialog는 창당 동시 1개(A113).
    /// ConditionalWeakTable이라 닫힌 창의 항목은 XamlRoot가 수거될 때 함께 사라진다.
    /// 모든 창이 단일 UI 스레드(A110 확정)지만 워커 완주 시점 정리가 없어 약참조 테이블을 쓴다.
    /// </summary>
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> Gates = new();

    /// <summary>
    /// 충돌 1건을 사용자에게 묻는다 — 아무 스레드에서나 호출 가능(워커 전제).
    /// name = 충돌 항목명, isFolder = 항목이 폴더인지(본문·병합 안내 분기),
    /// targetFolder = 대상 폴더 경로(본문 표기), offerAll = "남은 충돌에 일괄 적용" 체크박스 표시.
    /// 반환 Choice가 null이면 취소(Esc·창 닫힘·표시 실패) — 남은 작업 중단.
    /// All은 체크박스 상태 — 3버튼 전부에 적용된다(Replace all / Skip all / Keep both all).
    /// </summary>
    internal static Task<(ConflictChoice? Choice, bool All)> AskAsync(
        DispatcherQueue dispatcher, XamlRoot? root,
        string name, bool isFolder, string targetFolder, bool offerAll)
    {
        var tcs = new TaskCompletionSource<(ConflictChoice? Choice, bool All)>(
            TaskCreationOptions.RunContinuationsAsynchronously); // UI 스레드에서 완료해도 워커 연속은 풀로
        if (root is null)
        {
            tcs.TrySetResult((null, false)); // 표시할 곳이 없다 — 취소 흐름
            return tcs.Task;
        }
        if (!dispatcher.TryEnqueue(() => _ = RunOnUiAsync(root, name, isFolder, targetFolder, offerAll, tcs)))
            tcs.TrySetResult((null, false)); // UI 스레드가 이미 내려갔다 — 취소 흐름
        return tcs.Task;
    }

    /// <summary>UI 스레드: 게이트 직렬화 후 표시. 어떤 실패도 취소(null)로만 떨어진다 — 예외 전파 없음.</summary>
    private static async Task RunOnUiAsync(XamlRoot root, string name, bool isFolder,
        string targetFolder, bool offerAll, TaskCompletionSource<(ConflictChoice? Choice, bool All)> tcs)
    {
        try
        {
            var gate = Gates.GetValue(root, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(); // 같은 창의 앞선 대화상자가 닫히기를 대기 (VideoPlayerView._playerGate 관용구)
            try
            {
                tcs.TrySetResult(await ShowOnUiAsync(root, name, isFolder, targetFolder, offerAll));
            }
            finally
            {
                gate.Release();
            }
        }
        catch
        {
            tcs.TrySetResult((null, false)); // ShowAsync 실패(창 닫힘 등) = 그 시점부터 취소 흐름
        }
    }

    /// <summary>대화상자 구성·표시·결과 해석 — UI 스레드 전용(RunOnUiAsync만 부른다).</summary>
    private static async Task<(ConflictChoice? Choice, bool All)> ShowOnUiAsync(
        XamlRoot root, string name, bool isFolder, string targetFolder, bool offerAll)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"The destination already has a {(isFolder ? "folder" : "file")} named \"{name}\".",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = "Destination: " + targetFolder,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 12,
        });
        if (isFolder)
            body.Children.Add(new TextBlock
            {
                Text = "Replace merges the contents into the existing folder. Files inside get the same choices.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                FontSize = 12,
            });
        CheckBox? applyAll = null;
        if (offerAll)
        {
            applyAll = new CheckBox { Content = "Do this for all remaining conflicts" };
            body.Children.Add(applyAll);
        }

        var dialog = new ContentDialog
        {
            Title = "Replace or skip files",
            Content = body,
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Keep both",
            CloseButtonText = "Skip",
            DefaultButton = ContentDialogButton.Primary, // 탐색기처럼 Enter = 대체
            XamlRoot = root,
        };

        // Skip(버튼)과 취소(Esc)의 구분 — 클래스 요약 주석 참고. handledEventsToo 구독은
        // ContentDialog 자체 Esc 처리가 이벤트를 Handled로 만들어도 관찰하기 위함이다
        // (ExplorerPane 생성자 AddHandler와 같은 관용구).
        var skipClicked = false;
        var escPressed = false;
        dialog.CloseButtonClick += (_, _) => skipClicked = true;
        dialog.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) escPressed = true;
        }), true);

        var result = await dialog.ShowAsync();
        ConflictChoice? choice = result switch
        {
            ContentDialogResult.Primary => ConflictChoice.Replace,
            ContentDialogResult.Secondary => ConflictChoice.KeepBoth,
            // None = Skip 버튼 또는 Esc/외부 닫힘 — 버튼 클릭이 확증되고 Esc가 없을 때만 Skip
            _ => skipClicked && !escPressed ? ConflictChoice.Skip : null,
        };
        return (choice, choice is not null && applyAll?.IsChecked == true);
    }
}
