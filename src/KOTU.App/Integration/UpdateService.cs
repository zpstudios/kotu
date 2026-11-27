using Microsoft.Extensions.DependencyInjection;
using Velopack;
using Velopack.Sources;
using KOTU.Core.Threading;
using KOTU.Core.Updates;
using System.Diagnostics;

namespace KOTU.App.Integration;

/// <summary>
/// GitHub Releases를 피드로 쓰는 자동 업데이트(Velopack).
/// Setup.exe로 설치했거나 Velopack 포터블 패키지로 실행한 경우에만 동작하고,
/// 수동 zip 실행에서는 조용히 비활성이다.
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/zpstudios/kotu";
    internal static string LastRestartError { get; private set; } = string.Empty;

    private static UpdateManager CreateManager() =>
        new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

    /// <summary>Velopack 관리 하에 실행 중인지(설치판/Velopack 포터블).</summary>
    public static bool IsUpdatableBuild
    {
        get
        {
            if (!KOTU.Core.Integration.DistributionPolicy.IsStableDistribution) return false;
            try { return CreateManager().IsInstalled; }
            catch { return false; }
        }
    }

    /// <summary>업데이트 확인. 업데이트 불가 빌드거나 최신이면 null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        if (!KOTU.Core.Integration.DistributionPolicy.IsStableDistribution) return null;
        var manager = CreateManager();
        if (!manager.IsInstalled) return null;
        return await manager.CheckForUpdatesAsync();
    }

    /// <summary>업데이트 다운로드. 진행률(0~100)을 콜백으로 알린다(백그라운드 스레드에서 호출됨).</summary>
    public static async Task DownloadAsync(UpdateInfo info, Action<int>? progress = null)
    {
        if (!KOTU.Core.Integration.DistributionPolicy.IsStableDistribution) return;
        var manager = CreateManager();
        await manager.DownloadUpdatesAsync(info, progress);
    }

    /// <summary>다운로드된 업데이트를 적용하고 앱을 재시작한다.</summary>
    public static async Task<bool> ApplyAndRestartAsync(UpdateInfo info)
    {
        if (!KOTU.Core.Integration.DistributionPolicy.IsStableDistribution) return false;
        LastRestartError = "Restart was postponed. Finish or cancel active jobs, close other KOTU processes, and try again.";
        var windows = App.Services.GetRequiredService<WindowManager>();
        using var worker = new ModuleWorker("KOTU update restart", ThreadPriority.BelowNormal);
        UpdateRestartTransaction? transaction = null;
        var applied = false;
        try
        {
            if (!await worker.Run(_ => IsUpdatableBuild && IsOnlyInstalledProcess())) return false;
            transaction = await windows.PrepareUpdateRestartAsync();
            if (transaction is null) return false;
            await windows.FlushSettingsAsync();
            if (!await worker.Run(_ => IsOnlyInstalledProcess()) || !windows.IsUpdateRestartValid) return false;
            applied = await worker.Run(_ =>
            {
                if (windows.RestartWasInterrupted) return false;
                CreateManager().ApplyUpdatesAndRestart(info);
                return true;
            });
            return applied;
        }
        catch (Exception ex)
        {
            LastRestartError = "KOTU could not restart. Your open windows have been kept. " + ex.Message;
            return false;
        }
        finally
        {
            if (!applied)
            {
                try { if (transaction is not null) await transaction.DisposeAsync(); }
                finally { windows.CancelUpdateRestart(); }
            }
        }
    }

    private static bool IsOnlyInstalledProcess()
    {
        var current = Environment.ProcessPath;
        if (current is null) return false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(current)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.HasExited) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null || string.Equals(Path.GetFullPath(path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) return false;
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch { return false; } // 승격된 다른 프로세스를 조회할 수 없어도 설치하지 않는다.
            }
        }
        return true;
    }
}
