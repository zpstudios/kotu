using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace KOTU.App.Integration;

/// <summary>
/// 트레이 아이콘 자동 승격(A100 ②). Win11은 guidItem 없는 트레이 아이콘을
/// HKCU\Control Panel\NotifyIconSettings 항목으로 관리하는데, 처음 보는 식별
/// (예: 새 슬롯의 최초 실행)의 항목은 기본 '끔'으로 생겨 사용자가 수동으로 켜야 보인다.
/// ①(v0.126.0)의 결정적 식별로 항목 증식은 멈췄지만 이 최초 1회 수동 켜기가 남아,
/// NIM_ADD 후 이 키를 스캔해 자기 exe 항목의 IsPromoted(DWORD)=1을 직접 써서 없앤다.
/// IsPromoted=1은 탐색기 재시작 없이 즉시 반영된다.
/// 항목의 ExecutablePath 값은 known-folder GUID 접두 형태(예: "{GUID}\...\KOTU.exe")일 수
/// 있어 GUID 접두사를 실제 경로로 해석한 뒤 현재 실행 파일과 비교한다.
/// 비공식 레지스트리 직접 쓰기(A38 UserChoice와 같은 성격)라 실패는 전부 조용히 무시한다
/// (현상 유지 폴백 — 승격이 안 되면 지금처럼 사용자가 수동으로 켜면 될 뿐, 동작은 정상).
/// </summary>
internal static class TrayPromotion
{
    /// <summary>
    /// 승격 스캔을 예약한다(fire-and-forget). NotifyIconSettings 항목은 NIM_ADD 직후가
    /// 아니라 비동기로 생기므로 호출 후 약 1초/4초/10초 시점에 3회 스캔한다.
    /// 쓰기가 멱등이라 체인이 겹쳐도 무해 — 조기 종료·디바운스·잠금을 일부러 두지 않는다.
    /// </summary>
    public static void Request()
    {
        if (!KOTU.Core.Integration.DistributionPolicy.IsStableDistribution) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000);
                Promote();
                await Task.Delay(3000);
                Promote();
                await Task.Delay(6000);
                Promote();
            }
            catch
            {
                // 비공식 키라 어떤 실패(권한·정책·OS 변경)도 앱 동작에 영향을 주면 안 된다.
            }
        });
    }

    /// <summary>NotifyIconSettings를 1회 스캔해 자기 exe의 꺼진 항목을 IsPromoted=1로 만든다.</summary>
    private static void Promote()
    {
        if (!KOTU.Core.Integration.DistributionPolicy.IsStableDistribution) return;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: false);
        if (root is null) return; // 키 부재(구 Windows 등) = 승격 개념 자체가 없음 — 무동작

        foreach (var name in root.GetSubKeyNames())
        {
            // 항목마다 개별 try/catch — 한 항목이 권한·동시 삭제로 던져도 나머지는 계속 본다.
            try
            {
                using var entry = root.OpenSubKey(name, writable: true);
                if (entry is null) continue;
                var path = entry.GetValue("ExecutablePath") as string;
                if (path is null) continue;
                if (!MatchesExecutable(path, exePath)) continue;
                if (entry.GetValue("IsPromoted") is int v && v == 1) continue; // 이미 켜짐 — 불필요한 쓰기(churn) 방지
                entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord); // 종류 명시 — 문자열로 쓰면 조용히 무효
            }
            catch
            {
                // 이 항목만 포기하고 다음 항목으로.
            }
        }
    }

    private static bool MatchesExecutable(string registeredPath, string exePath)
    {
        // Explorer also stores paths such as "{FOLDERID_LocalAppData}\KOTU\current\KOTU.exe".
        var separator = registeredPath.IndexOf('\\');
        if (separator > 0 && Guid.TryParse(registeredPath[..separator], out var folderId))
        {
            var result = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var folder);
            try
            {
                if (result < 0 || Marshal.PtrToStringUni(folder) is not { } resolved) return false;
                registeredPath = Path.Combine(resolved, registeredPath[(separator + 1)..]);
            }
            finally { Marshal.FreeCoTaskMem(folder); }
        }
        return string.Equals(registeredPath, exePath, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
