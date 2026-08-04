using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinUtil.Core.Cli;
using WinUtil.Core.Contracts;

namespace WinUtil.App.Integration;

/// <summary>
/// 탐색기 통합(파일 연결·우클릭 메뉴)의 레지스트리 등록/해제.
/// 전부 HKCU(현재 사용자) 범위라 관리자 권한이 필요 없고, 해제하면 흔적 없이 사라진다.
/// 설정 페이지에서 사용자가 명시적으로 켤 때만 등록한다.
/// </summary>
public static class ExplorerIntegration
{
    private const string ExtractHereVerbName = "WinUtil.ExtractHere";
    private const string CompressVerbName = "WinUtil.Compress";

    private static string ExePath =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");

    private static string ProgId(IModule module) => "WinUtil." + module.Id;

    // ---------- 파일 연결 ("연결 프로그램" 목록 등록) ----------

    public static bool IsAssociationRegistered(IModule module)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId(module)}");
        return key is not null;
    }

    public static void RegisterAssociation(IModule module)
    {
        var progId = ProgId(module);

        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
        {
            progKey.SetValue(null, $"WinUtil {module.DisplayName}");
            using (var icon = progKey.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{ExePath}\",0");
            using (var command = progKey.CreateSubKey(@"shell\open\command"))
                command.SetValue(null, $"\"{ExePath}\" \"%1\"");
        }

        // 확장자마다 OpenWithProgids에 등록 → 탐색기 "연결 프로그램" 목록에 나타난다.
        // (기본 앱 강탈이 아니라 후보 등록 — 기본 앱 지정은 Windows 설정에서 사용자가 한다)
        foreach (var ext in module.SupportedExtensions)
        {
            using var extKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{ext}\OpenWithProgids");
            extKey.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyShell();
    }

    public static void UnregisterAssociation(IModule module)
    {
        var progId = ProgId(module);

        foreach (var ext in module.SupportedExtensions)
        {
            using var extKey = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            if (extKey?.GetValueNames().Contains(progId, StringComparer.OrdinalIgnoreCase) == true)
                extKey.DeleteValue(progId, throwOnMissingValue: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
        NotifyShell();
    }

    // ---------- 우클릭 메뉴: 압축 파일 → "WinUtil로 여기에 풀기" ----------

    public static bool IsExtractHereMenuRegistered(IReadOnlyList<string> archiveExtensions)
    {
        if (archiveExtensions.Count == 0) return false;
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\SystemFileAssociations\{archiveExtensions[0]}\shell\{ExtractHereVerbName}");
        return key is not null;
    }

    public static void RegisterExtractHereMenu(IReadOnlyList<string> archiveExtensions)
    {
        foreach (var ext in archiveExtensions)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\{ExtractHereVerbName}");
            verb.SetValue(null, "WinUtil로 여기에 풀기");
            verb.SetValue("Icon", $"\"{ExePath}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{ExePath}\" {LaunchRequest.ExtractHereToken} \"%1\"");
        }
        NotifyShell();
    }

    public static void UnregisterExtractHereMenu(IReadOnlyList<string> archiveExtensions)
    {
        foreach (var ext in archiveExtensions)
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\{ExtractHereVerbName}",
                throwOnMissingSubKey: false);
        }
        NotifyShell();
    }

    // ---------- 우클릭 메뉴: 모든 파일 → "WinUtil로 압축" ----------

    private const string CompressVerbKeyPath = @"Software\Classes\*\shell\" + CompressVerbName;

    public static bool IsCompressMenuRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(CompressVerbKeyPath);
        return key is not null;
    }

    public static void RegisterCompressMenu()
    {
        using (var verb = Registry.CurrentUser.CreateSubKey(CompressVerbKeyPath))
        {
            verb.SetValue(null, "WinUtil로 압축");
            verb.SetValue("Icon", $"\"{ExePath}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{ExePath}\" {LaunchRequest.CompressToken} \"%1\"");
        }
        NotifyShell();
    }

    public static void UnregisterCompressMenu()
    {
        Registry.CurrentUser.DeleteSubKeyTree(CompressVerbKeyPath, throwOnMissingSubKey: false);
        NotifyShell();
    }

    // ---------- 공통 ----------

    /// <summary>탐색기에 연결 변경을 알린다(아이콘/메뉴 캐시 갱신).</summary>
    private static void NotifyShell() =>
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */,
            IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
