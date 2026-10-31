namespace KOTU.DocumentModel;

/// <summary>표시 모드는 브라우저의 잠금과 본문 확인이 끝난 뒤에만 확정한다.</summary>
public static class EditorModeTransition
{
    public static async Task PrepareAsync(bool previousReadOnly, bool readOnly,
        Func<bool, Task> setReadOnly, Func<Task> captureSnapshot, Func<bool> isCurrent,
        Action<Exception> recoveryRequired)
    {
        try
        {
            await setReadOnly(readOnly);
            if (!isCurrent()) throw new OperationCanceledException();
            await captureSnapshot();
            if (!isCurrent()) throw new OperationCanceledException();
        }
        catch
        {
            if (isCurrent())
            {
                try { await setReadOnly(previousReadOnly); }
                catch (Exception rollbackError) { recoveryRequired(rollbackError); }
            }
            throw;
        }
    }
}
