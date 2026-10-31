namespace KOTU.DocumentModel;

/// <summary>시각 트리의 부착 여부와 세션의 영구 종료를 분리한다.</summary>
public sealed class EditorSurfaceLifetime
{
    public bool IsAttached { get; private set; }
    public bool IsFailed { get; private set; }
    public bool IsReleased { get; private set; }
    public void Attach() { if (!IsReleased) IsAttached = true; }
    public void Detach() => IsAttached = false;
    public bool Fail()
    {
        if (IsReleased || IsFailed) return false;
        IsFailed = true;
        return true;
    }
    public bool Release()
    {
        if (IsReleased) return false;
        IsReleased = true;
        IsAttached = false;
        return true;
    }
}
