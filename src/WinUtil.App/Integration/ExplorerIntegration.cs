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
    private const string ExtractHereVerbName = "ZP.ExtractHere";
    private const string CompressVerbName = "ZP.Compress";

    // 구 이름(WinUtil.*) — v0.33.0 리브랜딩 이전에 등록된 흔적의 탐지·청소용.
    private const string LegacyExtractHereVerbName = "WinUtil.ExtractHere";
    private const string LegacyCompressVerbName = "WinUtil.Compress";

    private static string ExePath =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the executable path.");

    private static string ProgId(IModule module) => "ZP." + module.Id;

    private static string LegacyProgId(IModule module) => "WinUtil." + module.Id;

    // ---------- 파일 연결 ("연결 프로그램" 목록 등록) ----------

    public static bool IsAssociationRegistered(IModule module)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId(module)}");
        if (key is not null) return true;
        using var legacy = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{LegacyProgId(module)}");
        return legacy is not null; // 구 이름 등록만 있어도 '켜짐'으로 보여 재등록(=이관)을 유도
    }

    public static void RegisterAssociation(IModule module)
    {
        var progId = ProgId(module);

        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
        {
            progKey.SetValue(null, $"{module.BrandName} file");
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

        RemoveAssociationKeys(module, LegacyProgId(module)); // 구 WinUtil.* 등록 흔적 청소 (v0.33.0)
        NotifyShell();
    }

    public static void UnregisterAssociation(IModule module)
    {
        RemoveAssociationKeys(module, ProgId(module));
        RemoveAssociationKeys(module, LegacyProgId(module));
        NotifyShell();
    }

    private static void RemoveAssociationKeys(IModule module, string progId)
    {
        foreach (var ext in module.SupportedExtensions)
        {
            using var extKey = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            if (extKey?.GetValueNames().Contains(progId, StringComparer.OrdinalIgnoreCase) == true)
                extKey.DeleteValue(progId, throwOnMissingValue: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
    }

    // ---------- 우클릭 메뉴: 압축 파일 → "Extract here with ZP-zip" ----------

    public static bool IsExtractHereMenuRegistered(IReadOnlyList<string> archiveExtensions)
    {
        if (archiveExtensions.Count == 0) return false;
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\SystemFileAssociations\{archiveExtensions[0]}\shell\{ExtractHereVerbName}");
        if (key is not null) return true;
        using var legacy = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\SystemFileAssociations\{archiveExtensions[0]}\shell\{LegacyExtractHereVerbName}");
        return legacy is not null;
    }

    public static void RegisterExtractHereMenu(IReadOnlyList<string> archiveExtensions)
    {
        foreach (var ext in archiveExtensions)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\{ExtractHereVerbName}");
            verb.SetValue(null, "Extract here with ZP-zip");
            verb.SetValue("Icon", $"\"{ExePath}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{ExePath}\" {LaunchRequest.ExtractHereToken} \"%1\"");

            // 구 WinUtil.* 등록 흔적 청소 (v0.33.0)
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\{LegacyExtractHereVerbName}",
                throwOnMissingSubKey: false);
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
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\{LegacyExtractHereVerbName}",
                throwOnMissingSubKey: false);
        }
        NotifyShell();
    }

    // ---------- 우클릭 메뉴: 모든 파일 → "Compress with ZP-zip" ----------

    private const string CompressVerbKeyPath = @"Software\Classes\*\shell\" + CompressVerbName;
    private const string LegacyCompressVerbKeyPath = @"Software\Classes\*\shell\" + LegacyCompressVerbName;

    public static bool IsCompressMenuRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(CompressVerbKeyPath);
        if (key is not null) return true;
        using var legacy = Registry.CurrentUser.OpenSubKey(LegacyCompressVerbKeyPath);
        return legacy is not null;
    }

    public static void RegisterCompressMenu()
    {
        using (var verb = Registry.CurrentUser.CreateSubKey(CompressVerbKeyPath))
        {
            verb.SetValue(null, "Compress with ZP-zip");
            verb.SetValue("Icon", $"\"{ExePath}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{ExePath}\" {LaunchRequest.CompressToken} \"%1\"");
        }
        // 구 WinUtil.* 등록 흔적 청소 (v0.33.0)
        Registry.CurrentUser.DeleteSubKeyTree(LegacyCompressVerbKeyPath, throwOnMissingSubKey: false);
        NotifyShell();
    }

    public static void UnregisterCompressMenu()
    {
        Registry.CurrentUser.DeleteSubKeyTree(CompressVerbKeyPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(LegacyCompressVerbKeyPath, throwOnMissingSubKey: false);
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
