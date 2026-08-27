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
///
/// A169(v0.172.0): 열림 상태의 <b>줄별 글자색</b>을 모듈이 실을 수 있다
/// (<see cref="Line1Color"/>·<see cref="Line2Color"/>). 하드웨어 모듈이 두 줄에
/// 서로 다른 센서 채널을 표시하는데 A101에서 채널 색이 소실됐던 것을 되돌리기 위한 자리다.
/// 색 타입 대신 ARGB 정수를 쓰는 이유도 위와 같다 — 이 계약은 UI 프레임워크에 기대지 않는다.
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

    /// <summary>
    /// 위 줄 글자색 (A169, v0.172.0) — <b>0xAARRGGBB 형식의 ARGB 32비트</b>.
    /// null이면 셸이 종전대로 모듈 액센트 1색을 쓴다(색을 안 싣는 모듈은 동작이 그대로다).
    /// 알파는 셸이 무시한다 — 글자는 늘 불투명이다.
    /// <b>유휴(1줄) 상태에서는 무시된다</b>: 유휴 색 규칙(A140 전면 채움 + 흰 글자 / 규칙 밖
    /// 저채도 글자)은 모듈 축이라 줄 색이 끼어들 자리가 없다.
    /// </summary>
    public uint? Line1Color { get; init; }

    /// <summary>
    /// 아래 줄 글자색 (A169, v0.172.0) — 형식·의미는 <see cref="Line1Color"/>와 같다.
    /// <see cref="Line2Bars"/>(막대)에는 적용되지 않는다(막대 색은 종전대로 모듈 액센트).
    /// </summary>
    public uint? Line2Color { get; init; }

    /// <summary>
    /// 열림 상태를 위/아래 2줄 대신 <b>우상단→좌하단 대각선 분할</b>로 그린다 (A138 — 문서 모듈의
    /// 페이지 위치: 좌상 = 현재, 우하 = 전체). 열림 2줄 자리를 대체하는 표기라 유휴 전면 채움
    /// (A140)과는 구조적으로 배타다 — 이 값이 true인 상태는 Line2가 있어 <see cref="IsIdle"/>이
    /// 항상 false다(<see cref="OpenDiagonal"/>만 이 값을 세운다 — 막대와의 조합은 만들지 않는다).
    /// </summary>
    public bool Diagonal { get; init; }

    /// <summary>콘텐츠를 안 열고 있는 상태(1줄·저채도)인지.</summary>
    public bool IsIdle => Line2 is null && Line2Bars is null;

    /// <summary>유휴 — 모듈 3자 표기 한 줄.</summary>
    public static TrayStatus Idle(string label) => new() { Line1 = label };

    /// <summary>
    /// 열림 — 두 줄. 못 구한 값은 자동으로 "—"가 된다.
    /// 색 두 개는 선택 인자다(A169) — 안 주면 종전과 완전히 같은 결과이므로 기존 호출부는 무수정이다.
    /// </summary>
    public static TrayStatus Open(string? line1, string? line2,
        uint? line1Color = null, uint? line2Color = null) =>
        new()
        {
            Line1 = Or(line1),
            Line2 = Or(line2),
            Line1Color = line1Color,
            Line2Color = line2Color,
        };

    /// <summary>열림 — 위 줄 텍스트 + 아래 줄 막대(오디오).</summary>
    public static TrayStatus OpenWithBars(string? line1, IReadOnlyList<double> bars) =>
        new() { Line1 = Or(line1), Line2Bars = bars };

    /// <summary>
    /// 열림 — 대각 분할 2값 (A138, 문서 전용: 좌상 = line1 = 현재 페이지, 우하 = line2 = 전체).
    /// 줄 색 축(A169)은 실을 수 없다 — 대각 표기는 두 값이 한 쌍(페이지 위치)이라 색을 가를 이유가
    /// 없고, 셸도 공용 색 하나로 그린다.
    /// </summary>
    public static TrayStatus OpenDiagonal(string? line1, string? line2) =>
        new() { Line1 = Or(line1), Line2 = Or(line2), Diagonal = true };

    private static string Or(string? text) => string.IsNullOrEmpty(text) ? Unknown : text;
}

/// <summary>
/// 모듈 뷰가 "지금 트레이에 뭘 보여야 하는지"를 셸에 알리는 계약 (A54, v0.118.0).
/// 모듈 프로젝트는 셸을 참조할 수 없으므로(App → 모듈 단방향) 계약을 Core에 두고
/// 모듈은 값만 내준다 — <see cref="IBottomBarProvider"/>·<see cref="IDriveStripHost"/>·
/// <see cref="IWindowShrinkSource"/>와 같은 방향이다. 아이콘 합성(GDI+)은 셸이 한다.
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
