using System.Collections.Concurrent;
using System.Text.Json;
using KOTU.Core.Settings;
using KOTU.Module.Record;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class RecordingFolderCoordinatorTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KOTU-folder-coordinator-" + Guid.NewGuid().ToString("N"));
    private string X => Path.Combine(_root, "x");
    private string Y => Path.Combine(_root, "y");
    private string Z => Path.Combine(_root, "z");
    private string Known(Environment.SpecialFolder kind) => Path.Combine(_root, kind.ToString());
    private string Prepared(RecordingFolderSpec spec) => spec.ConfiguredPath.Length == 0
        ? Path.Combine(Known(Environment.SpecialFolder.MyDocuments), "KOTU") : spec.ConfiguredPath;
    private RecordingFolderCoordinator Create(ISettingsService settings, Func<RecordingFolderSpec, string>? prepare = null) =>
        new(settings, Known, prepare ?? Prepared);
    private TestSettings Existing()
    {
        var settings = new TestSettings();
        settings.Set(RecordingFolderSettings.Key, X);
        return settings;
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task CapturedSessionSurvivesACommitOrFailedCandidateBeforeItsQueuedPreparation(bool fail, int kindValue)
    {
        var settings = Existing();
        using var saveEntered = new ManualResetEventSlim();
        using var saveRelease = new ManualResetEventSlim();
        settings.OnSave = count =>
        {
            if (count != 1) return;
            saveEntered.Set();
            Assert.True(saveRelease.Wait(Limit));
            if (fail) throw new IOException("candidate failed");
        };
        var folders = Create(settings);
        await folders.EnsureInitializedAsync();
        var a = folders.CaptureCommitted()!;
        var preparationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedPreparation = Task.Run(async () =>
        {
            await preparationRelease.Task;
            return folders.PrepareCaptured(a.Spec!);
        });
        var pending = folders.ChangeAsync(Y, CancellationToken.None);
        try
        {
            Assert.True(saveEntered.Wait(Limit));
            Assert.Equal(X, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
            Assert.Equal(Y, settings.Get<string?>(RecordingFolderSettings.Key, null));
        }
        finally { saveRelease.Set(); }
        await pending.WaitAsync(Limit);
        preparationRelease.SetResult();
        var capturedFolder = await queuedPreparation.WaitAsync(Limit);
        Assert.Equal(X, capturedFolder);
        var expected = fail ? X : Y;
        Assert.Equal(expected, folders.CaptureCommitted()!.Spec!.ConfiguredPath);

        // 변경 이후에도 이미 준비된 세션의 임시 파일과 결과 폴더는 이전 목적지를 따른다.
        Directory.CreateDirectory(capturedFolder);
        var destination = RecordingOutput.CreateDestinationPath(capturedFolder, (RecordingKind)kindValue, DateTime.Now);
        var extension = (RecordingKind)kindValue != RecordingKind.Microphone ? ".mp4" : ".wav";
        var temporary = RecordingOutput.CreateTemporaryPath(destination, extension);
        File.WriteAllText(temporary, "closed synthetic recording");
        RecordingOutput.Publish(temporary, destination, overwrite: false);
        Assert.Equal(X, Path.GetDirectoryName(destination));
        Assert.EndsWith(extension, destination);
        Assert.Equal("closed synthetic recording", File.ReadAllText(destination));
    }

    [Fact]
    public async Task OneSharedInitializationOutlivesCanceledObservationAndPublishesBeforeReentrantRead()
    {
        var settings = new TestSettings();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var folders = Create(settings, spec =>
        {
            entered.Set();
            Assert.True(release.Wait(Limit));
            return Prepared(spec);
        });
        Task? reentrant = null;
        folders.Changed += () => reentrant = folders.EnsureInitializedAsync();
        var first = folders.EnsureInitializedAsync();
        Assert.True(entered.Wait(Limit));
        var second = folders.EnsureInitializedAsync();
        using var observer = new CancellationTokenSource();
        var canceledObservation = first.WaitAsync(observer.Token);
        observer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledObservation);
        release.Set();
        await second.WaitAsync(Limit);
        Assert.Same(first, second);
        Assert.Same(first, reentrant);
        Assert.True(folders.Current.Ready);
        Assert.Equal(1, settings.Saves);
    }

    [Fact]
    public async Task CancellationBeforeMutationWritesNothingButCancellationDuringSaveCannotRollbackSuccess()
    {
        var settings = Existing();
        using var cancellation = new CancellationTokenSource();
        var folders = Create(settings, spec =>
        {
            if (spec.ConfiguredPath == Y) cancellation.Cancel();
            return Prepared(spec);
        });
        await folders.EnsureInitializedAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => folders.ChangeAsync(Y, cancellation.Token));
        Assert.Equal(0, settings.Saves);
        Assert.Equal(X, folders.CaptureCommitted()!.Spec!.ConfiguredPath);

        using var afterSet = new CancellationTokenSource();
        settings.OnSave = _ => afterSet.Cancel();
        await folders.ChangeAsync(Z, afterSet.Token);
        Assert.Equal(Z, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
        Assert.Equal(Z, settings.Get<string?>(RecordingFolderSettings.Key, null));
        Assert.Equal(1, settings.Saves);
    }

    [Fact]
    public async Task NotificationsCanThrowReenterAndArriveLateWithoutChangingCommittedResult()
    {
        var settings = Existing();
        var folders = Create(settings);
        await folders.EnsureInitializedAsync();
        var callbacks = new ConcurrentQueue<Action>();
        var displayed = folders.Current;
        var disposed = false;
        Action observer = () => callbacks.Enqueue(() =>
        {
            if (!disposed && folders.Current.Revision >= displayed.Revision) displayed = folders.Current;
        });
        folders.Changed += () => throw new InvalidOperationException("closed observer");
        folders.Changed += observer;
        Task? reentered = null;
        folders.Changed += () =>
        {
            if (folders.Current.Spec?.ConfiguredPath == Y)
                reentered = folders.ChangeAsync(Z, CancellationToken.None);
        };
        await folders.ChangeAsync(Y, CancellationToken.None).WaitAsync(Limit);
        await reentered!.WaitAsync(Limit);
        Assert.Equal(Z, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
        foreach (var callback in callbacks.Reverse()) callback();
        Assert.Equal(Z, displayed.Spec!.ConfiguredPath);
        folders.Changed -= observer;
        disposed = true;
        await folders.ChangeAsync(X, CancellationToken.None);
        foreach (var callback in callbacks) callback();
        Assert.Equal(Z, displayed.Spec!.ConfiguredPath);
        Assert.Equal(X, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
    }

    [Fact]
    public async Task OlderStartFailureCannotPoisonNewChoiceAndRetryPreservesCommittedSpecification()
    {
        var settings = Existing();
        var folders = Create(settings);
        await folders.EnsureInitializedAsync();
        var x = folders.CaptureCommitted()!;
        await folders.ChangeAsync(Y, CancellationToken.None);
        await folders.ReportPreparationFailureAsync(x, "old X failure");
        Assert.True(folders.Current.Ready);
        Assert.Equal(Y, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
        var y = folders.CaptureCommitted()!;
        await folders.ReportPreparationFailureAsync(y, "current Y failure");
        Assert.Null(folders.CaptureCommitted());
        await folders.RetryAsync(CancellationToken.None);
        Assert.Equal(Y, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
        Assert.Equal(1, settings.Saves);
    }

    [Fact]
    public async Task CompetingSuccessThenFailureRestoresSuccessfulValueAndCanceledWaiterDoesNotWrite()
    {
        var settings = Existing();
        var folders = Create(settings);
        await folders.EnsureInitializedAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        settings.OnSave = count =>
        {
            if (count == 1) { entered.Set(); Assert.True(release.Wait(Limit)); }
            if (count == 2) throw new IOException("second choice failed");
        };
        var success = folders.ChangeAsync(Y, CancellationToken.None);
        Assert.True(entered.Wait(Limit));
        using var canceled = new CancellationTokenSource();
        var waiting = folders.ChangeAsync(X, canceled.Token);
        canceled.Cancel();
        var failed = folders.ChangeAsync(Z, CancellationToken.None);
        release.Set();
        await Task.WhenAll(success, failed).WaitAsync(Limit);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(3, settings.Saves);
        Assert.Equal(Y, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
        Assert.Equal(Y, settings.Get<string?>(RecordingFolderSettings.Key, null));
    }

    [Fact]
    public async Task DoubleFailureWarnsAllConsumersUntilExplicitSuccessfulSaveAndRetainsDiagnosticCause()
    {
        var settings = Existing();
        var folders = Create(settings);
        await folders.EnsureInitializedAsync();
        RecordingFolderState? a = null, b = null;
        folders.Changed += () => a = folders.Current;
        folders.Changed += () => b = folders.Current;
        settings.OnSave = count => throw new IOException(count == 1 ? "candidate failure" : "compensation failure");
        await folders.ChangeAsync(Y, CancellationToken.None);
        Assert.Equal(X, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
        Assert.Contains("candidate failure", folders.Current.Notice);
        Assert.Contains("compensation failure", folders.Current.PersistenceError);
        Assert.Contains("after restart may differ", folders.Current.PersistenceWarning);
        Assert.Same(a, b);
        await folders.RetryAsync(CancellationToken.None);
        Assert.NotNull(folders.Current.PersistenceWarning);
        Assert.Equal(2, settings.Saves);
        settings.OnSave = null;
        await folders.ChangeAsync(X, CancellationToken.None);
        Assert.Null(folders.Current.PersistenceWarning);
        Assert.Null(folders.Current.PersistenceError);
        Assert.Equal(3, settings.Saves);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    [InlineData("invalid")]
    public async Task FailedFirstChoiceRestoresTypedSemanticsAndCannotEnableStart(string kind)
    {
        var settings = new TestSettings();
        if (kind == "null") settings.Set<string?>(RecordingFolderSettings.Key, null);
        if (kind == "number") settings.Set(RecordingFolderSettings.Key, 42);
        if (kind == "invalid") settings.Set(RecordingFolderSettings.Key, "relative");
        settings.OnSave = _ => throw new IOException("disk unavailable");
        var folders = Create(settings);
        await folders.EnsureInitializedAsync();
        if (kind == "invalid") await folders.ChangeAsync(Y, CancellationToken.None);
        Assert.Null(folders.CaptureCommitted());
        Assert.NotNull(folders.Current.Error);
        Assert.NotNull(folders.Current.PersistenceWarning);
        Assert.Equal(kind == "invalid" ? "relative" : null, settings.Get<string?>(RecordingFolderSettings.Key, null));
        settings.OnSave = null;
        await folders.ChangeAsync(X, CancellationToken.None);
        Assert.Equal(X, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
        Assert.Null(folders.Current.PersistenceWarning);
    }

    [Fact]
    public async Task MissingDocumentsAndSelectedFileErrorsCanBeRetriedWithoutLegacyFallbackOrMutation()
    {
        var settings = new TestSettings();
        settings.Set(RecordingFolderSettings.Key, "");
        var documents = "";
        var folders = new RecordingFolderCoordinator(settings,
            kind => kind == Environment.SpecialFolder.MyDocuments ? documents : throw new IOException("legacy not allowed"));
        await folders.EnsureInitializedAsync();
        Assert.Null(folders.CaptureCommitted());
        documents = Known(Environment.SpecialFolder.MyDocuments);
        await folders.RetryAsync(CancellationToken.None);
        Assert.True(folders.Current.Ready);
        Assert.Equal("", settings.Get<string?>(RecordingFolderSettings.Key, null));
        Assert.Equal(0, settings.Saves);
        var blocked = Path.Combine(_root, "existing-file");
        File.WriteAllText(blocked, "sentinel");
        await folders.ChangeAsync(blocked, CancellationToken.None);
        Assert.True(folders.Current.Ready);
        Assert.NotNull(folders.Current.Notice);
        Assert.Equal("sentinel", File.ReadAllText(blocked));
        Assert.Equal(0, settings.Saves);
    }

    [Fact]
    public async Task RealJsonQueuedCandidateSnapshotIsRepairedByCompensationBeforeChangeCompletes()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        using var candidateEntered = new ManualResetEventSlim();
        using var failCandidate = new ManualResetEventSlim();
        using var unrelatedEntered = new ManualResetEventSlim();
        using var restoredMemory = new ManualResetEventSlim();
        using var releaseUnrelated = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<string>();
        var count = 0;
        var json = new JsonSettingsService(path, (_, content) =>
        {
            var current = Interlocked.Increment(ref count);
            writes.Enqueue(content);
            if (current == 2)
            {
                candidateEntered.Set();
                Assert.True(failCandidate.Wait(Limit));
                throw new IOException("candidate failed");
            }
            if (current == 3)
            {
                unrelatedEntered.Set();
                Assert.True(releaseUnrelated.Wait(Limit));
            }
            File.WriteAllText(path, content);
        });
        json.Set(RecordingFolderSettings.Key, X);
        await json.SaveAsync();
        var wrapper = new TestSettings(json);
        wrapper.BeforeSet = (key, value) =>
        {
            if (key == RecordingFolderSettings.Key && Equals(value, X))
                Assert.True(unrelatedEntered.Wait(Limit));
        };
        wrapper.AfterSet = (key, value) =>
        {
            if (key == RecordingFolderSettings.Key && Equals(value, X)) restoredMemory.Set();
        };
        var folders = Create(wrapper);
        await folders.EnsureInitializedAsync();
        var change = folders.ChangeAsync(Y, CancellationToken.None);
        try
        {
            Assert.True(candidateEntered.Wait(Limit));
            json.Set("unrelated", 17);
            var unrelated = json.SaveAsync();
            failCandidate.Set();
            Assert.True(unrelatedEntered.Wait(Limit));
            Assert.Equal(Y, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(writes.ElementAt(2))![RecordingFolderSettings.Key].GetString());
            Assert.True(restoredMemory.Wait(Limit));
            releaseUnrelated.Set();
            await Task.WhenAll(change, unrelated).WaitAsync(Limit);
        }
        finally { failCandidate.Set(); releaseUnrelated.Set(); }
        Assert.Equal(4, count);
        var reload = new JsonSettingsService(path);
        Assert.Equal(X, reload.Get<string?>(RecordingFolderSettings.Key, null));
        Assert.Equal(17, reload.Get("unrelated", 0));
        Assert.Equal(X, folders.CaptureCommitted()!.Spec!.ConfiguredPath);
        Assert.Null(folders.Current.PersistenceWarning);
        var restarted = Create(reload);
        await restarted.EnsureInitializedAsync();
        Assert.Equal(X, restarted.CaptureCommitted()!.Spec!.ConfiguredPath);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    public async Task RealJsonFailedMigrationRestoresUnresolvedNullAndSuccessfulRetryIsRestartIdempotent(string kind)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "typed-settings.json");
        var writes = 0;
        var settings = new JsonSettingsService(path, (_, content) =>
        {
            if (Interlocked.Increment(ref writes) == 2) throw new IOException("migration save failed");
            File.WriteAllText(path, content);
        });
        settings.Set("record.audioFolder", "");
        settings.Set("record.videoFolder", "");
        if (kind == "null") settings.Set<string?>(RecordingFolderSettings.Key, null);
        if (kind == "number") settings.Set(RecordingFolderSettings.Key, 42);
        await settings.SaveAsync();
        var folders = Create(settings);
        await folders.EnsureInitializedAsync();
        Assert.Null(folders.CaptureCommitted());
        Assert.Equal(3, writes);
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty(RecordingFolderSettings.Key).ValueKind);
        var reload = new JsonSettingsService(path);
        Assert.Null(reload.Get<string?>(RecordingFolderSettings.Key, null));
        Assert.Equal("", reload.Get<string?>("record.audioFolder", null));
        await folders.RetryAsync(CancellationToken.None);
        Assert.Equal(4, writes);
        Assert.Equal("", folders.CaptureCommitted()!.Spec!.ConfiguredPath);
        var restarted = Create(new JsonSettingsService(path));
        await restarted.EnsureInitializedAsync();
        Assert.Equal("", restarted.CaptureCommitted()!.Spec!.ConfiguredPath);
        Assert.Equal(4, writes);
    }

    private sealed class TestSettings(ISettingsService? inner = null) : ISettingsService
    {
        private readonly ISettingsService _inner = inner ?? new MemorySettingsService();
        private int _saves;
        public int Saves => Volatile.Read(ref _saves);
        public Action<int>? OnSave { get; set; }
        public Action<string, object?>? BeforeSet { get; set; }
        public Action<string, object?>? AfterSet { get; set; }
        public string FilePath => _inner.FilePath;
        public T Get<T>(string key, T defaultValue) => _inner.Get(key, defaultValue);
        public void Set<T>(string key, T value)
        {
            BeforeSet?.Invoke(key, value);
            _inner.Set(key, value);
            AfterSet?.Invoke(key, value);
        }
        public void Save()
        {
            var count = Interlocked.Increment(ref _saves);
            OnSave?.Invoke(count);
            _inner.Save();
        }
        public Task SaveAsync() { Save(); return Task.CompletedTask; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
