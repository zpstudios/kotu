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

    /// <summary>확장자별 ProgID (A23, v0.60.0). 예: "ZP.archive.zip" — 확장자마다 다른 아이콘을 달기 위함.</summary>
    private static string ExtProgId(IModule module, string ext) => ProgId(module) + ext;

    /// <summary>확장자 전용 아이콘 경로(Assets\fileicons\zp-{ext}.ico). 없으면 null → exe 아이콘 폴백.</summary>
    private static string? FileIconPath(string ext)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "fileicons",
            $"zp-{ext.TrimStart('.')}.ico");
        return File.Exists(path) ? path : null;
    }

    // ---------- 앱 등록 + 기본 앱 지정 보조 (A25, v0.61.0) ----------
    // 기본 앱(UserChoice) 자체는 Windows 10+가 비공개 해시로 보호해 프로그램이 직접 쓸 수 없다.
    // 그래서 ① 설정 '기본 앱' 목록에 ZP가 앱으로 나타나게 Capabilities를 등록하고,
    // ② 그 페이지로 바로 가는 딥링크와 ③ 확장자별 '연결 프로그램' 대화상자,
    // ④ 현재 기본 앱 여부 읽기(조회는 허용됨)를 제공한다.

    private const string CapabilitiesKeyPath = @"Software\ZP\Capabilities";

    /// <summary>설정 '기본 앱' 목록에 ZP가 앱으로 나타나도록 모듈 확장자를 Capabilities에 병합 등록.</summary>
    private static void RegisterCapabilities(IModule module)
    {
        using (var cap = Registry.CurrentUser.CreateSubKey(CapabilitiesKeyPath))
        {
            cap.SetValue("ApplicationName", "ZP");
            cap.SetValue("ApplicationDescription", "ZP - archives, images, video, music, documents");
            using var fa = cap.CreateSubKey("FileAssociations");
            foreach (var ext in module.SupportedExtensions)
                fa.SetValue(ext, ExtProgId(module, ext));
        }
        using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue("ZP", CapabilitiesKeyPath);
    }

    /// <summary>모듈 확장자를 Capabilities에서 제거. 남는 연결이 없으면 앱 등록 자체를 걷어낸다.</summary>
    private static void UnregisterCapabilities(IModule module)
    {
        using (var fa = Registry.CurrentUser.OpenSubKey(
                   CapabilitiesKeyPath + @"\FileAssociations", writable: true))
        {
            if (fa is not null)
            {
                foreach (var ext in module.SupportedExtensions)
                    fa.DeleteValue(ext, throwOnMissingValue: false);
            }
        }

        using var remaining = Registry.CurrentUser.OpenSubKey(CapabilitiesKeyPath + @"\FileAssociations");
        if (remaining is null || remaining.ValueCount == 0)
        {
            // 이 키는 여기서만 만든다(앱 설정은 settings.ini 파일) — 통째로 정리해도 안전.
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\ZP", throwOnMissingSubKey: false);
            using var registered = Registry.CurrentUser.OpenSubKey(
                @"Software\RegisteredApplications", writable: true);
            registered?.DeleteValue("ZP", throwOnMissingValue: false);
        }
    }

    /// <summary>ext의 현재 기본 앱이 ZP인지(UserChoice 읽기 — 쓰기는 OS 보호라 조회만).</summary>
    public static bool IsDefaultForExtension(string ext)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice");
            return key?.GetValue("ProgId") is string progId &&
                   progId.StartsWith("ZP.", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>모듈 확장자 중 ZP가 기본 앱인 개수 (설정 화면 "n/m" 표시용).</summary>
    public static int CountDefaults(IModule module) =>
        module.SupportedExtensions.Count(IsDefaultForExtension);

    /// <summary>
    /// Windows 설정의 ZP 기본 앱 페이지를 연다(Win11 22H2+ 딥링크).
    /// 파라미터를 모르는 구버전은 기본 앱 목록 페이지가 열린다 — 둘 다 사용자가
    /// 파일 형식별로 ZP를 지정할 수 있는 화면이다.
    /// </summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps?registeredAppUser=ZP",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 설정 앱을 못 열어도 토글 동작 자체는 성공이어야 한다.
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string FileName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? FileTypeDescription;
        public uint Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(nint parent, ref OpenAsInfo info);

    private const uint OaifRegisterExt = 0x00000002;       // 선택을 확장자 기본으로 등록
    private const uint OaifForceRegistration = 0x00000020; // "항상 이 앱 사용" 강제 표시

    /// <summary>확장자별 OS '연결 프로그램' 대화상자 — 여기서 ZP를 고르면 기본 앱이 된다.</summary>
    public static void ShowSetDefaultDialog(nint ownerHwnd, string ext)
    {
        var info = new OpenAsInfo
        {
            FileName = ext, // ".mp4" 형태 — OAIF_REGISTER_EXT와 함께면 확장자 등록 모드로 동작
            FileTypeDescription = null,
            Flags = OaifRegisterExt | OaifForceRegistration,
        };
        _ = SHOpenWithDialog(ownerHwnd, ref info);
    }

    // ---------- 파일 연결 ("연결 프로그램" 목록 등록) ----------

    public static bool IsAssociationRegistered(IModule module)
    {
        foreach (var ext in module.SupportedExtensions)
        {
            using var extProg = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ExtProgId(module, ext)}");
            if (extProg is not null) return true;
        }
        // 구 형태(모듈 단일 ProgID·WinUtil.*) 등록만 있어도 '켜짐'으로 보여 재등록(=이관)을 유도
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId(module)}");
        if (key is not null) return true;
        using var legacy = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{LegacyProgId(module)}");
        return legacy is not null;
    }

    /// <summary>
    /// 확장자마다 전용 ProgID를 만들어 등록한다(A23) — DefaultIcon이 확장자별 아이콘
    /// (모듈 색 + 확장자 글씨 + zp 표식)을 가리킨다. OpenWithProgids 등록이라 기본 앱
    /// 강탈이 아니라 후보 등록 — 기본 앱 지정은 Windows 설정에서 사용자가 한다.
    /// </summary>
    public static void RegisterAssociation(IModule module)
    {
        foreach (var ext in module.SupportedExtensions)
        {
            var progId = ExtProgId(module, ext);
            using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
            {
                progKey.SetValue(null, $"{module.BrandName} {ext.TrimStart('.').ToUpperInvariant()} file");
                using (var icon = progKey.CreateSubKey("DefaultIcon"))
                    icon.SetValue(null, FileIconPath(ext) is { } ico ? $"\"{ico}\"" : $"\"{ExePath}\",0");
                using (var command = progKey.CreateSubKey(@"shell\open\command"))
                    command.SetValue(null, $"\"{ExePath}\" \"%1\"");
            }

            using var extKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{ext}\OpenWithProgids");
            extKey.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // 구 형태 청소: 모듈 단일 ProgID(v0.60.0 이전)·WinUtil.*(v0.33.0 이전)
        RemoveAssociationKeys(module, ProgId(module), removeExtProgIds: false);
        RemoveAssociationKeys(module, LegacyProgId(module), removeExtProgIds: false);
        RegisterCapabilities(module); // 설정 '기본 앱' 목록 노출 (A25, v0.61.0)
        NotifyShell();
    }

    public static void UnregisterAssociation(IModule module)
    {
        RemoveAssociationKeys(module, ProgId(module), removeExtProgIds: true);
        RemoveAssociationKeys(module, LegacyProgId(module), removeExtProgIds: false);
        UnregisterCapabilities(module); // (A25, v0.61.0)
        NotifyShell();
    }

    /// <summary>progId(모듈 단일)와 — 요청 시 — 확장자별 ProgID들의 등록 흔적을 지운다.</summary>
    private static void RemoveAssociationKeys(IModule module, string progId, bool removeExtProgIds)
    {
        foreach (var ext in module.SupportedExtensions)
        {
            using var extKey = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            if (extKey?.GetValueNames().Contains(progId, StringComparer.OrdinalIgnoreCase) == true)
                extKey.DeleteValue(progId, throwOnMissingValue: false);

            if (removeExtProgIds)
            {
                var extProgId = ExtProgId(module, ext);
                if (extKey?.GetValueNames().Contains(extProgId, StringComparer.OrdinalIgnoreCase) == true)
                    extKey.DeleteValue(extProgId, throwOnMissingValue: false);
                Registry.CurrentUser.DeleteSubKeyTree(
                    $@"Software\Classes\{extProgId}", throwOnMissingSubKey: false);
            }
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
