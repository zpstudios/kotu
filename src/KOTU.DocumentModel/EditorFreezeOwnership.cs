namespace KOTU.DocumentModel;

/// <summary>이전 닫기의 늦은 취소가 같은 편집기의 다음 닫기 잠금을 풀지 못하게 한다.</summary>
public sealed class EditorFreezeOwnership
{
    public long? Owner { get; private set; }
    public void Acquire(long owner)
    {
        if (Owner is not null) throw new InvalidOperationException("A close operation is already pending.");
        Owner = owner;
    }
    public bool Owns(long owner) => Owner == owner;
    public bool Release(long owner)
    {
        if (!Owns(owner)) return false;
        Owner = null;
        return true;
    }
}
