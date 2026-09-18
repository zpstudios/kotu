using System.ComponentModel;
using KOTU.Core.Jobs;

namespace KOTU.Module.Archive;

/// <summary>선택 순서와 완료 결과를 화면 수명 동안 유지한다. 공용 이력에서 지워져도 결과를 잃지 않는다.</summary>
public sealed class ArchiveBatchPresentation
{
    private readonly Dictionary<Guid, ArchiveBatchItem> _byId;
    private readonly Dictionary<Guid, BackgroundJobHandle> _handles;
    public IReadOnlyList<ArchiveBatchItem> Items { get; }
    public string Summary => $"Archive extraction — {Items.Count(item => item.Snapshot is { IsActive: false })}/{Items.Count} finished";

    public ArchiveBatchPresentation(IReadOnlyList<BackgroundJobHandle> handles, IReadOnlyList<string> paths)
    {
        if (handles.Count != paths.Count) throw new ArgumentException("Each selection must have a job.");
        Items = Array.AsReadOnly(handles.Select((handle, index) => new ArchiveBatchItem(handle.Id, paths[index])).ToArray());
        _byId = Items.ToDictionary(item => item.Id);
        _handles = handles.ToDictionary(handle => handle.Id);
    }

    /// <summary>화면의 관찰 취소는 완료 task를 버리지 않는다. 재부착 시 새 토큰으로 다시 관찰한다.</summary>
    public Task<BackgroundJobSnapshot> WaitForCompletionAsync(Guid id, CancellationToken observation)
        => _handles[id].Completion.WaitAsync(observation);

    public void Update(IEnumerable<BackgroundJobSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
            if (_byId.TryGetValue(snapshot.Id, out var item)) item.Update(snapshot);
    }
}

public sealed class ArchiveBatchItem : INotifyPropertyChanged
{
    public Guid Id { get; }
    public string Name { get; }
    public BackgroundJobSnapshot? Snapshot { get; private set; }
    private string? _openError;
    internal ArchiveBatchItem(Guid id, string path) { Id = id; Name = Path.GetFileName(path); }
    public event PropertyChangedEventHandler? PropertyChanged;

    public double Progress => (Snapshot?.Progress ?? 0) * 100;
    public bool IsIndeterminate => Snapshot is null || Snapshot.State == BackgroundJobState.Running && Snapshot.Progress == 0;
    public bool CanOpen => Snapshot?.State == BackgroundJobState.Succeeded && !string.IsNullOrWhiteSpace(Snapshot.ResultPath);
    public bool CanCancel => Snapshot?.IsActive == true && Snapshot.State != BackgroundJobState.Canceling;
    public string ResultPath => Snapshot?.ResultPath ?? string.Empty;
    public string Status => _openError ?? (Snapshot switch
    {
        { State: BackgroundJobState.Running } snapshot => snapshot.Progress == 0 ? "Preparing or extracting…" : $"Extracting — {snapshot.Progress:P0}",
        { State: BackgroundJobState.WaitingForPassword } => "Password required — use Jobs in the upper-right corner.",
        { State: BackgroundJobState.Canceling } => "Canceling after the current file. Created files remain.",
        { State: BackgroundJobState.Succeeded } => "Completed",
        { State: BackgroundJobState.Failed } snapshot => snapshot.Error ?? "Extraction failed. See Jobs for details.",
        { State: BackgroundJobState.Canceled } => "Canceled. Already-created files remain.",
        _ => "Preparing…"
    });

    internal void Update(BackgroundJobSnapshot snapshot)
    {
        // 완료 task가 준 정본은 늦게 도착한 진행 알림으로 되돌리지 않는다.
        if (Snapshot is { IsActive: false }) return;
        Snapshot = snapshot;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void ReportOpenError()
    {
        _openError = "Could not open the result folder. It may have moved or been removed.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }
}
