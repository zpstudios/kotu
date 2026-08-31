using System.Runtime.InteropServices;

namespace KOTU.App.Integration;

/// <summary>
/// 화면보호기·디스플레이 꺼짐 억제 (A306, v0.290.0) — kernel32 SetThreadExecutionState.
/// 모듈 프로젝트에는 DllImport를 두지 않는다는 규약에 따라 P/Invoke는 여기(셸)에만 있고,
/// 모듈 진입은 Core 훅(<see cref="KOTU.Core.Integration.DisplayAwakeHook"/> — App이 시작 시
/// 배선) 경유다. 요구 개수 세기(창 여러 개)도 그 훅의 몫이라 이 파일은 "지금 걸어라/풀어라"만 한다.
///
/// <b>스레드 규칙</b>: 이 API가 세우는 상태는 <b>부른 스레드</b>에 붙는다. 그 스레드가 끝나면
/// 상태도 사라지고, 다른 스레드에서 푸는 것은 아무 효과가 없다. 그래서 호출은 창 수명 내내
/// 살아 있는 <b>UI 스레드</b>에서만 한다(워커·스레드풀 금지 — 반납된 스레드에서는 억제가
/// 소리 없이 무효가 된다). 이 앱은 창이 여럿이어도 UI 스레드가 하나다(WindowManager 주석).
///
/// 반환값(직전 상태)은 쓰지 않는다 — 0이면 실패지만 억제는 보조 기능이라 조용히 넘긴다(A306 확정).
/// </summary>
internal static class DisplayAwake
{
    /// <summary>ES_CONTINUOUS — 뒤에 오는 플래그를 "해제할 때까지 유지"로 만든다(0x80000000).</summary>
    private const uint EsContinuous = 0x80000000;

    /// <summary>ES_DISPLAY_REQUIRED — 디스플레이 유휴 타이머를 계속 되돌린다(화면보호기·화면 꺼짐 억제, 0x00000002).</summary>
    private const uint EsDisplayRequired = 0x00000002;

    /// <summary>
    /// keepAwake = true면 억제를 걸고, false면 ES_CONTINUOUS 단독 호출로 원상 복귀한다
    /// (해제 = "요구 없음"을 다시 세우는 것 — 별도의 해제 API가 없다).
    /// UI 스레드에서만 부를 것.
    /// </summary>
    public static void Set(bool keepAwake)
    {
        _ = SetThreadExecutionState(keepAwake ? EsContinuous | EsDisplayRequired : EsContinuous);
    }

    // ---------- P/Invoke ----------

    // EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags) — EXECUTION_STATE는 DWORD(uint)다.
    // 반환은 직전 상태(실패 시 0). SetLastError는 문서화돼 있지 않아 붙이지 않는다.
    [DllImport("kernel32")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
