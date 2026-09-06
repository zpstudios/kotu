namespace KOTU.Core.Contracts;

/// <summary>모듈 뷰를 열 때 전달되는 맥락. 파일 없이(네비게이션으로) 열 수도 있다.</summary>
public sealed record OpenContext
{
    /// <summary>열 파일 경로. 네비게이션으로 진입한 경우 null.</summary>
    public string? FilePath { get; init; }

    /// <summary>추가 인자(커맨드라인 등).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// A354: 재생 모듈(영상·오디오)이 이 파일을 <b>자동 재생하지 않고</b> 이어보기 위치에서
    /// 일시정지로 세워야 하는가. 재시작 세션 복원(A124) 전용이다 — 부팅 직후 장치 준비 전
    /// 자동 재생으로 libvlc 이벤트 축이 죽는 현상 회피 + 창이 여럿 되살아날 때 갑작스러운
    /// 소리 방지. 일반 열기 경로는 전부 false(기본값)라 동작이 바뀌지 않는다.
    /// </summary>
    public bool StartPaused { get; init; }

    public static OpenContext Empty { get; } = new();

    /// <summary>
    /// 파일 하나로 여는 기본 맥락. <paramref name="startPaused"/>는 A354 재시작 세션 복원만
    /// true로 준다(기본값 false = 종전 호출부 무변경).
    /// </summary>
    public static OpenContext ForFile(string path, bool startPaused = false) =>
        new() { FilePath = path, StartPaused = startPaused };
}
