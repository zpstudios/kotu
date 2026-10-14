namespace KOTU.Core.Threading;

/// <summary>UI 소유: 전체 재조회 하나와 보류 요청 하나. 편집 중에는 시작하지 않는다.</summary>
public sealed class RefreshRequestGate
{
    public bool Running { get; private set; }
    public bool Pending { get; private set; }

    public bool Request(bool blocked)
    {
        Pending = true;
        if (blocked || Running) return false;
        Pending = false;
        Running = true;
        return true;
    }

    public void Defer() => Pending = true;
    public void ResetPending() => Pending = false;
    public void Complete() => Running = false;
}
