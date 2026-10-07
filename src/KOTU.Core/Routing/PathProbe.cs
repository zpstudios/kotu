namespace KOTU.Core.Routing;

/// <summary>동기 경로 질의. 반드시 워커에서 호출하고 UI 반영 전에 요청 수명을 대조한다.</summary>
public static class PathProbe
{
    public static string ExistingAncestor(string folder, CancellationToken cancellation,
        Func<string, bool>? exists = null)
    {
        exists ??= Directory.Exists;
        var target = folder;
        while (target.Length > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (exists(target)) return target;
            target = Directory.GetParent(target)?.FullName ?? string.Empty;
        }
        return folder; // 기존 스캔 오류·빈 목록·감시 해제 경로를 유지한다.
    }
}
