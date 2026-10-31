using KOTU.Core.Contracts;

namespace KOTU.Core.Content;

/// <summary>준비 시점의 공급자와 작업명을 고정하고 교체된 문서의 페이지가 섞이는 것을 막는다.</summary>
public sealed class PreparedPrintPageProvider(IPrintPageProvider provider, string jobName, Func<bool> isCurrent) : IPrintPageProvider
{
    public bool CanPrintNow => isCurrent();
    public string PrintJobName => jobName;
    public event Action? PrintRequested { add { } remove { } }
    public int GetPrintPageCount(PrintPageSpec spec) => isCurrent() ? provider.GetPrintPageCount(spec) : 0;
    public async Task<object?> CreatePrintPageAsync(int pageNumber, PrintPageSpec spec)
    {
        if (!isCurrent()) return null;
        var page = await provider.CreatePrintPageAsync(pageNumber, spec);
        return isCurrent() ? page : null;
    }
}
