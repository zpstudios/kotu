using System.Runtime;

namespace KOTU.App;

/// <summary>
/// A342 배치 4 실험 — 폴더 항해 구간에만 GC 지연 모드를 낮춘다.
/// <para>
/// 배경(실측): txt 1만 개 폴더 냉 진입 5.0초 중 GC 정지가 2.5초였고 gen2가 27회
/// (gen0 32 · gen1 29 = 사실상 전부 full GC)였다. 코드에는 GC.Collect도
/// AddMemoryPressure도 없으므로, XAML 객체 대량 생성(2,000행 + 2,000타일)이 런타임에
/// 메모리 압력으로 보고돼 full GC를 유도한다는 가설이다. 그래서 항해가 시작될 때
/// <c>GCSettings.LatencyMode</c>를 <c>SustainedLowLatency</c>로 올리고 조립이 끝나면
/// 되돌려, 진단 줄의 "gc a/b/c pause Nms"가 줄어드는지 본다.
/// </para>
/// <para>
/// 한계 두 가지(실험 전에 이미 알고 수용한 것):
/// ① 유도된 full GC(런타임이 스스로 부르는 것 포함 GC.Collect류)에는 이 모드가 효과가
///    없을 수 있다 — SustainedLowLatency는 "블로킹 gen2를 피한다"는 힌트일 뿐이다.
/// ② 중앙 타일 표면이 이번 항해에서 ShowEntries를 받지 않으면 Grid 비트가 남아 복원이
///    다음 정상 항해까지 미뤄진다. 치명적이지 않아 수용한다 — 다음 Enter는 이미 진입
///    상태라 무동작이고, 그 항해의 두 Leave가 남은 비트까지 함께 풀어 준다.
/// </para>
/// <para>
/// 스레드 계약: Enter · Leave는 <b>UI 스레드에서만</b> 불린다(좌 리스트의 NavigateTo ·
/// FinishFill, 중앙 타일의 FinishShowEntries — 전부 UI 문맥). 그래서 락이 없다.
/// </para>
/// </summary>
internal static class NavGcScope
{
    /// <summary>항해 한 번에 참여하는 두 표면 — 둘 다 끝나야 지연 모드를 되돌린다.</summary>
    [System.Flags]
    internal enum Participant
    {
        None = 0,
        /// <summary>좌 리스트(ExplorerPane) 조립.</summary>
        List = 1,
        /// <summary>중앙 타일(ThumbnailExplorer) 조립.</summary>
        Grid = 2,
    }

    private static bool _entered;
    private static Participant _pending = Participant.None;
    private static GCLatencyMode _saved;

    /// <summary>
    /// 항해 시작 — 아직 진입 상태가 아니면 현재 지연 모드를 저장하고 SustainedLowLatency로
    /// 바꾼다. 이미 진입 상태면 무동작(연속 항해에서 저장값이 덮이지 않게).
    /// 런타임 구성에 따라 대입이 거부될 수 있어(예: 서버 GC 조합) 예외는 삼키고
    /// "진입 안 됨"으로 둔다 — 그러면 뒤따르는 Leave도 전부 무동작이 된다.
    /// </summary>
    public static void Enter()
    {
        if (_entered) return;
        try
        {
            _saved = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }
        catch
        {
            return; // 진입 실패 — 실험만 못 할 뿐 항해는 그대로 진행된다
        }
        _entered = true;
        _pending = Participant.List | Participant.Grid;
    }

    /// <summary>
    /// 한 표면의 조립 완료 통지 — 남은 참여자가 없어지면 저장해 둔 지연 모드로 되돌린다.
    /// 진입 상태가 아니면 무동작. 낡은 항해(seq 불일치)에서는 부르지 않는다 — 새 항해가
    /// 이미 Enter로 두 비트를 세워 두었으므로 낡은 Leave가 그것을 깎으면 안 된다.
    /// </summary>
    public static void Leave(Participant who)
    {
        if (!_entered) return;
        _pending &= ~who;
        if (_pending != Participant.None) return;
        try
        {
            GCSettings.LatencyMode = _saved;
        }
        catch
        {
            // 복원 실패 — 되돌릴 방법이 없다. 진입 상태만 풀어 다음 항해가 다시 시도하게 둔다.
        }
        _entered = false;
    }
}
