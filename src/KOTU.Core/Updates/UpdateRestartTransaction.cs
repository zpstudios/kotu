namespace KOTU.Core.Updates;

/// <summary>창별 닫기 승인을 모두 확보한다. 취소/저장/적용 실패는 역순으로 원래 편집을 복구한다.</summary>
public sealed class UpdateRestartTransaction : IAsyncDisposable
{
    private List<IAsyncDisposable>? _leases = [];

    public static async Task<UpdateRestartTransaction?> PrepareAsync(IEnumerable<Func<Task<IAsyncDisposable?>>> prepare)
    {
        var transaction = new UpdateRestartTransaction();
        try
        {
            foreach (var acquire in prepare)
            {
                var lease = await acquire();
                if (lease is null) { await transaction.DisposeAsync(); return null; }
                transaction._leases!.Add(lease);
            }
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        var leases = Interlocked.Exchange(ref _leases, null);
        if (leases is null) return;
        List<Exception>? errors = null;
        for (var i = leases.Count - 1; i >= 0; i--)
        {
            try { await leases[i].DisposeAsync(); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        if (errors is not null) throw new AggregateException(errors);
    }
}
