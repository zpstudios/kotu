namespace KOTU.Core.Routing;

/// <summary>
/// 자동 미리보기의 원본 접근 전 검사. 파일 속성을 읽으므로 반드시 워커에서 호출한다.
/// 열거 당시의 placeholder 판정은 유지하고, 로컬 항목만 현재 속성을 다시 확인한다.
/// 속성 조회와 실제 열기는 원자적이지 않으므로 그 사이의 상태 변경까지 막지는 못한다.
/// </summary>
public static class CloudFileAccess
{
    public static bool CanReadContent(string path, bool listedAsPlaceholder, CancellationToken cancellation,
        Func<string, FileAttributes>? readAttributes = null)
    {
        cancellation.ThrowIfCancellationRequested();
        // RecallOnOpen은 디렉터리 열거 전용 비트일 수 있으므로 최신 조회만으로 보호를 낮추지 않는다.
        if (listedAsPlaceholder) return false;

        FileAttributes attributes;
        try
        {
            attributes = (readAttributes ?? File.GetAttributes)(path);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // 조회 실패는 원본 읽기 허가가 아니다. 조회 중 취소도 실패 폴백과 구분한다.
            cancellation.ThrowIfCancellationRequested();
            return false;
        }
        cancellation.ThrowIfCancellationRequested();
        return !ExplorerListing.IsCloudPlaceholder(attributes);
    }

    /// <summary>로컬임을 확인한 경우에만 읽는다. 읽기 자체의 예외는 호출자가 기존 정책으로 처리한다.</summary>
    public static T? ReadLocal<T>(string path, bool listedAsPlaceholder, CancellationToken cancellation,
        Func<T?> read, Func<string, FileAttributes>? readAttributes = null) where T : class
        => CanReadContent(path, listedAsPlaceholder, cancellation, readAttributes) ? read() : null;
}
