namespace KOTU.Core.Settings;

/// <summary>UI 저장 요청의 실패를 관측하고 셸에 알린다. 메모리 값과 창 간 알림은 즉시 유지한다.</summary>
public static class SettingsPersistence
{
    private static readonly object Gate = new();
    private static Exception? s_lastError;
    private static long s_request;
    public static Exception? LastError => Volatile.Read(ref s_lastError);
    public static event Action? Changed;

    public static async void RequestSave(this ISettingsService settings)
    {
        long request;
        lock (Gate) request = ++s_request;
        Exception? error = null;
        try { await settings.SaveAsync().ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        lock (Gate)
        {
            if (request != s_request) return;
            Volatile.Write(ref s_lastError, error);
        }
        Changed?.Invoke(); // 구독자는 통지 순서 대신 최신 LastError를 읽는다.
    }

    public static void Report(Exception? error)
    {
        lock (Gate)
        {
            ++s_request;
            Volatile.Write(ref s_lastError, error);
        }
        Changed?.Invoke();
    }
}
