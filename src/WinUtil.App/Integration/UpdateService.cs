using Velopack;
using Velopack.Sources;

namespace WinUtil.App.Integration;

/// <summary>
/// GitHub Releases를 피드로 쓰는 자동 업데이트(Velopack).
/// Setup.exe로 설치했거나 Velopack 포터블 패키지로 실행한 경우에만 동작하고,
/// 수동 zip 실행에서는 조용히 비활성이다.
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/zpstudios/zpro";

    private static UpdateManager CreateManager() =>
        new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    /// <summary>Velopack 관리 하에 실행 중인지(설치판/Velopack 포터블).</summary>
    public static bool IsUpdatableBuild
    {
        get
        {
            try { return CreateManager().IsInstalled; }
            catch { return false; }
        }
    }

    /// <summary>업데이트 확인. 업데이트 불가 빌드거나 최신이면 null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        var manager = CreateManager();
        if (!manager.IsInstalled) return null;
        return await manager.CheckForUpdatesAsync();
    }

    /// <summary>업데이트 다운로드. 진행률(0~100)을 콜백으로 알린다(백그라운드 스레드에서 호출됨).</summary>
    public static async Task DownloadAsync(UpdateInfo info, Action<int>? progress = null)
    {
        var manager = CreateManager();
        await manager.DownloadUpdatesAsync(info, progress);
    }

    /// <summary>다운로드된 업데이트를 적용하고 앱을 재시작한다.</summary>
    public static void ApplyAndRestart(UpdateInfo info) =>
        CreateManager().ApplyUpdatesAndRestart(info);
}
