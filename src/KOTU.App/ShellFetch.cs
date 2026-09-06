using Windows.Foundation;

namespace KOTU.App;

/// <summary>
/// A352 배치 3: 셸(WinRT) 비동기 호출에 시한을 거는 공용 헬퍼.
/// <para>
/// 왜 필요한가: 갓 복사된 OneDrive 폴더의 파일에서 <c>StorageFile.GetFileFromPathAsync</c> ·
/// <c>GetThumbnailAsync</c> · 속성 조회가 <b>영원히 반환하지 않는</b> 것이 A352 트레이스로
/// 실측됐다(파일 4장 · "fill shell" 네 줄 뒤 완료 줄도 예외 줄도 끝내 없음). 워커 풀은 3칸뿐이라
/// 그런 호출 셋이 걸리면 그 뒤 모든 셸 썸네일이 조용히 멈춘다 — 즉 한 폴더의 동기화 지연이
/// 앱 전체의 미리보기를 끝내는 구조였다. 여기서 시한을 걸어 워커를 되찾는다.
/// </para>
/// <para>
/// <b>취소는 기대하지 않는다</b>: 시한이 지나도 원래의 WinRT 작업은 계속 돈다.
/// <see cref="IAsyncInfo.Cancel"/>를 한 번 불러 보긴 하지만(셸이 협조하면 자원을 일찍 놓는다)
/// 효과는 보장되지 않는다. 우리가 되찾는 것은 <b>호출한 스레드</b>이고, 뒤늦게 도착할 결과는
/// 주인이 없으므로 그대로 버려진다.
/// </para>
/// </summary>
internal static class ShellFetch
{
    /// <summary>
    /// 셸 호출 한 건의 시한. 정상 파일의 썸네일·속성 조회는 실측 수십 ms라 100배 여유다 —
    /// 이 값을 넘긴 호출은 느린 것이 아니라 걸린 것으로 본다(OneDrive 동기화 중 무기한 대기 실측).
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 워커 스레드용 동기 대기 — 시한을 넘기면 <see cref="TimeoutException"/>.
    /// 호출부는 이 예외를 "실패"로 접되 <b>"없음 확정"으로는 굳히지 않는다</b>
    /// (동기화가 끝난 뒤 다시 물으면 성공할 수 있다 — ThumbnailExplorer의 PreviewTimedOut 규칙).
    /// </summary>
    internal static T WaitOrThrow<T>(IAsyncOperation<T> operation)
    {
        try
        {
            return operation.AsTask().WaitAsync(Timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            TryCancel(operation);
            throw;
        }
    }

    /// <summary>
    /// UI 스레드 await용 — <see cref="WaitOrThrow{T}"/>와 같은 시한·같은 예외다.
    /// <para>
    /// <b>A352 배치 4부터 사용처가 없다</b>(유일한 호출부였던 ThumbnailExplorer.
    /// FillCachedThumbnailAsync를 삭제했다). 남겨 두되 <b>새로 쓰지 말 것</b>:
    /// UI 스레드에서 <c>StorageFile</c> 취득·썸네일·속성 조회를 부르면 <c>await</c> 앞의 호출
    /// 자체가 동기로 COM을 왕복하고, 그 대기가 메시지를 펌프해 XAML의 큐된 작업을 재진입시켜
    /// 프로세스가 죽는다(힙 포함 크래시 덤프로 확정 — ThumbnailExplorer 클래스 상단
    /// "불가침 규칙" 문단). 셸 호출은 워커 스레드에서 <see cref="WaitOrThrow{T}"/>로 한다.
    /// </para>
    /// </summary>
    internal static async Task<T> WaitOrThrowAsync<T>(IAsyncOperation<T> operation)
    {
        try
        {
            return await operation.AsTask().WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            TryCancel(operation);
            throw;
        }
    }

    /// <summary>예외가 이 시설의 시한 초과인지 — 호출부가 "없음 확정"과 "지금은 못 얻었다"를 가르는 판정.</summary>
    internal static bool IsTimeout(Exception ex) => ex is TimeoutException;

    /// <summary>취소 시도(효과 기대 없음) — 이미 끝났거나 취소를 지원하지 않으면 던진다. 삼킨다.</summary>
    private static void TryCancel(IAsyncInfo operation)
    {
        try
        {
            operation.Cancel();
        }
        catch
        {
            // 되찾을 것은 스레드뿐이다 — 취소 실패는 진행에 아무 영향이 없다.
        }
    }
}
