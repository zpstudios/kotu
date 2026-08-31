namespace KOTU.Module.Audio;

/// <summary>
/// A301: 오디오 비주얼라이저 교체 계측 오버레이 토글의 설정 키·변경 알림 — EditorDecorDiagnostics
/// (A285)·ShellDiagnostics(A234) 관용구 복제(SettingKey + Changed + NotifyChanged). 설정 화면
/// (SettingsView)이 저장 후 NotifyChanged를 부르고, 열린 오디오 뷰(AudioPlayerView)가 Changed를
/// 구독해 계측 오버레이를 즉시 켜고 끈다. 계측 목적: 스타일 교체 버벅임·싱크 어긋남(A301)을
/// 어림 파라미터로 흔들기 전에(A285 교훈) 실기기에서 교체 소요(재생성 시작→신 인스턴스 Playing
/// 도달 ms)를 실측 확정하기 위한 시설이다.
/// <para>위치가 KOTU.App이 아니라 이 모듈인 이유: 참조 방향이 App → Module.Audio 단방향이라
/// 읽는 쪽(AudioPlayerView)과 쓰는 쪽(SettingsView)이 둘 다 닿는 자리는 여기뿐이다
/// (EditorDecorDiagnostics가 Module.Document에 있는 것과 같은 배치 근거).</para>
/// </summary>
public static class AudioDiagnostics
{
    /// <summary>설정 키. 값은 bool, 기본 false — 일반 사용자에게는 보이지 않는 진단 전용 오버레이다.
    /// 파일(settings.json)에 저장되므로 재시작 후에도 유지된다(ShellDiagnostics와 같은 구현 결정).</summary>
    public const string SettingKey = "diag.audioSwap";

    /// <summary>설정 변경 시 열린 오디오 뷰가 오버레이 표시를 다시 적용하도록 알린다(설정 화면 → 각 AudioPlayerView).</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();
}
