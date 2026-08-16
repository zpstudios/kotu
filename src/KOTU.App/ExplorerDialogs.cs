using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.App;

/// <summary>
/// 탐색기 파일 조작의 확인·안내 대화상자 모음 (A94 4차, v0.151.0) + 창 단위 표시 게이트.
/// 코드 생성 ContentDialog(XAML 불요) — 3차의 <see cref="ExplorerConflictDialog"/>와 같은 방식이다.
///
/// **게이트를 여기로 올린 이유**: ContentDialog는 창당 동시 1개다(A113 실사례). 3차까지는 충돌
/// 대화상자 하나뿐이라 그 클래스가 자기 테이블을 들고 있었지만, 4차부터 영구 삭제 확인·접근 거부
/// 안내가 더해져 서로 다른 대화상자가 겹칠 수 있다(예: 붙여넣기 진행 중 Shift+Del). 그래서
/// XamlRoot 단위 세마포어를 <see cref="GateFor"/> 한 곳으로 모으고 충돌 대화상자도 이걸 쓴다 —
/// 대화상자 종류가 달라도 한 창에서는 차례로 뜬다.
///
/// 스레드 규약도 3차와 같다: 아무 스레드에서나 부를 수 있고(워커 포함), 조작 시작 시점에 캡처한
/// DispatcherQueue로 마셜해 그 창의 XamlRoot에 띄운다. 호출부는 TaskCompletionSource를 await할 뿐
/// UI 스레드를 .Wait()/.Result로 막지 않는다. 표시 실패(창 닫힘·TryEnqueue 거절·XamlRoot 부재)는
/// 전부 안전한 기본값(확인 대화상자 = 취소 / 관리자 재시작 제안 = 재시작 안 함)으로 떨어진다.
/// </summary>
internal static class ExplorerDialogs
{
    /// <summary>
    /// 창(XamlRoot) 단위 표시 직렬화 게이트 — ContentDialog는 창당 동시 1개(A113).
    /// ConditionalWeakTable이라 닫힌 창의 항목은 XamlRoot가 수거될 때 함께 사라진다.
    /// 모든 창이 단일 UI 스레드(A110 확정)지만 워커 완주 시점 정리가 없어 약참조 테이블을 쓴다.
    /// </summary>
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> Gates = new();

    /// <summary>이 창의 대화상자 게이트 — 탐색기 대화상자 전부(충돌·영구 삭제·접근 거부)가 공유한다.</summary>
    internal static SemaphoreSlim GateFor(XamlRoot root) =>
        Gates.GetValue(root, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// 영구 삭제(Shift+Del) 확인 — 탐색기 동등으로 **영구 삭제만** 확인창을 띄운다(휴지통행은 무확인).
    /// 1건이면 항목명을 함께 보여 준다. 기본 버튼 = Cancel(파괴적 동작의 Enter 오폭 방지 — A113 ⓓ와
    /// 같은 원칙, DocumentView의 폐기 확인과 같은 DefaultButton.Close).
    /// 반환 true = 삭제 진행. 표시할 수 없으면 false(= 취소)다.
    /// </summary>
    internal static Task<bool> ConfirmPermanentDeleteAsync(
        DispatcherQueue dispatcher, XamlRoot? root, IReadOnlyList<string> paths) =>
        ShowSerializedAsync(dispatcher, root, r => ShowPermanentDeleteAsync(r, paths), false);

    /// <summary>
    /// 접근 거부(UAC 필요) 안내 + 관리자 재시작 제안 — 조작 실패 중 권한 부족이 1건 이상일 때
    /// 완료 요약 문구 대신 뜬다(일반 실패에 뭉개지지 않게). Primary = 재시작, 기본 버튼 = Cancel.
    /// 재시작을 고르면 하드웨어 뷰와 같은 공용 흐름(Integration.AdminRelaunch)을 탄다 —
    /// 승격 뒤 자동 재시도는 하지 않는다(사용자가 다시 조작. docs/A94-matrix.md 명기).
    /// </summary>
    internal static async Task PromptAccessDeniedAsync(
        DispatcherQueue dispatcher, XamlRoot? root, int denied)
    {
        if (!await ShowSerializedAsync(dispatcher, root, r => ShowAccessDeniedAsync(r, denied), false))
            return;
        // 게이트를 놓은 뒤 UI 스레드에서 실행 — 마지막 단계가 Application.Exit라 대화상자
        // 뒷정리가 끝난 자리에서 부르는 편이 안전하다.
        dispatcher.TryEnqueue(() => Integration.AdminRelaunch.Relaunch());
    }

    // ---------- 내부: 마셜 + 게이트 공통 파이프 ----------

    /// <summary>
    /// 아무 스레드 → UI 스레드 마셜 → 창 게이트 직렬화 → 표시. 어떤 실패도 fallback으로만 떨어진다
    /// (예외 전파 없음). TCS는 RunContinuationsAsynchronously — UI 스레드에서 완료해도 워커 연속이
    /// UI에 얹히지 않는다(3차 규약 그대로).
    /// </summary>
    private static Task<T> ShowSerializedAsync<T>(
        DispatcherQueue dispatcher, XamlRoot? root, Func<XamlRoot, Task<T>> show, T fallback)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (root is null)
        {
            tcs.TrySetResult(fallback); // 표시할 곳이 없다
            return tcs.Task;
        }
        if (!dispatcher.TryEnqueue(() => _ = RunOnUiAsync(root, show, fallback, tcs)))
            tcs.TrySetResult(fallback); // UI 스레드가 이미 내려갔다
        return tcs.Task;
    }

    /// <summary>UI 스레드: 게이트를 잡고 표시. 표시 실패(창 닫힘 등)도 fallback이다.</summary>
    private static async Task RunOnUiAsync<T>(
        XamlRoot root, Func<XamlRoot, Task<T>> show, T fallback, TaskCompletionSource<T> tcs)
    {
        try
        {
            var gate = GateFor(root);
            await gate.WaitAsync(); // 같은 창의 앞선 대화상자가 닫히기를 대기
            try
            {
                tcs.TrySetResult(await show(root));
            }
            finally
            {
                gate.Release();
            }
        }
        catch
        {
            tcs.TrySetResult(fallback);
        }
    }

    // ---------- 내부: 대화상자 구성 (UI 스레드 전용) ----------

    private static async Task<bool> ShowPermanentDeleteAsync(XamlRoot root, IReadOnlyList<string> paths)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = paths.Count == 1
                ? "Permanently delete this item?"
                : $"Permanently delete these {paths.Count} items?",
            TextWrapping = TextWrapping.Wrap,
        });
        // 1건이면 이름을 보여 준다(무엇을 지우는지 확증) — 여러 건은 수만 알린다.
        if (paths.Count == 1 && NameOf(paths[0]) is { Length: > 0 } name)
            body.Children.Add(new TextBlock
            {
                Text = name,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                FontSize = 12,
            });
        body.Children.Add(new TextBlock
        {
            // 단수·복수 모두에 맞는 문장(위 제목 줄이 이미 건수를 말한다)
            Text = "Items deleted this way do not go to the Recycle Bin and cannot be restored.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 12,
        });

        var dialog = new ContentDialog
        {
            Title = "Delete permanently",
            Content = body,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, // 파괴 방지 — Enter는 취소로 떨어진다
            XamlRoot = root,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static async Task<bool> ShowAccessDeniedAsync(XamlRoot root, int denied)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"Access was denied for {denied} item(s). " +
                   "Restarting as administrator may allow this operation.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = "KOTU closes and opens again with administrator rights, "
                 + "and the open windows are restored. The operation is not retried automatically.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 12,
        });

        var dialog = new ContentDialog
        {
            Title = "Access denied",
            Content = body,
            PrimaryButtonText = "Restart as admin", // 하드웨어 뷰 버튼과 같은 문구
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, // 재시작도 파괴적(전 창 닫힘) — 기본은 취소
            XamlRoot = root,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>경로의 표시용 이름 — 끝 구분자를 떼고 마지막 조각. 못 얻으면 빈 문자열.</summary>
    private static string NameOf(string path)
    {
        try
        {
            return Path.GetFileName(
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            return string.Empty; // 별난 경로 — 이름 줄만 생략된다
        }
    }
}
