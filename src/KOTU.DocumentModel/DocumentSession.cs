namespace KOTU.DocumentModel;

/// <summary>
/// 문서 저장 기준과 진행 중 저장의 소유자. UI 소유 스레드에서만 사용한다.
/// 입력 본문은 뷰가 보유하고 정규화한 스냅샷만 전달한다(매 입력마다 본문 복제하지 않음).
/// </summary>
public sealed class DocumentSession
{
    private SaveOperation? _activeSave;

    public string BaselineText { get; private set; } = string.Empty;
    public DocumentStamp DiskStamp { get; private set; }
    public bool IsSaving => _activeSave is not null;

    /// <summary>새 콘텐츠를 수립한다. 이전 콘텐츠의 늦은 저장 완료는 무효화한다.</summary>
    public void Reset(string normalizedText, DocumentStamp stamp = default)
    {
        BaselineText = normalizedText;
        DiskStamp = stamp;
        _activeSave = null;
    }

    public bool MatchesBaseline(string normalizedText) =>
        string.Equals(BaselineText, normalizedText, StringComparison.Ordinal);

    public bool CanClose(string normalizedText) => !IsSaving && MatchesBaseline(normalizedText);

    public bool IsCurrentSave(SaveOperation operation) => ReferenceEquals(operation, _activeSave);

    public SaveOperation? TryBeginSave()
    {
        if (IsSaving) return null;
        return _activeSave = new SaveOperation();
    }

    /// <summary>저장한 스냅샷을 기준으로 삼는다. 이후 입력까지 저장됐다고 간주하지 않는다.</summary>
    public bool TryCommitSave(SaveOperation operation, string savedText, DocumentStamp stamp)
    {
        if (!IsCurrentSave(operation)) return false;
        BaselineText = savedText;
        DiskStamp = stamp;
        return true;
    }

    public void EndSave(SaveOperation operation)
    {
        if (ReferenceEquals(operation, _activeSave)) _activeSave = null;
    }

    /// <summary>파일명과 무관한 저장 식별자. 같은 경로를 다시 열어도 이전 저장과 구분한다.</summary>
    public sealed class SaveOperation
    {
        internal SaveOperation() { }
    }
}

public readonly record struct DocumentStamp(DateTime WriteTimeUtc, long Length);
