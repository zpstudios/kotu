namespace KOTU.FileOperations;

// 계획은 원본 전체를 먼저 확인한다. 실행 중 새로 생긴 항목은 삭제 대상으로 삼지 않는다.
internal sealed record TransferNode(string Path, bool IsFolder, EntryStamp Stamp,
    IReadOnlyList<TransferNode> Children);

internal static class TransferPlanner
{
    internal static TransferNode Read(string path)
    {
        using var ancestors = PhysicalTransferStore.LockAncestors(path);
        var stamp = PhysicalTransferStore.Inspect(path)
            ?? throw new FileNotFoundException("The source no longer exists.", path);
        if (!stamp.IsFolder) return new(path, false, stamp, Array.Empty<TransferNode>());
        PhysicalTransferStore.RejectDirectoryStreams(path);
        var children = Directory.EnumerateFileSystemEntries(path).Select(Read).ToArray();
        PhysicalTransferStore.RequireIdentity(path, stamp);
        return new(path, true, stamp, Array.AsReadOnly(children));
    }
}
