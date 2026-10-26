using KOTU.Core.Settings;
using KOTU.Core.Threading;

namespace KOTU.Module.Record;

internal sealed record RecordingFolderState(long Revision, RecordingFolderSpec? Spec, string? DisplayPath,
    string? Error, string? Notice, string? PersistenceWarning)
{
    public bool Ready => Spec is not null && DisplayPath is not null && Error is null;
    public string? PersistenceError { get; init; }
}

internal sealed class RecordingFolderCoordinator
{
    private readonly ISettingsService _settings;
    private readonly Func<Environment.SpecialFolder, string> _knownFolder;
    private readonly Func<RecordingFolderSpec, string> _prepare;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initializationGate = new();
    private Task? _initialization;
    private RecordingFolderState _current = new(0, null, null, null, null, null);

    public RecordingFolderCoordinator(ISettingsService settings,
        Func<Environment.SpecialFolder, string>? knownFolder = null,
        Func<RecordingFolderSpec, string>? prepare = null)
    {
        _settings = settings;
        _knownFolder = knownFolder ?? RecordingFolderSettings.KnownFolder;
        _prepare = prepare ?? (spec => RecordingOutput.PrepareFolder(spec,
            () => _knownFolder(Environment.SpecialFolder.MyDocuments)));
    }

    public RecordingFolderState Current => Volatile.Read(ref _current);
    public RecordingFolderState? CaptureCommitted() => Current is { Ready: true } state ? state : null;
    public event Action? Changed;

    // 세션 워커는 받아 둔 명세만 사용한다. 현재 설정이나 화면 캐시를 다시 읽지 않는다.
    public string PrepareCaptured(RecordingFolderSpec spec) => _prepare(spec);

    public Task EnsureInitializedAsync()
    {
        TaskCompletionSource completion;
        lock (_initializationGate)
        {
            if (_initialization is not null) return _initialization;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _initialization = completion.Task;
        }
        _ = CompleteInitializationAsync(completion);
        return completion.Task;
    }

    private async Task CompleteInitializationAsync(TaskCompletionSource completion)
    {
        try
        {
            await ExecuteAsync(InitializeOrRetry, CancellationToken.None).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    public async Task RetryAsync(CancellationToken cancellation)
    {
        await EnsureInitializedAsync().WaitAsync(cancellation).ConfigureAwait(false);
        await ExecuteAsync(InitializeOrRetry, cancellation).ConfigureAwait(false);
    }

    public Task ChangeAsync(string path, CancellationToken cancellation) => ExecuteAsync(token =>
    {
        try
        {
            var spec = new RecordingFolderSpec(RecordingFolderSettings.Normalize(path));
            var prepared = _prepare(spec);
            token.ThrowIfCancellationRequested();
            Persist(spec, prepared);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Fail("Could not change the save folder. " + ex.Message); }
    }, cancellation);

    public Task ReportPreparationFailureAsync(RecordingFolderState captured, string error) => ExecuteAsync(_ =>
    {
        if (ReferenceEquals(Current, captured))
            Publish(captured with { Error = error });
    }, CancellationToken.None);

    private void InitializeOrRetry(CancellationToken token)
    {
        try
        {
            var state = Current;
            var choice = state.Spec is { } committed
                ? new RecordingFolderChoice(committed, false)
                : RecordingFolderSettings.Resolve(_settings, _knownFolder);
            string path;
            try { path = _prepare(choice.Spec); }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                // 기존의 권위 있는 설정은 준비 실패 후에도 유지한다. 미저장 이관은 커밋하지 않는다.
                Publish(state with { Spec = choice.NeedsSave ? null : choice.Spec,
                    Error = "Could not prepare the save folder. " + ex.Message });
                return;
            }
            token.ThrowIfCancellationRequested();
            if (choice.NeedsSave) Persist(choice.Spec, path);
            else Publish(state with { Spec = choice.Spec, DisplayPath = path, Error = null, Notice = null });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Fail("Could not prepare the save folder. " + ex.Message); }
    }

    private void Persist(RecordingFolderSpec spec, string path)
    {
        var previous = _settings.Get<string?>(RecordingFolderSettings.Key, null);
        try
        {
            // 이 시점부터 취소하지 않는다. 저장 성공 뒤 닫힌 화면 때문에 롤백하면 안 된다.
            _settings.Set(RecordingFolderSettings.Key, spec.ConfiguredPath);
            _settings.Save();
        }
        catch (Exception saveError)
        {
            string? warning = Current.PersistenceWarning;
            string? persistenceError = Current.PersistenceError;
            var restoring = true;
            try
            {
                _settings.Set<string?>(RecordingFolderSettings.Key, previous);
                restoring = false;
                // 다른 설정의 대기 스냅샷에 후보가 포함됐을 수 있으므로 복원도 직렬 저장한다.
                _settings.Save();
            }
            catch (Exception restoreError)
            {
                persistenceError = (restoring ? "Could not restore the in-memory setting. " : "Could not save the restored setting. ") + restoreError.Message;
                warning = "The save folder was not changed. Settings could not be restored on disk; the folder after restart may differ. Choose a save folder again to retry.";
            }
            Fail("Could not save the folder setting. " + saveError.Message, warning, persistenceError);
            return;
        }
        Publish(new(0, spec, path, null, "Save folder updated. Applies to the next recording.", null));
    }

    private void Fail(string error, string? warning = null, string? persistenceError = null)
    {
        var state = Current;
        Publish(state with { Error = state.Spec is null ? error : state.Error,
            Notice = error, PersistenceWarning = warning ?? state.PersistenceWarning,
            PersistenceError = persistenceError ?? state.PersistenceError });
    }

    private void Publish(RecordingFolderState state) =>
        Volatile.Write(ref _current, state with { Revision = Current.Revision + 1 });

    private async Task ExecuteAsync(Action<CancellationToken> operation, CancellationToken cancellation)
    {
        var changed = false;
        try
        {
            using (await CancellableBatch.EnterAsync(_gate, cancellation).ConfigureAwait(false))
            {
                var before = Current;
                using var worker = new ModuleWorker("KOTU recording folder");
                try { await worker.Run(_ => operation(cancellation), cancellation).ConfigureAwait(false); }
                finally { changed = !ReferenceEquals(before, Current); }
            }
        }
        finally
        {
            // 잠금 밖에서 최신 상태를 알린다. 구독자의 실패나 재진입은 커밋 결과를 바꾸지 않는다.
            if (changed && Changed is { } handlers)
                foreach (Action handler in handlers.GetInvocationList())
                    try { handler(); }
                    catch { /* 닫힌 화면의 알림 실패는 다른 화면에 전파하지 않는다. */ }
        }
    }
}
