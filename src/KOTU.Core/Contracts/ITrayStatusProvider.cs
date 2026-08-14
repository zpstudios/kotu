namespace KOTU.Core.Contracts;

/// <summary>
/// 트레이 아이콘 한 개(= 인스턴스 한 개)가 지금 표시할 내용 (A54, v0.118.0).
///
/// 16px 아이콘 안에 값을 그리는 규격이라 두 상태를 <b>줄 수와 색</b>으로 이중 구분한다:
///  · <b>유휴</b>(콘텐츠 없음) = 1줄 중앙·저채도 — 모듈 3자 표기(IMG/VID/AUD/DOC/ARC/ALL/INF).
///  · <b>열림</b> = 2줄·모듈 색 — 위 줄이 종류, 아래 줄이 값(또는 오디오의 막대 시각화).
/// 이 이중 구분 덕에 "DOC"(유휴)와 ".doc"(열림 위 줄) 같은 문자 충돌이 무해해진다.
///
/// 값 문자열은 모듈이 <see cref="TrayFormat"/>으로 만들어 넘긴다 —
/// 셸은 받은 문자열을 그리기만 한다(UI 프레임워크 비의존 유지).
/// </summary>
public sealed record TrayStatus
{
    /// <summary>값을 못 구한 줄에 쓰는 표기(구 A18 센서 트레이가 쓰던 em dash 승계).</summary>
    public const string Unknown = "—";

    /// <summary>위 줄. 유휴면 이 줄 하나만 중앙에 그린다.</summary>
    public required string Line1 { get; init; }

    /// <summary>아래 줄 텍스트. null이면서 <see cref="Line2Bars"/>도 null이면 유휴로 본다.</summary>
    public string? Line2 { get; init; }

    /// <summary>
    /// 아래 줄을 텍스트 대신 막대로 그릴 때의 높이 비율(0~1). 오디오 이퀄라이저 장식용 —
    /// 실제 주파수 분석이 아니라 재생 중임을 알리는 의사 패턴이다(구현 시 결정, A54).
    /// </summary>
    public IReadOnlyList<double>? Line2Bars { get; init; }

    /// <summary>콘텐츠를 안 열고 있는 상태(1줄·저채도)인지.</summary>
    public bool IsIdle => Line2 is null && Line2Bars is null;

    /// <summary>유휴 — 모듈 3자 표기 한 줄.</summary>
    public static TrayStatus Idle(string label) => new() { Line1 = label };

    /// <summary>열림 — 두 줄. 못 구한 값은 자동으로 "—"가 된다.</summary>
    public static TrayStatus Open(string? line1, string? line2) =>
        new() { Line1 = Or(line1), Line2 = Or(line2) };

    /// <summary>열림 — 위 줄 텍스트 + 아래 줄 막대(오디오).</summary>
    public static TrayStatus OpenWithBars(string? line1, IReadOnlyList<double> bars) =>
        new() { Line1 = Or(line1), Line2Bars = bars };

    private static string Or(string? text) => string.IsNullOrEmpty(text) ? Unknown : text;
}

/// <summary>
/// 모듈 뷰가 "지금 트레이에 뭘 보여야 하는지"를 셸에 알리는 계약 (A54, v0.118.0).
/// 모듈 프로젝트는 셸을 참조할 수 없으므로(App → 모듈 단방향) 계약을 Core에 두고
/// 모듈은 값만 내준다 — <see cref="IBottomBarProvider"/>·<see cref="IDriveStripHost"/>·
/// <see cref="IWindowCollapseSource"/>와 같은 방향이다. 아이콘 합성(GDI+)은 셸이 한다.
///
/// 구현하지 않는 화면(설정·미지원 파일 안내·정보 모듈처럼 표시 값이 상수인 경우)은
/// 셸이 모듈 ID → 3자 표기 표로 유휴 아이콘을 그린다.
/// </summary>
public interface ITrayStatusProvider
{
    /// <summary>
    /// 표시 값이 바뀌었을 때 발생한다(UI 스레드 보장 없음 — 셸이 디스패치한다).
    /// 값이 실제로 바뀐 경우만 쏠 필요는 없다: 셸이 문자열 키를 비교해
    /// 같으면 아이콘을 다시 합성하지 않는다(구 A18 SensorTray의 ComposeKey 방식).
    /// </summary>
    event Action? TrayStatusChanged;

    /// <summary>지금 그려야 할 내용(UI 스레드에서 호출된다).</summary>
    TrayStatus GetTrayStatus();
}
