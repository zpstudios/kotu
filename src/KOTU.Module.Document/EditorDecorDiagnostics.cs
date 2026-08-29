namespace KOTU.Module.Document;

/// <summary>
/// A285: 에디터 장식 EOF 계측 오버레이 토글의 설정 키·변경 알림 — ShellDiagnostics(A234) 관용구
/// 복제(SettingKey + Changed + NotifyChanged). 설정 화면(SettingsView)이 저장 후 NotifyChanged를
/// 부르고, 열린 문서 뷰(DocumentView)가 Changed를 구독해 EditorDecor의 계측 오버레이를 즉시
/// 켜고 끈다. 계측 목적: EOF 오배치 수리 2연속 실패(A283·A284) 뒤라 블라인드 수리를 멈추고,
/// 실기기 스크린샷 1장으로 "RectOf(len-1)이 실제로 어떤 좌표를 주는가 / EOF가 어느 가드에서
/// 죽는가"를 실측 확정하기 위한 시설이다(원인 확정 후 다음 항목이 수리).
/// <para>위치가 KOTU.App이 아니라 이 모듈인 이유: 참조 방향이 App → Module.Document 단방향이라
/// (모듈은 App을 못 본다 — csproj가 정본) 읽는 쪽(DocumentView)과 쓰는 쪽(SettingsView)이
/// 둘 다 닿는 자리는 여기뿐이다. document.* 설정 키의 정의처(DocumentModule)와 같은 배치.</para>
/// </summary>
public static class EditorDecorDiagnostics
{
    /// <summary>설정 키. 값은 bool, 기본 false — 일반 사용자에게는 보이지 않는 진단 전용 오버레이다.
    /// 파일(settings.json)에 저장되므로 재시작 후에도 유지된다(ShellDiagnostics와 같은 구현 결정).</summary>
    public const string SettingKey = "diag.editorDecor";

    /// <summary>설정 변경 시 열린 문서 뷰가 오버레이 표시를 다시 적용하도록 알린다(설정 화면 → 각 DocumentView).</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();
}
