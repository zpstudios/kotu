using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using KOTU.Core.Cli;
using KOTU.Core.Contracts;
using KOTU.Core.Integration;

namespace KOTU.App.Integration;

/// <summary>
/// 탐색기 통합(파일 연결·우클릭 메뉴)의 레지스트리 등록/해제.
/// 전부 HKCU(현재 사용자) 범위라 관리자 권한이 필요 없고, 해제하면 흔적 없이 사라진다.
/// 설정 페이지에서 사용자가 명시적으로 켤 때만 등록한다.
/// </summary>
public static class ExplorerIntegration
{
    /// <summary>
    /// 현재 브랜드 접두사. 리브랜딩(A46) 때 여기만 바꾸고 <see cref="LegacyBrands"/>에 구 이름을 추가한다.
    /// </summary>
    private const string Brand = Branding.AppName; // "KOTU"

    /// <summary>
    /// 지난 브랜드 이름들 — 등록 흔적의 탐지·청소용(신→구 순).
    /// "ZP" = v0.33.0~v0.85.0, "WinUtil" = v0.33.0 이전.
    /// 사용자 결정(2026-08-10): 설정·연결의 **자동 이관은 하지 않는다**. 다만 남은 구 등록은
    /// 탐색기에 유령 메뉴·아이콘으로 보이므로 등록/해제 시 발견되는 대로 지운다.
    /// </summary>
    private static readonly string[] LegacyBrands = ["ZP", "WinUtil"];

    /// <summary>
    /// A350: 구 브랜드 이름의 읽기 전용 노출. <see cref="TaskbarIdentity"/>가 창 AUMID 표시 키
    /// (HKCU AppUserModelId) 청소에 같은 목록을 쓴다 — 리브랜딩 때 고칠 곳을 위 배열 하나로 유지하기 위함.
    /// </summary>
    internal static IReadOnlyList<string> LegacyBrandNames => LegacyBrands;

    private const string ExtractHereVerbName = Brand + ".ExtractHere";
    private const string CompressVerbName = Brand + ".Compress";

    private static IEnumerable<string> LegacyExtractHereVerbNames =>
        LegacyBrands.Select(b => b + ".ExtractHere");

    private static IEnumerable<string> LegacyCompressVerbNames =>
        LegacyBrands.Select(b => b + ".Compress");

    private static string ExePath =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the executable path.");

    private static string ProgId(IModule module) => Brand + "." + module.Id;

    private static IEnumerable<string> LegacyProgIds(IModule module) =>
        LegacyBrands.Select(b => b + "." + module.Id);

    /// <summary>확장자별 ProgID (A23, v0.60.0). 예: "KOTU.archive.zip" — 확장자마다 다른 아이콘을 달기 위함.</summary>
    private static string ExtProgId(IModule module, string ext) => ProgId(module) + ext;

    /// <summary>확장자 전용 아이콘 경로(Assets\fileicons\kotu-{ext}.ico). 없으면 null → exe 아이콘 폴백.</summary>
    private static string? FileIconPath(string ext)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "fileicons",
            $"{Brand.ToLowerInvariant()}-{ext.TrimStart('.')}.ico");
        return File.Exists(path) ? path : null;
    }

    // ---------- 앱 등록 + 기본 앱 지정 보조 (A25, v0.61.0) ----------
    // 기본 앱(UserChoice) 자체는 Windows 10+가 비공개 해시로 보호해 프로그램이 직접 쓸 수 없다.
    // 그래서 ① 설정 '기본 앱' 목록에 KOTU가 앱으로 나타나게 Capabilities를 등록하고,
    // ② 그 페이지로 바로 가는 딥링크와 ③ 확장자별 '연결 프로그램' 대화상자,
    // ④ 현재 기본 앱 여부 읽기(조회는 허용됨)를 제공한다.

    private const string CapabilitiesKeyPath = @"Software\" + Brand + @"\Capabilities";

    /// <summary>설정 '기본 앱' 목록에 KOTU가 앱으로 나타나도록 모듈 확장자를 Capabilities에 병합 등록.</summary>
    private static void RegisterCapabilities(IModule module)
    {
        using (var cap = Registry.CurrentUser.CreateSubKey(CapabilitiesKeyPath))
        {
            cap.SetValue("ApplicationName", Brand);
            cap.SetValue("ApplicationDescription", Brand + " - archives, images, video, music, documents");
            using var fa = cap.CreateSubKey("FileAssociations");
            foreach (var ext in module.SupportedExtensions)
                fa.SetValue(ext, ExtProgId(module, ext));
        }
        using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue(Brand, CapabilitiesKeyPath);
        RemoveLegacyCapabilities();
    }

    /// <summary>
    /// A292: 확장자 하나를 Capabilities에 병합 등록 — <see cref="RegisterCapabilities"/>(모듈 전체)의
    /// 확장자 단위 판. 앱 등록(ApplicationName·RegisteredApplications)은 모듈 판과 같은 값을 다시 써
    /// 멱등이다.
    /// </summary>
    private static void RegisterCapabilityExtension(IModule module, string ext)
    {
        using (var cap = Registry.CurrentUser.CreateSubKey(CapabilitiesKeyPath))
        {
            cap.SetValue("ApplicationName", Brand);
            cap.SetValue("ApplicationDescription", Brand + " - archives, images, video, music, documents");
            using var fa = cap.CreateSubKey("FileAssociations");
            fa.SetValue(ext, ExtProgId(module, ext));
        }
        using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue(Brand, CapabilitiesKeyPath);
        RemoveLegacyCapabilities();
    }

    /// <summary>
    /// A292: 확장자 하나를 Capabilities에서 제거 — <see cref="UnregisterCapabilities"/>(모듈 전체)의
    /// 확장자 단위 판. 남는 연결이 없으면 모듈 판과 같은 규칙으로 앱 등록 자체를 걷어낸다.
    /// </summary>
    private static void UnregisterCapabilityExtension(string ext)
    {
        using (var fa = Registry.CurrentUser.OpenSubKey(
                   CapabilitiesKeyPath + @"\FileAssociations", writable: true))
        {
            fa?.DeleteValue(ext, throwOnMissingValue: false);
        }
        CollapseCapabilitiesIfEmpty();
        RemoveLegacyCapabilities();
    }

    /// <summary>연결이 하나도 안 남았으면 앱 등록(Capabilities·RegisteredApplications)을 걷어낸다.</summary>
    private static void CollapseCapabilitiesIfEmpty()
    {
        using var remaining = Registry.CurrentUser.OpenSubKey(CapabilitiesKeyPath + @"\FileAssociations");
        if (remaining is not null && remaining.ValueCount > 0) return;
        // 이 키는 여기서만 만든다(앱 설정은 설정 파일) — 통째로 정리해도 안전.
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\{Brand}", throwOnMissingSubKey: false);
        using var registered = Registry.CurrentUser.OpenSubKey(
            @"Software\RegisteredApplications", writable: true);
        registered?.DeleteValue(Brand, throwOnMissingValue: false);
    }

    /// <summary>구 브랜드의 Capabilities·RegisteredApplications 등록을 걷어낸다(A46 리브랜딩 청소).</summary>
    private static void RemoveLegacyCapabilities()
    {
        using var registered = Registry.CurrentUser.OpenSubKey(
            @"Software\RegisteredApplications", writable: true);
        foreach (var legacy in LegacyBrands)
        {
            // 이 키는 우리가 만든 것만 들어 있다(앱 설정은 설정 파일) — 통째로 정리해도 안전.
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{legacy}", throwOnMissingSubKey: false);
            registered?.DeleteValue(legacy, throwOnMissingValue: false);
        }
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

        CollapseCapabilitiesIfEmpty(); // A292에서 확장자 단위 판과 공유하도록 추출 — 규칙은 종전 그대로
        RemoveLegacyCapabilities();
    }

    /// <summary>
    /// ext의 현재 기본 앱이 KOTU인지(UserChoice 읽기 — 쓰기는 A38이 담당).
    /// 구 브랜드(ZP.*) ProgID는 리브랜딩 후 클래스 키가 없어 무효이므로 <b>세지 않는다</b> —
    /// 설정 화면의 "n/m"이 0으로 떨어지고, 토글을 켜면 A38이 새 ProgID로 다시 지정한다.
    /// </summary>
    public static bool IsDefaultForExtension(string ext)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice");
            return key?.GetValue("ProgId") is string progId &&
                   progId.StartsWith(Brand + ".", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>모듈 확장자 중 KOTU가 기본 앱인 개수 (설정 화면 "n/m" 표시용).</summary>
    public static int CountDefaults(IModule module) =>
        module.SupportedExtensions.Count(IsDefaultForExtension);

    /// <summary>
    /// Windows 설정의 KOTU 기본 앱 페이지를 연다(Win11 22H2+ 딥링크).
    /// 파라미터를 모르는 구버전은 기본 앱 목록 페이지가 열린다 — 둘 다 사용자가
    /// 파일 형식별로 KOTU를 지정할 수 있는 화면이다.
    /// </summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps?registeredAppUser=KOTU",
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

    /// <summary>확장자별 OS '연결 프로그램' 대화상자 — 여기서 KOTU를 고르면 기본 앱이 된다.</summary>
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

    // ---------- 기본 앱 강제 지정 (A38, v0.85.0) ----------
    // A25는 여기까지(후보 등록 + 사용자가 설정에서 확정)였다. A38은 한 걸음 더 나아가
    // UserChoice(ProgId+Hash)를 직접 써서 사용자 클릭 없이 기본 앱 지정을 완결한다.
    // 해시는 UserChoiceHash가 자체 계산(외부 exe 없음). 비공식이라 쓴 뒤 반드시 재검증하고,
    // 실패한 확장자는 호출 측이 A25 폴백(설정 딥링크/'연결 프로그램' 대화상자)으로 넘긴다.

    private const string FileExtsPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    /// <summary>
    /// Win10/Win11의 UserChoice Protection Driver(UCPD.sys)가 커널 레지스트리 콜백(CmRegisterCallbackEx)으로
    /// 보호하는 확장자·프로토콜 (A166 조사). 이 목록의 UserChoice/UserChoiceLatest 키는 Microsoft 서명
    /// 신뢰 프로세스만 쓰기/삭제/이름변경/ACL 변경이 허용되고, 제3자 미서명 앱(KOTU)이 강행하면 커널이
    /// ACCESS_DENIED로 되돌린다. 게다가 진단 데이터가 켜져 있으면 시도 자체(수정 종류·바이너리 이름)가
    /// Microsoft 텔레메트리로 보고된다. 그래서 이 확장자는 A38 강행을 아예 건너뛰고 곧바로 폴백으로 안내한다.
    /// 목록 근거: kolbicz UCPD 분석(2024·2025) + binary.ninja 리버싱(2025). 목록은 커널 드라이버가
    /// 정하는 고정 집합이라 런타임 실패 기억 없이 하드코딩으로 충분하다.
    /// </summary>
    private static readonly HashSet<string> UcpdProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".htm", ".html", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
    };

    /// <summary>
    /// ext가 UCPD가 커널에서 보호하는 확장자인지 — true면 UserChoice 강행이 반드시 실패하므로
    /// 시도하지 않고 사용자 확인 폴백(설정 페이지·'연결 프로그램' 대화상자)으로 안내한다.
    /// </summary>
    public static bool IsProtectedExtension(string ext) => UcpdProtectedExtensions.Contains(ext);

    /// <summary>
    /// 모듈의 모든 확장자를 KOTU 기본 앱으로 강제 지정 시도.
    /// 반드시 <see cref="RegisterAssociation"/> 이후 호출(ProgID 클래스 키가 있어야 UserChoice가 유효).
    /// </summary>
    /// <param name="module">대상 모듈.</param>
    /// <param name="progress">확장자 하나를 끝낼 때마다 n/m을 보고할 곳 (A77, v0.106.0). 없으면 보고 안 함.</param>
    /// <returns>강제 지정에 실패한 확장자 목록 — A25 폴백 대상. 비어 있으면 전부 성공.</returns>
    public static IReadOnlyList<string> SetAsDefault(IModule module,
        IProgress<AssociationProgress>? progress = null)
    {
        string sid;
        try { sid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty; }
        catch { sid = string.Empty; }
        if (string.IsNullOrEmpty(sid))
            return module.SupportedExtensions.ToList(); // SID를 못 구하면 전부 폴백

        var failed = new List<string>();
        var total = module.SupportedExtensions.Count;
        var done = 0;
        foreach (var ext in module.SupportedExtensions)
        {
            // UCPD 보호 확장자(.pdf 등)는 강행해도 커널이 되돌리므로 시도하지 않는다 — 무익한
            // 재시도·텔레메트리를 피하고 폴백으로만 안내한다(A166). 호출 측은 실패 목록에서
            // IsProtectedExtension으로 이들을 가려 "확인 필요" 안내와 일반 실패를 구분한다.
            if (IsProtectedExtension(ext) || !TrySetDefaultForExtension(ext, ExtProgId(module, ext), sid))
                failed.Add(ext);
            progress?.Report(new AssociationProgress(++done, total, AssociationProgress.SettingDefault));
        }
        if (failed.Count < module.SupportedExtensions.Count)
            NotifyShell(); // 하나라도 바뀌었으면 아이콘/기본앱 캐시 갱신
        return failed;
    }

    /// <summary>
    /// A292: 확장자 하나를 KOTU 기본 앱으로 강제 지정 시도 — <see cref="SetAsDefault"/>(모듈 전체)의
    /// 확장자 단위 판. 반드시 <see cref="RegisterExtensionAssociation"/> 이후 호출.
    /// UCPD 보호 확장자(A166)는 커널이 되돌리므로 시도하지 않고 false — 호출 측이
    /// <see cref="IsProtectedExtension"/>으로 "확인 필요" 안내와 일반 실패를 구분한다.
    /// </summary>
    /// <returns>지정에 성공했으면 true. false면 A25 폴백('연결 프로그램' 대화상자 등) 대상.</returns>
    public static bool SetAsDefaultForExtension(IModule module, string ext)
    {
        if (IsProtectedExtension(ext)) return false;

        string sid;
        try { sid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty; }
        catch { sid = string.Empty; }
        if (string.IsNullOrEmpty(sid)) return false;

        if (!TrySetDefaultForExtension(ext, ExtProgId(module, ext), sid)) return false;
        NotifyShell(); // 바뀐 것이 있을 때만 — 모듈 판의 "하나라도 바뀌었으면"과 같은 규칙
        return true;
    }

    /// <summary>
    /// 확장자 하나를 UserChoice 직접 쓰기로 기본 앱 지정하고 실제 LastWrite 기준으로 검증.
    /// 최신 빌드는 UserChoice 값 쓰기를 ACL로 막으므로, 부모에서 하위 키를 지우고 새로 만들면
    /// 새 키는 쓰기 가능한 기본 ACL을 얻는다(SetUserFTA/Mozilla와 동일한 우회).
    /// 분 경계로 해시가 어긋날 수 있어 최대 3회 재시도하고, 최종 실패 시 우리가 남긴 흔적을 지운다.
    /// </summary>
    private static bool TrySetDefaultForExtension(string ext, string progId, string sid)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using (var extKey = Registry.CurrentUser.CreateSubKey($@"{FileExtsPath}\{ext}"))
                {
                    extKey.DeleteSubKey("UserChoice", throwOnMissingSubKey: false); // ACL 우회
                    var ft = UserChoiceHash.FloorToMinute(DateTime.UtcNow.ToFileTimeUtc());
                    var hash = UserChoiceHash.Generate(ext, sid, progId, ft);
                    using var uc = extKey.CreateSubKey("UserChoice");
                    uc.SetValue("ProgId", progId, RegistryValueKind.String);
                    uc.SetValue("Hash", hash, RegistryValueKind.String);
                }

                if (VerifyUserChoice(ext, progId, sid))
                    return true;
                // 검증 실패 = 분 경계로 어긋났거나 OS가 되돌림 → 재시도
            }

            // 3회 모두 실패: 우리가 남긴 UserChoice를 지워 상태를 되돌린다
            // (유효하지 않은 Hash에 ProgId만 남아 CountDefaults가 오판하는 것 방지).
            using var parent = Registry.CurrentUser.OpenSubKey($@"{FileExtsPath}\{ext}", writable: true);
            parent?.DeleteSubKey("UserChoice", throwOnMissingSubKey: false);
            return false;
        }
        catch
        {
            return false; // ACL/UCPD 차단 등 — 폴백
        }
    }

    /// <summary>쓴 UserChoice가 실제 키 LastWrite 기준 해시와 일치하는지(=Windows가 수용하는지) 확인.</summary>
    private static bool VerifyUserChoice(string ext, string progId, string sid)
    {
        using var uc = Registry.CurrentUser.OpenSubKey($@"{FileExtsPath}\{ext}\UserChoice");
        if (uc is null) return false;
        if (uc.GetValue("ProgId") as string != progId) return false;
        if (uc.GetValue("Hash") is not string storedHash) return false;
        if (!TryGetKeyLastWriteFileTime(uc, out var lastWrite)) return false;

        var expected = UserChoiceHash.Generate(ext, sid, progId, UserChoiceHash.FloorToMinute(lastWrite));
        return string.Equals(storedHash, expected, StringComparison.Ordinal);
    }

    [DllImport("advapi32.dll")]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle hKey, nint lpClass, nint lpcchClass, nint lpReserved,
        nint lpcSubKeys, nint lpcbMaxSubKeyLen, nint lpcbMaxClassLen, nint lpcValues,
        nint lpcbMaxValueNameLen, nint lpcbMaxValueLen, nint lpcbSecurityDescriptor,
        out long lpftLastWriteTime);

    /// <summary>레지스트리 키의 LastWrite 시각(FILETIME, UTC)을 읽는다(.NET이 직접 노출하지 않아 P/Invoke).</summary>
    private static bool TryGetKeyLastWriteFileTime(RegistryKey key, out long fileTime) =>
        RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out fileTime) == 0;

    // ---------- 파일 연결 ("연결 프로그램" 목록 등록) ----------

    public static bool IsAssociationRegistered(IModule module)
    {
        foreach (var ext in module.SupportedExtensions)
        {
            using var extProg = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ExtProgId(module, ext)}");
            if (extProg is not null) return true;
        }
        // 구 형태(모듈 단일 ProgID, v0.60.0 이전)도 현재 브랜드면 '켜짐'으로 본다.
        // 구 브랜드(ZP.*/KOTU.*)는 세지 않는다 — 이관하지 않기로 했으므로(A46, 2026-08-10)
        // 토글은 꺼진 상태로 보이고, 남은 흔적은 CleanUpLegacyBrandRegistrations가 지운다.
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId(module)}");
        return key is not null;
    }

    /// <summary>
    /// A292: 확장자 하나의 등록 여부 — <see cref="IsAssociationRegistered"/>(모듈 전체)의 확장자 단위 판.
    /// 정본은 레지스트리다(설정 파일 키 없음) — 모듈 토글이 그랬던 것과 같은 원칙.
    /// 구 형태(모듈 단일 ProgID) 폴백은 클래스 키 유무가 아니라 <b>이 확장자의 OpenWithProgids에
    /// 그 ProgID가 걸려 있는지</b>로 본다 — 클래스 키는 모듈 공유라 확장자 하나를 끈 뒤에도 남아
    /// '켜짐'으로 오판하게 되기 때문이다. 구 브랜드는 세지 않는다(A46 — 모듈 판과 같은 규칙).
    /// </summary>
    public static bool IsExtensionAssociationRegistered(IModule module, string ext)
    {
        using var extProg = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\{ExtProgId(module, ext)}");
        if (extProg is not null) return true;

        using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids");
        return extKey?.GetValueNames().Contains(ProgId(module), StringComparer.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// 구 브랜드(ZP·WinUtil)로 등록됐던 파일 연결·우클릭 메뉴 흔적을 전부 지운다 (A46, v0.86.0).
    /// 리브랜딩으로 ProgID가 바뀌면 구 키는 동작하지 않는 유령이 되므로 앱 시작 시 1회 청소한다.
    /// 실패해도 앱 동작에는 영향이 없다.
    /// </summary>
    public static void CleanUpLegacyBrandRegistrations(IEnumerable<IModule> modules)
    {
        try
        {
            // A59: 연결 대상이 아닌 모듈(All Readable)은 등록한 적이 없으니 청소할 것도 없다.
            foreach (var module in modules.Where(m => m.RegistersFileAssociations))
            {
                foreach (var legacyProgId in LegacyProgIds(module))
                    RemoveAssociationKeys(module, legacyProgId, removeExtProgIds: true);

                foreach (var ext in module.SupportedExtensions)
                {
                    foreach (var verb in LegacyExtractHereVerbNames)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(
                            $@"Software\Classes\SystemFileAssociations\{ext}\shell\{verb}",
                            throwOnMissingSubKey: false);
                    }
                }
            }

            foreach (var legacyPath in LegacyCompressVerbKeyPaths)
                Registry.CurrentUser.DeleteSubKeyTree(legacyPath, throwOnMissingSubKey: false);

            RemoveLegacyCapabilities();
            NotifyShell();
        }
        catch
        {
            // 청소 실패는 치명적이지 않다 — 다음 실행에서 다시 시도된다.
        }
    }

    /// <summary>
    /// 확장자마다 전용 ProgID를 만들어 등록한다(A23) — DefaultIcon이 확장자별 아이콘
    /// (모듈 색 + 확장자 글씨 + kotu 표식)을 가리킨다. OpenWithProgids 등록이라 기본 앱
    /// 강탈이 아니라 후보 등록 — 기본 앱 지정은 Windows 설정에서 사용자가 한다.
    /// A292: 설정 화면·exe 이동 재등록은 이제 확장자 단위 판
    /// (<see cref="RegisterExtensionAssociation"/>)을 쓴다 — 이 모듈 일괄 판은 호출처가 없지만
    /// 확장자 판의 원형이자 일괄 등록 진입로로 남긴다(해제 판도 같다).
    /// </summary>
    /// <param name="module">대상 모듈.</param>
    /// <param name="progress">확장자 하나를 끝낼 때마다 n/m을 보고할 곳 (A77, v0.106.0). 없으면 보고 안 함.</param>
    public static void RegisterAssociation(IModule module, IProgress<AssociationProgress>? progress = null)
    {
        var total = module.SupportedExtensions.Count;
        var done = 0;
        foreach (var ext in module.SupportedExtensions)
        {
            WriteExtensionClassKeys(module, ext);
            progress?.Report(new AssociationProgress(++done, total, AssociationProgress.Registering));
        }

        // 구 형태 청소: 모듈 단일 ProgID(v0.60.0 이전) + 구 브랜드 전체(ZP·WinUtil)
        RemoveAssociationKeys(module, ProgId(module), removeExtProgIds: false);
        foreach (var legacyProgId in LegacyProgIds(module))
            RemoveAssociationKeys(module, legacyProgId, removeExtProgIds: true);
        RegisterCapabilities(module); // 설정 '기본 앱' 목록 노출 (A25, v0.61.0)
        NotifyShell();
    }

    /// <summary>확장자 하나의 클래스 키 기록(전용 ProgID + DefaultIcon + open command + OpenWithProgids 후보 등록).
    /// <see cref="RegisterAssociation"/>의 루프 본문을 A292에서 확장자 단위 판과 공유하도록 추출한 것 — 내용 무변경.</summary>
    private static void WriteExtensionClassKeys(IModule module, string ext)
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

        using (var extKey = Registry.CurrentUser.CreateSubKey(
                   $@"Software\Classes\{ext}\OpenWithProgids"))
        {
            extKey.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }
    }

    /// <summary>
    /// A292: 확장자 하나를 등록 — <see cref="RegisterAssociation"/>(모듈 전체)의 확장자 단위 판.
    /// 구 형태·구 브랜드 청소도 <b>이 확장자 범위만</b> 한다(모듈 공유 클래스 키는 남긴다 —
    /// 다른 확장자가 아직 기대고 있을 수 있고, 구 브랜드 잔재는 앱 시작 시
    /// <see cref="CleanUpLegacyBrandRegistrations"/>가 마저 지운다).
    /// </summary>
    public static void RegisterExtensionAssociation(IModule module, string ext)
    {
        WriteExtensionClassKeys(module, ext);

        // 이 확장자 범위의 구 형태(모듈 단일 ProgID)·구 브랜드 청소 — RemoveAssociationKeys의
        // 루프 본문과 같은 코드(RemoveExtensionAssociationKeys)를 확장자 하나에만 적용한다.
        RemoveExtensionAssociationKeys(ProgId(module), ext, removeExtProgId: false);
        foreach (var legacyProgId in LegacyProgIds(module))
            RemoveExtensionAssociationKeys(legacyProgId, ext, removeExtProgId: true);
        RegisterCapabilityExtension(module, ext); // 설정 '기본 앱' 목록 노출 (A25, v0.61.0)
        NotifyShell();
    }

    /// <summary>
    /// A292: 확장자 하나를 해제 — <see cref="UnregisterAssociation"/>(모듈 전체)의 확장자 단위 판.
    /// UserChoice(기본 앱 선택)는 모듈 판과 같이 건드리지 않는다.
    /// </summary>
    public static void UnregisterExtensionAssociation(IModule module, string ext)
    {
        RemoveExtensionAssociationKeys(ProgId(module), ext, removeExtProgId: true);
        foreach (var legacyProgId in LegacyProgIds(module))
            RemoveExtensionAssociationKeys(legacyProgId, ext, removeExtProgId: true);
        UnregisterCapabilityExtension(ext); // (A25, v0.61.0)
        NotifyShell();
    }

    /// <param name="module">대상 모듈.</param>
    /// <param name="progress">확장자 하나를 끝낼 때마다 n/m을 보고할 곳 (A77, v0.106.0).
    /// 진행률은 현재 브랜드 정리분만 센다 — 구 브랜드 청소는 보통 지울 게 없어 순식간에 끝난다.</param>
    public static void UnregisterAssociation(IModule module, IProgress<AssociationProgress>? progress = null)
    {
        RemoveAssociationKeys(module, ProgId(module), removeExtProgIds: true, progress);
        foreach (var legacyProgId in LegacyProgIds(module))
            RemoveAssociationKeys(module, legacyProgId, removeExtProgIds: true);
        UnregisterCapabilities(module); // (A25, v0.61.0)
        NotifyShell();
    }

    /// <summary>
    /// progId(모듈 단일)와 — 요청 시 — 그 확장자별 파생 ProgID(progId+ext)들의 등록 흔적을 지운다.
    /// 파생 ID를 현재 브랜드가 아니라 전달받은 progId에서 만들기 때문에 구 브랜드 청소에도 그대로 쓸 수 있다.
    /// </summary>
    private static void RemoveAssociationKeys(IModule module, string progId, bool removeExtProgIds,
        IProgress<AssociationProgress>? progress = null)
    {
        var total = module.SupportedExtensions.Count;
        var done = 0;
        foreach (var ext in module.SupportedExtensions)
        {
            RemoveExtensionAssociationKeys(progId, ext, removeExtProgIds);
            progress?.Report(new AssociationProgress(++done, total, AssociationProgress.Unregistering));
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
    }

    /// <summary>
    /// progId가 확장자 하나에 남긴 등록 흔적(OpenWithProgids 값 + — 요청 시 — 파생 ProgID 클래스 키)을
    /// 지운다. <see cref="RemoveAssociationKeys"/>의 루프 본문을 A292에서 확장자 단위 판과 공유하도록
    /// 추출한 것 — 내용 무변경. 모듈 단일 ProgID 클래스 키(모듈 공유)는 여기서 지우지 않는다.
    /// </summary>
    private static void RemoveExtensionAssociationKeys(string progId, string ext, bool removeExtProgId)
    {
        using var extKey = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\{ext}\OpenWithProgids", writable: true);
        if (extKey?.GetValueNames().Contains(progId, StringComparer.OrdinalIgnoreCase) == true)
            extKey.DeleteValue(progId, throwOnMissingValue: false);

        if (removeExtProgId)
        {
            var extProgId = progId + ext;
            if (extKey?.GetValueNames().Contains(extProgId, StringComparer.OrdinalIgnoreCase) == true)
                extKey.DeleteValue(extProgId, throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\{extProgId}", throwOnMissingSubKey: false);
        }
    }

    // ---------- 우클릭 메뉴: 압축 파일 → "Extract here with {압축 모듈 BrandName}" ----------
    // 표시 라벨은 하드코딩하지 않고 호출 측(SettingsView)이 모듈 BrandName을 넘긴다 —
    // A52처럼 모듈명이 바뀔 때 문구가 따라오게 하기 위함.

    public static bool IsExtractHereMenuRegistered(IReadOnlyList<string> archiveExtensions)
    {
        if (archiveExtensions.Count == 0) return false;
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\SystemFileAssociations\{archiveExtensions[0]}\shell\{ExtractHereVerbName}");
        // 구 브랜드 등록은 세지 않는다(A46 — 이관 없음). 남은 흔적은 등록/해제 시 청소된다.
        return key is not null;
    }

    public static void RegisterExtractHereMenu(IReadOnlyList<string> archiveExtensions, string brandLabel)
    {
        foreach (var ext in archiveExtensions)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\{ExtractHereVerbName}");
            verb.SetValue(null, $"Extract here with {brandLabel}");
            verb.SetValue("Icon", $"\"{ExePath}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{ExePath}\" {LaunchRequest.ExtractHereToken} \"%1\"");

            // 구 브랜드(ZP·WinUtil) 등록 흔적 청소
            foreach (var legacyVerb in LegacyExtractHereVerbNames)
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    $@"Software\Classes\SystemFileAssociations\{ext}\shell\{legacyVerb}",
                    throwOnMissingSubKey: false);
            }
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
            foreach (var legacyVerb in LegacyExtractHereVerbNames)
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    $@"Software\Classes\SystemFileAssociations\{ext}\shell\{legacyVerb}",
                    throwOnMissingSubKey: false);
            }
        }
        NotifyShell();
    }

    // ---------- 우클릭 메뉴: 모든 파일 → "Compress with {압축 모듈 BrandName}" ----------

    private const string CompressVerbKeyPath = @"Software\Classes\*\shell\" + CompressVerbName;

    private static IEnumerable<string> LegacyCompressVerbKeyPaths =>
        LegacyCompressVerbNames.Select(v => @"Software\Classes\*\shell\" + v);

    public static bool IsCompressMenuRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(CompressVerbKeyPath);
        return key is not null; // 구 브랜드 등록은 세지 않는다 (A46 — 이관 없음)
    }

    public static void RegisterCompressMenu(string brandLabel)
    {
        using (var verb = Registry.CurrentUser.CreateSubKey(CompressVerbKeyPath))
        {
            verb.SetValue(null, $"Compress with {brandLabel}");
            verb.SetValue("Icon", $"\"{ExePath}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{ExePath}\" {LaunchRequest.CompressToken} \"%1\"");
        }
        // 구 브랜드(ZP·WinUtil) 등록 흔적 청소
        foreach (var legacyPath in LegacyCompressVerbKeyPaths)
            Registry.CurrentUser.DeleteSubKeyTree(legacyPath, throwOnMissingSubKey: false);
        NotifyShell();
    }

    public static void UnregisterCompressMenu()
    {
        Registry.CurrentUser.DeleteSubKeyTree(CompressVerbKeyPath, throwOnMissingSubKey: false);
        foreach (var legacyPath in LegacyCompressVerbKeyPaths)
            Registry.CurrentUser.DeleteSubKeyTree(legacyPath, throwOnMissingSubKey: false);
        NotifyShell();
    }

    // ---------- exe 경로 자동 재등록 (A78, v0.89.0) ----------
    // 파일 연결·우클릭 메뉴 등록에는 그 시점의 exe 절대 경로가 구워진다
    // (ProgID의 shell\open\command, DefaultIcon 폴백, verb의 command·Icon).
    // exe 이름·위치가 바뀌면(A64의 WinUtil.App.exe → KOTU.exe가 실제 사례) 등록은 살아 있는데
    // 없는 파일을 가리켜 더블클릭이 '어떤 앱으로 열까요' 팝업으로 빠진다.
    // 그래서 매 실행 시 등록된 경로와 현재 경로를 비교해 다르면 조용히 재등록한다.
    //
    // 확정된 결정(2026-08-10):
    //  · 이미 켜 둔 등록만 대상 — 꺼 둔 것을 임의로 켜지 않는다.
    //  · 검사는 모듈당 대표 확장자 1개 — RegisterAssociation이 모듈의 전 확장자를 한 번에
    //    다시 쓰므로 하나만 어긋나도 전체 재기록으로 충분하다.
    //  · 조용히 처리(알림 없음). 실패해도 무시 — 다음 실행에서 다시 시도된다.
    //  · UserChoice(A38)는 ProgID만 참조하므로 여기서 건드리지 않는다.

    /// <summary>
    /// 켜져 있는 탐색기 등록(파일 연결·우클릭 메뉴)의 exe 경로가 현재 경로와 다르면 재등록한다.
    /// 앱 시작 시 워커 스레드에서 매 실행 호출(App.ShellRegistrationMaintenance).
    /// </summary>
    /// <param name="modules">전체 모듈(확장자 없는 모듈은 자동 제외).</param>
    /// <param name="archiveBrandLabel">우클릭 메뉴 라벨용 압축 모듈 BrandName.</param>
    public static void ReRegisterIfExeMoved(IReadOnlyList<IModule> modules, string archiveBrandLabel)
    {
        try
        {
            // A59: 연결 대상이 아닌 모듈(All Readable)은 확장자가 있어도 등록 자체를 하지 않는다.
            foreach (var module in modules.Where(m => m.SupportedExtensions.Count > 0
                                                      && m.RegistersFileAssociations))
            {
                // A292: 등록 단위가 확장자가 되면서 재등록도 <b>지금 등록돼 있는 확장자만</b> 다시 쓴다 —
                // 종전 RegisterAssociation(모듈 전체)을 그대로 부르면 사용자가 꺼 둔 확장자까지
                // 되살아난다("이미 켜 둔 등록만 대상" 원칙 유지). 검사는 종전대로 대표 1개
                // (AssociationCommandIsCurrent)로 충분하다 — 등록 시점이 같아 함께 어긋난다.
                var registeredExts = module.SupportedExtensions
                    .Where(ext => IsExtensionAssociationRegistered(module, ext)).ToList();
                if (registeredExts.Count == 0) continue;
                if (AssociationCommandIsCurrent(module)) continue;
                foreach (var ext in registeredExts)
                    RegisterExtensionAssociation(module, ext); // command·DefaultIcon·Capabilities 재기록
            }

            var archiveExts = modules.FirstOrDefault(m => m.Id == "archive")?.SupportedExtensions
                              ?? (IReadOnlyList<string>)[];
            if (archiveExts.Count > 0 && IsExtractHereMenuRegistered(archiveExts) && !VerbCommandIsCurrent(
                    $@"Software\Classes\SystemFileAssociations\{archiveExts[0]}\shell\{ExtractHereVerbName}\command"))
            {
                RegisterExtractHereMenu(archiveExts, archiveBrandLabel);
            }

            if (IsCompressMenuRegistered() && !VerbCommandIsCurrent(CompressVerbKeyPath + @"\command"))
                RegisterCompressMenu(archiveBrandLabel);
        }
        catch
        {
            // 재등록 실패는 치명적이지 않다 — 다음 실행에서 다시 시도된다.
        }
    }

    /// <summary>
    /// 모듈의 파일 연결 command가 현재 exe를 가리키는지 — 대표 확장자 1개만 검사.
    /// ProgID는 있는데 command를 못 읽는 경우와, 확장자별 ProgID가 하나도 없는
    /// 구 형태(모듈 단일 ProgID, v0.60.0 이전) 등록은 '어긋남'으로 보고 재등록시킨다(손상 복구 겸용).
    /// </summary>
    private static bool AssociationCommandIsCurrent(IModule module)
    {
        foreach (var ext in module.SupportedExtensions)
        {
            using var cmd = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ExtProgId(module, ext)}\shell\open\command");
            if (cmd is null) continue;
            return ShellCommand.IsSameExe(ShellCommand.ExtractExePath(cmd.GetValue(null) as string), ExePath);
        }
        return false;
    }

    /// <summary>우클릭 verb의 command 키가 현재 exe를 가리키는지.</summary>
    private static bool VerbCommandIsCurrent(string commandKeyPath)
    {
        using var cmd = Registry.CurrentUser.OpenSubKey(commandKeyPath);
        return cmd is not null &&
               ShellCommand.IsSameExe(ShellCommand.ExtractExePath(cmd.GetValue(null) as string), ExePath);
    }

    // ---------- 공통 ----------

    /// <summary>탐색기에 연결 변경을 알린다(아이콘/메뉴 캐시 갱신).</summary>
    private static void NotifyShell() =>
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */,
            IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
