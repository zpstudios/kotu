using System.Diagnostics;
using KOTU.Core.Jobs;
using KOTU.Core.Threading;

namespace KOTU.Module.Archive;

/// <summary>뷰를 캡처하지 않는 압축 작업 어댑터. 작업 입력을 복제하고 네이티브 실행 워커를 소유한다.</summary>
public sealed class ArchiveJobCoordinator
{
    private static readonly object DestinationGate = new();
    private static readonly HashSet<string> Destinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly IArchiveBackend _backend;
    private readonly Action<string> _openFolder;
    public BackgroundJobService Jobs { get; }

    public ArchiveJobCoordinator(BackgroundJobService jobs)
        : this(jobs, new SevenZipBackend(), OpenFolderInExplorer) { }

    internal ArchiveJobCoordinator(BackgroundJobService jobs, IArchiveBackend backend)
        : this(jobs, backend, _ => { }) { }

    internal ArchiveJobCoordinator(BackgroundJobService jobs, IArchiveBackend backend, Action<string> openFolder)
    {
        Jobs = jobs;
        _backend = backend;
        _openFolder = openFolder;
    }

    /// <summary>선택한 압축마다 즉시 작업을 등록한다. 목록 조회·암호 대기도 작업 수명에 속한다.</summary>
    public IReadOnlyList<BackgroundJobHandle> StartExtractMany(IReadOnlyList<string> archivePaths, bool forceFolder, string? password = null)
    {
        var operations = new List<(BackgroundJobRequest, Func<BackgroundJobContext, Task>)>();
        foreach (var path in archivePaths.ToArray())
        {
            var backend = _backend;
            operations.Add((new BackgroundJobRequest(BackgroundJobKind.ExtractArchive,
                "Extract: " + Path.GetFileName(path), path, null),
                context => ExtractPlannedAsync(backend, path, forceFolder, password, context)));
        }
        var handles = Jobs.StartMany(operations);
        _ = OpenFirstSuccessfulResultAsync(handles, _openFolder);
        return handles;
    }

    private static async Task ExtractPlannedAsync(IArchiveBackend backend, string archivePath,
        bool forceFolder, string? password, BackgroundJobContext context)
    {
        using var worker = new ModuleWorker("KOTU archive planning job");
        ExtractHerePlan? plan = null;
        try
        {
            while (true)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                var attempt = password;
                password = null;
                try
                {
                    await worker.Run(_ =>
                    {
                        if (plan is null)
                        {
                            var entries = backend.List(archivePath, attempt);
                            context.Cancellation.ThrowIfCancellationRequested();
                            var names = ArchiveEntryTree.Build(entries).Children.Select(entry => entry.Name).ToArray();
                            lock (DestinationGate)
                            {
                                plan = ExtractHerePlanner.Plan(Path.GetFullPath(archivePath), names,
                                    path => File.Exists(path) || Directory.Exists(path) || Destinations.Contains(path), forceFolder);
                                Destinations.Add(plan.ResultPath);
                            }
                            context.SetResultPath(plan.ResultPath);
                        }
                        backend.Extract(archivePath, plan.TargetDirectory, null, attempt,
                            context.Progress, context.Cancellation);
                    }, context.Cancellation).ConfigureAwait(false);
                    context.Cancellation.ThrowIfCancellationRequested();
                    return;
                }
                catch (ArchivePasswordException)
                {
                    context.Cancellation.ThrowIfCancellationRequested();
                }
                finally { attempt = null; }
                password = await context.RequestPasswordAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            password = null;
            if (plan is not null) lock (DestinationGate) Destinations.Remove(plan.ResultPath);
        }
    }

    public BackgroundJobHandle StartExtract(string archivePath, string targetDirectory,
        IReadOnlyCollection<string>? entryPaths = null, string? password = null,
        string? resultPath = null, bool openEntry = false)
    {
        var input = new ExtractInput(archivePath, targetDirectory,
            entryPaths is null ? null : Array.AsReadOnly(entryPaths.ToArray()));
        var backend = _backend;
        var request = new BackgroundJobRequest(
            openEntry ? BackgroundJobKind.OpenArchiveEntry : BackgroundJobKind.ExtractArchive,
            (openEntry ? "Open entry: " : "Extract: ") + Path.GetFileName(archivePath),
            archivePath, resultPath ?? targetDirectory);
        var handle = Jobs.Start(request, context => ExtractAsync(backend, input, password, context));
        if (!openEntry) _ = OpenFirstSuccessfulResultAsync([handle], _openFolder);
        return handle;
    }

    /// <summary>
    /// 한 번의 사용자 해제 요청에는 탐색기 창을 하나만 연다. 일괄 해제는 모든 작업이 끝난 뒤
    /// 선택 순서상 첫 성공 결과를 사용하며, 실패·취소·임시 항목 열기는 대상이 아니다.
    /// 뷰 수명과 분리해 사용자가 압축 화면을 떠난 뒤 완료돼도 같은 동작을 보장한다.
    /// </summary>
    private static async Task OpenFirstSuccessfulResultAsync(
        IReadOnlyList<BackgroundJobHandle> handles, Action<string> openFolder)
    {
        try
        {
            var results = await Task.WhenAll(handles.Select(handle => handle.Completion)).ConfigureAwait(false);
            var folder = results
                .Where(result => result.State == BackgroundJobState.Succeeded)
                .Select(result => ResultFolder(result.ResultPath))
                .FirstOrDefault(path => path is not null);
            if (folder is not null) openFolder(folder);
        }
        catch
        {
            // 결과 폴더 열기는 부가 동작이다. 해제 결과와 작업 상태를 뒤집지 않는다.
        }
    }

    private static string? ResultFolder(string? resultPath)
    {
        if (string.IsNullOrWhiteSpace(resultPath)) return null;
        if (Directory.Exists(resultPath)) return resultPath;
        return File.Exists(resultPath) ? Path.GetDirectoryName(resultPath) : null;
    }

    private static void OpenFolderInExplorer(string folder)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(folder);
        Process.Start(start);
    }

    public BackgroundJobHandle StartCreate(IReadOnlyList<string> sourcePaths, string targetPath,
        bool use7z, string? password = null)
    {
        var input = new CreateInput(Array.AsReadOnly(sourcePaths.ToArray()), targetPath, use7z);
        var backend = _backend;
        var request = new BackgroundJobRequest(BackgroundJobKind.CreateArchive,
            "Create: " + Path.GetFileName(targetPath), input.SourcePaths.FirstOrDefault(), targetPath);
        return Jobs.Start(request, context => CreateAsync(backend, input, password, context));
    }

    private static async Task ExtractAsync(IArchiveBackend backend, ExtractInput input,
        string? password, BackgroundJobContext context)
    {
        using var worker = new ModuleWorker("KOTU archive extraction job");
        while (true)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var attempt = password;
            password = null;
            try
            {
                await worker.Run(_ => backend.Extract(input.ArchivePath, input.TargetDirectory,
                    input.EntryPaths, attempt, context.Progress, context.Cancellation),
                    context.Cancellation).ConfigureAwait(false);
                context.Cancellation.ThrowIfCancellationRequested();
                return;
            }
            catch (ArchivePasswordException)
            {
                context.Cancellation.ThrowIfCancellationRequested();
            }
            finally
            {
                attempt = null;
            }
            // 대화상자나 이전 뷰가 아닌 앱 작업 패널이 다음 암호를 제공한다.
            password = await context.RequestPasswordAsync().ConfigureAwait(false);
        }
    }

    private static async Task CreateAsync(IArchiveBackend backend, CreateInput input,
        string? password, BackgroundJobContext context)
    {
        using var worker = new ModuleWorker("KOTU archive creation job");
        var target = Path.GetFullPath(input.TargetPath);
        lock (DestinationGate)
        {
            if (!Destinations.Add(target)) throw new IOException("Another archive job is writing this destination.");
        }
        try
        {
            await worker.Run(_ =>
            {
                if (input.Use7z) backend.Create7z(input.SourcePaths, input.TargetPath,
                    password, context.Progress, context.Cancellation);
                else backend.CreateZip(input.SourcePaths, input.TargetPath,
                    password, context.Progress, context.Cancellation);
            }, context.Cancellation).ConfigureAwait(false);
            context.Cancellation.ThrowIfCancellationRequested();
        }
        finally
        {
            password = null;
            lock (DestinationGate) Destinations.Remove(target);
        }
    }

    private sealed record ExtractInput(string ArchivePath, string TargetDirectory, IReadOnlyCollection<string>? EntryPaths);
    private sealed record CreateInput(IReadOnlyList<string> SourcePaths, string TargetPath, bool Use7z);
}
