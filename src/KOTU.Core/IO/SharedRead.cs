namespace KOTU.Core.IO;

/// <summary>
/// 다른 프로세스가 쓰기 잠금을 가진 파일도 읽을 수 있게 여는 읽기 전용 열기(A353).
/// <para>
/// 왜 필요한가: <c>File.OpenRead</c>·<c>File.ReadAllBytes</c>는 공유 모드가
/// <see cref="FileShare.Read"/>라, 누군가 그 파일을 <b>쓰려고</b> 잡고 있으면 공유 위반으로
/// 열기 자체가 실패한다("being used by another process"). 실제 사례 = 한 KOTU가
/// <c>%TEMP%\KOTU\trace.log</c>를 쓰는 중(DiagTrace는 <see cref="FileShare.ReadWrite"/>로 연다)에
/// 다른 창의 문서 모듈이 그 로그를 열지 못했다. 쓰는 쪽이 이미 공유를 허락했으니
/// <b>읽는 쪽</b>도 같은 폭으로 열면 된다.
/// </para>
/// <para>
/// 한계: 이것은 <b>읽는 순간의 스냅샷</b>일 뿐 일관성을 보장하지 않는다 — 읽는 동안 상대가 계속
/// 쓰면 잘린 줄·반쪽 레코드를 볼 수 있다. 로그·쓰기 중인 문서를 "지금 상태로 들여다본다"는
/// 용도에 맞고, 원자적 일관성이 필요한 곳에는 쓰지 않는다.
/// </para>
/// <para>
/// 적용 범위: <b>사용자가 여는 콘텐츠</b>를 읽는 경로만 이걸 쓴다. 우리가 방금 쓴 파일을 되읽는
/// 저장 검증·설정 파일·앱 자산 로드는 잠김 경합이 없어 기존 API 그대로 둔다.
/// </para>
/// </summary>
public static class SharedRead
{
    /// <summary>
    /// 읽기 전용 + <see cref="FileShare.ReadWrite"/>로 연다. 실패(없음·권한·삭제 경합)는
    /// <see cref="File.OpenRead"/>와 같은 예외를 그대로 던진다 — 호출부 처리는 바뀌지 않는다.
    /// </summary>
    public static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}
