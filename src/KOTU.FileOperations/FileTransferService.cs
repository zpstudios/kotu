namespace KOTU.FileOperations;

public enum TransferChoice { Replace, Skip, KeepBoth }
public sealed record TransferDecision(TransferChoice Choice, bool ApplyToAll);
public sealed record TransferConflict(string Name, bool IsFolder, string DestinationFolder, bool OfferAll);
public sealed record TransferResult(int Done, int Skipped, int Failed, string? FirstError,
    bool Cancelled, int Total, int Denied, bool SourcesRemain = false);

public sealed class FileTransferService
{
    private readonly bool _forceCopyForMoves;
    public FileTransferService() { }
    internal FileTransferService(bool forceCopyForMoves) => _forceCopyForMoves = forceCopyForMoves;

    public Task<TransferResult> TransferAsync(IReadOnlyList<string> paths, string targetFolder, bool move,
        Func<TransferConflict, Task<TransferDecision?>> resolveConflict, Action<int>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(resolveConflict);
        var snapshot = paths.ToArray();
        return Task.Run(() => new Execution(move, resolveConflict, reportProgress, cancellationToken, _forceCopyForMoves)
            .Run(snapshot, targetFolder));
    }

    private sealed class Execution(bool move, Func<TransferConflict, Task<TransferDecision?>> resolve,
        Action<int>? progress, CancellationToken cancellation, bool forceCopyForMoves)
    {
        private TransferChoice? _sticky;
        private bool _cancelled;
        private bool _itemSkipped;
        private string? _itemError;
        private bool _itemDenied;
        private int _skipCount;
        private int _errorCount;

        internal async Task<TransferResult> Run(string[] paths, string targetFolder)
        {
            int done = 0, skipped = 0, failed = 0, denied = 0;
            string? firstError = null;
            string target;
            Microsoft.Win32.SafeHandles.SafeFileHandle? targetHandle = null;
            PhysicalTransferStore.Handles? targetAncestors = null;
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("File transfers require Windows.");
                target = PhysicalTransferStore.Normalize(targetFolder);
                targetAncestors = PhysicalTransferStore.LockAncestors(target);
                if (PhysicalTransferStore.Inspect(target) is not { IsFolder: true })
                    throw new DirectoryNotFoundException("The destination folder does not exist.");
                targetHandle = PhysicalTransferStore.LockDirectory(target);
            }
            catch (Exception ex) { targetHandle?.Dispose(); targetAncestors?.Dispose(); return new(0, 0, paths.Length, ex.Message, false, paths.Length, IsDenied(ex) ? paths.Length : 0, move); }

            using (targetHandle)
            using (targetAncestors)
            {
            for (var i = 0; i < paths.Length && !_cancelled; i++)
            {
                if (cancellation.IsCancellationRequested) { _cancelled = true; break; }
                _itemSkipped = false; _itemError = null; _itemDenied = false;
                try
                {
                    progress?.Invoke(i + 1);
                    var source = PhysicalTransferStore.Normalize(paths[i]);
                    // 수집 뒤 부모 폴더까지 사라진 항목도 기존과 같이 건너뛴다. 존재하는 항목은 아래에서 경로를 검증한다.
                    if (PhysicalTransferStore.Inspect(source) is null) { skipped++; continue; }
                    using (var ancestors = PhysicalTransferStore.LockAncestors(source))
                    {
                        if (PhysicalTransferStore.Inspect(source) is null) { skipped++; continue; }
                    }
                    if (PhysicalTransferStore.Equal(source, Path.GetPathRoot(source)!)) throw new IOException("Cannot transfer a drive root.");
                    var node = TransferPlanner.Read(source);
                    if (node.IsFolder && (PhysicalTransferStore.Within(target, source) ||
                        PhysicalTransferStore.HasAncestorIdentity(target, node.Stamp)))
                        throw new IOException("Cannot put a folder inside itself.");
                    if (move && PhysicalTransferStore.Equal(Path.GetDirectoryName(source)!, target)) { skipped++; continue; }
                    var destination = Path.Combine(target, Path.GetFileName(source));
                    if (PhysicalTransferStore.Equal(source, destination)) destination = Unique(target, Path.GetFileName(source));
                    var offerAll = node.IsFolder || paths.Skip(i + 1).Any(p =>
                    {
                        try { return PhysicalTransferStore.Inspect(Path.Combine(target, Path.GetFileName(PhysicalTransferStore.Normalize(p)))) is not null; }
                        catch { return false; }
                    });
                    await Execute(node, destination, true, offerAll);
                    if (_cancelled) break;
                    if (_itemError is not null) { failed++; if (_itemDenied) denied++; firstError ??= _itemError; }
                    else if (_itemSkipped) skipped++;
                    else done++;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _cancelled = true; }
                catch (Exception ex) { failed++; if (IsDenied(ex)) denied++; firstError ??= ex.Message; }
            }
            var remains = move && paths.Any(path =>
            {
                try { return PhysicalTransferStore.Inspect(path) is not null; }
                catch { return true; }
            });
            return new(done, skipped, failed, firstError, _cancelled, paths.Length, denied, remains);
            }
        }

        private async Task Execute(TransferNode node, string destination, bool topLevel, bool offerAll)
        {
            if (_cancelled) return;
            cancellation.ThrowIfCancellationRequested();
            PhysicalTransferStore.RequireIdentity(node.Path, node.Stamp);
            EntryStamp? existing;
            using (var ancestors = PhysicalTransferStore.LockAncestors(destination)) existing = PhysicalTransferStore.Inspect(destination);
            if (existing is not null && (topLevel || !node.IsFolder))
            {
                var choice = _sticky;
                if (choice is null)
                {
                    var decision = await resolve(new(Path.GetFileName(node.Path), node.IsFolder, Path.GetDirectoryName(destination)!, offerAll));
                    if (decision is null) { _cancelled = true; return; }
                    choice = decision.Choice;
                    if (decision.ApplyToAll) _sticky = choice;
                }
                cancellation.ThrowIfCancellationRequested();
                using (var ancestors = PhysicalTransferStore.LockAncestors(destination)) PhysicalTransferStore.RequireUnchanged(destination, existing);
                PhysicalTransferStore.RequireIdentity(node.Path, node.Stamp);
                if (choice == TransferChoice.Skip) { _itemSkipped |= topLevel; _skipCount++; return; }
                if (choice == TransferChoice.KeepBoth)
                {
                    destination = Unique(Path.GetDirectoryName(destination)!, Path.GetFileName(destination));
                    existing = null;
                }
            }
            if (existing is not null && existing.IsFolder != node.IsFolder) throw new IOException("A different item type is in the way: " + destination);
            // 충돌 없는 같은 볼륨 이동은 핸들 이름변경으로 끝낸다. 병합과 대치는 검증 복사 경로를 쓴다.
            if (move && !forceCopyForMoves && existing is null && PhysicalTransferStore.TryRename(node, destination, cancellation)) return;
            if (!node.IsFolder)
            {
                PhysicalTransferStore.CopyFile(node, destination, move, existing, cancellation);
                return;
            }
            if (existing is null) PhysicalTransferStore.CreateFolder(destination);
            // 병합은 각 자식 실패를 격리한다. 원본 디렉터리의 새 항목을 따라가지 않는다.
            var skipsBefore = _skipCount;
            var errorsBefore = _errorCount;
            using (var sourceAncestors = PhysicalTransferStore.LockAncestors(node.Path))
            using (var sourceHandle = PhysicalTransferStore.LockDirectory(node.Path, node.Stamp))
            using (var destinationAncestors = PhysicalTransferStore.LockAncestors(destination))
            using (var destinationHandle = PhysicalTransferStore.LockDirectory(destination, existing))
            {
            foreach (var child in node.Children)
            {
                if (_cancelled) return;
                cancellation.ThrowIfCancellationRequested();
                try { await Execute(child, Path.Combine(destination, Path.GetFileName(child.Path)), false, true); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) { _itemError ??= ex.Message; _itemDenied |= IsDenied(ex); _errorCount++; }
            }
            }
            if (move && !_cancelled && _skipCount == skipsBefore && _errorCount == errorsBefore)
                PhysicalTransferStore.RemoveEmptySource(node);
        }

        private static string Unique(string target, string name)
        {
            var destination = Path.Combine(target, name);
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var index = 2; PhysicalTransferStore.Inspect(destination) is not null; index++)
                destination = Path.Combine(target, $"{stem} ({index}){extension}");
            return destination;
        }

        private static bool IsDenied(Exception ex) => ex is UnauthorizedAccessException || (ex.HResult & 0xffff) is 5 or 1314;
    }
}
