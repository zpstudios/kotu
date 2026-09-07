using KOTU.Core.Jobs;
using KOTU.Core.Threading;

namespace KOTU.Module.Archive;

/// <summary>뷰를 캡처하지 않는 압축 작업 어댑터. 작업 입력을 복제하고 네이티브 실행 워커를 소유한다.</summary>
public sealed class ArchiveJobCoordinator
{
    private readonly IArchiveBackend _backend;
    public BackgroundJobService Jobs { get; }

    public ArchiveJobCoordinator(BackgroundJobService jobs) : this(jobs, new SevenZipBackend()) { }

    public ArchiveJobCoordinator(BackgroundJobService jobs, IArchiveBackend backend)
    {
        Jobs = jobs;
        _backend = backend;
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
        return Jobs.Start(request, context => ExtractAsync(backend, input, password, context));
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
        }
    }

    private sealed record ExtractInput(string ArchivePath, string TargetDirectory, IReadOnlyCollection<string>? EntryPaths);
    private sealed record CreateInput(IReadOnlyList<string> SourcePaths, string TargetPath, bool Use7z);
}

