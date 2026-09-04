using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KOTU.App.Integration;

/// <summary>
/// 창별 작업표시줄 그룹 분리 (A105 ①, v0.143.0) — 창 HWND에 인스턴스 고유
/// AppUserModelID(AUMID)를 지정해, 같은 exe의 창들이 하나로 묶이는 태스크바 기본 그룹핑을
/// 인스턴스 단위로 갈라놓는다(트레이 아이콘이 인스턴스별로 뜨는 것과 정합 —
/// 2026-08-14 사용자 정정: 모듈별 아님).
///
/// AUMID 값 = "KOTU.Instance.{n}" — n은 A100 트레이 슬롯 번호(창 생성 단조 시퀀스,
/// <see cref="TrayIcon.Slot"/>)를 그대로 재사용한다. 표시용 인스턴스 번호(A2)를 쓰지 않는
/// 이유: 그 번호는 중간 창이 닫히면 재배정되는데(당겨오기) AUMID가 따라 바뀌면 창 하나 닫을
/// 때마다 남은 창들의 태스크바 그룹이 재편된다. 슬롯 번호는 창 수명 동안 불변이고,
/// 같은 순서로 창을 열면 실행이 달라도 같은 값이 나온다(A100과 같은 결정화 성질).
///
/// 절차는 표준: SHGetPropertyStoreForWindow(shell32)로 창의 IPropertyStore를 얻어
/// PKEY_AppUserModelID에 VT_LPWSTR 문자열을 쓰고 Commit. 호출 시점은 창 표시 전
/// (HWND 확보 직후, Activate 전 = MainWindow 생성자) 1회이며, 실패는 전부 조용히 무시한다
/// (A100 스타일 — 실패하면 공유 AUMID(exe 기본)로 후퇴해 그룹만 합쳐질 뿐 동작 무영향).
///
/// 수용된 트레이드오프(사양 — 코드 대응 없음): 창별 AUMID는 작업표시줄 고정(pin)·점프리스트가
/// 인스턴스 단위로 갈라진다. 고정한 버튼은 다음 실행에서 같은 슬롯 번호의 창하고만 다시
/// 만나고, Velopack 바로가기의 AUMID와도 달라 시작 메뉴 그룹 연결이 느슨해질 수 있다.
///
/// A350(2026-09-04): 창마다 AUMID를 따로 박으면 그 AUMID가 곧 앱의 정체가 되므로,
/// 비패키지 앱은 AUMID별 표시 정보를 직접 등록해야 한다. 등록이 없으면 미디어 플라이아웃(SMTC)이
/// 세션 주인을 "알 수 없는 앱"으로 표시한다(v0.342.0 실사례). 등록 위치는 표준 경로
/// HKCU\Software\Classes\AppUserModelId\{AUMID}의 DisplayName(REG_SZ) + IconUri(절대 경로) —
/// Firefox 등 다른 비패키지 플레이어가 쓰는 것과 같은 방식이다.
/// 키 수 = 한 사용자가 열어 본 최대 슬롯 번호만큼(슬롯은 창 생성 단조 시퀀스라 작고 안정적).
/// 청소 지점은 둘이다: ① 프로세스당 1회 구 브랜드 접두 키 스캔 삭제(리브랜딩 규칙 —
/// 지금은 KOTU 이전 브랜드가 이 키를 만든 적이 없어 실제 삭제 대상 0건) ② 제거(uninstall) 시
/// Velopack 훅에서 <see cref="RemoveAllDisplayKeys"/>로 현재·구 브랜드 접두 키 전부 삭제.
/// 전 구간 HKCU라 관리자 권한이 필요 없고, 모든 실패는 조용히 무시한다(A100 스타일).
///
/// A350 후속(v0.343.1): 창 AUMID 표시 키만 등록해서는 SMTC(미디어 플라이아웃) 이름이 잡히지
/// 않았다(v0.343.0 실기기 확인 — 여전히 "알 수 없는 앱"). 남은 설명은 셸이 SMTC 세션 주인을
/// 창이 아니라 **프로세스 AUMID**로 식별한다는 것이다. KOTU는 프로세스 수준 AUMID를 명시한 적이
/// 없어 셸이 exe 경로에서 유추하고, 그 유추 값에는 등록된 표시 이름이 없다. 그래서
/// <see cref="ApplyProcessIdentity"/>로 프로세스 AUMID를 "KOTU"로 못 박고 같은 이름의 표시 키를
/// 등록한다(Firefox 등 비패키지 플레이어가 쓰는 것과 같은 방식).
/// 창별 AUMID(<see cref="Apply"/>)는 그대로 둔다 — 창 프로퍼티 스토어 값이 프로세스 값을 덮으므로
/// 태스크바 인스턴스 분리(A105)는 영향을 받지 않는다.
/// </summary>
internal static class TaskbarIdentity
{
    /// <summary>PKEY_AppUserModelID = fmtid {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3} / pid 5.</summary>
    private static readonly PropertyKey s_pkeyAppUserModelId = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    /// <summary>IPropertyStore의 IID — SHGetPropertyStoreForWindow의 riid 인자.</summary>
    private static readonly Guid s_iidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    private const ushort VtLpwstr = 31; // PROPVARIANT.vt — CoTaskMem 유니코드 문자열

    /// <summary>
    /// 창에 인스턴스 고유 AUMID를 지정한다. UI 스레드에서 창 표시(Activate) 전에 부를 것.
    /// 실패는 전부 조용히 무시 — 호출자는 결과를 알 필요가 없다.
    /// </summary>
    /// <param name="sequence">창 생성 단조 시퀀스 — A100 트레이 슬롯 번호(<see cref="TrayIcon.Slot"/>).</param>
    public static void Apply(Microsoft.UI.Xaml.Window window, int sequence)
    {
        try
        {
            var aumid = $"{Branding.AppName}.Instance.{sequence}";
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var iid = s_iidPropertyStore; // ref 인자용 복사 — readonly 원본 보호
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) >= 0 && store is not null)
                WriteAumidToStore(store, aumid);

            // A350: 프로퍼티 스토어 성공 여부와 무관하게 같은 AUMID로 표시 정보를 등록한다.
            // (스토어가 실패했다면 창은 exe 기본 AUMID로 후퇴해 이 키가 쓰이지 않지만, 남아도 무해하고
            //  다음 실행에서 성공하면 그대로 쓰인다 — 그래서 성패로 분기하지 않는다.)
            RegisterDisplay(aumid);
        }
        catch
        {
            // 어떤 실패(OS 변형·마샬링 오류)도 앱 동작에 영향을 주면 안 된다 — 공유 AUMID로 후퇴.
        }
    }

    /// <summary>
    /// 프로세스 수준 AUMID를 브랜드 이름("KOTU")으로 못 박고, 같은 이름의 표시 키를 등록한다.
    /// Program.Main에서 창이 하나도 만들어지기 전에 1회 부를 것 — 프로세스 AUMID는 첫 창이
    /// 생기기 전에 박아야 셸이 인식한다(창이 생긴 뒤 바꾸면 이미 굳은 식별자가 남는다).
    /// 값을 브랜드 이름 그대로 쓰는 이유: Velopack packId·시작 메뉴 바로가기와 같은 이름이라
    /// 고정(pin)·시작 메뉴 그룹과도 정합이다. 실패는 전부 조용히 무시한다.
    /// </summary>
    internal static void ApplyProcessIdentity()
    {
        try
        {
            // 반환값은 HRESULT지만 실패해도 할 수 있는 일이 없다 — 셸이 exe 경로로 유추하는
            // 종전 동작으로 후퇴할 뿐이다.
            _ = SetCurrentProcessExplicitAppUserModelID(Branding.AppName);
        }
        catch
        {
            // OS 변형·마샬링 오류 — 앱 동작에는 영향이 없다.
        }

        // P/Invoke 성패와 무관하게 표시 키는 등록해 둔다(멱등). 이번 실행에서 셸이 유추 AUMID를
        // 썼더라도 키가 남아 있는 것은 무해하고, 다음 실행에서 그대로 쓰인다.
        RegisterDisplay(Branding.AppName);
    }

    /// <summary>
    /// 창의 프로퍼티 스토어에 PKEY_AppUserModelID를 쓰고 커밋한다.
    /// (A350에서 Apply 본문에서 분리 — 스토어 획득 실패로 조기 반환해도 표시 키 등록은 계속되게 하기 위함.)
    /// </summary>
    private static void WriteAumidToStore(IPropertyStore store, string aumid)
    {
        try
        {
            // 수동 PROPVARIANT(VT_LPWSTR): 문자열을 CoTaskMem에 두고, 쓰기가 끝나면
            // 성패와 무관하게 PropVariantClear로 해제한다(내부의 CoTaskMemFree까지 담당).
            var value = new PropVariant
            {
                vt = VtLpwstr,
                p = Marshal.StringToCoTaskMemUni(aumid),
            };
            try
            {
                var key = s_pkeyAppUserModelId; // ref 인자용 복사
                if (store.SetValue(ref key, ref value) >= 0) _ = store.Commit();
            }
            finally
            {
                _ = PropVariantClear(ref value);
            }
        }
        finally
        {
            // RCW 즉시 해제 — 창마다 1회뿐인 일회성 객체라 GC 지연 해제에 기대지 않는다.
            _ = Marshal.FinalReleaseComObject(store);
        }
    }

    // ---------- A350: AUMID 표시 이름 · 아이콘 ----------

    /// <summary>비패키지 앱의 AUMID별 표시 정보 등록 뿌리(HKCU). 관리자 권한 불필요.</summary>
    private const string DisplayKeyRoot = @"Software\Classes\AppUserModelId";

    /// <summary>AUMID 중간 마디 — 브랜드 이름 뒤에 붙는 고정 접두사(청소 스캔의 판별 기준).</summary>
    private const string InstanceInfix = ".Instance.";

    /// <summary>구 브랜드 키 청소를 프로세스당 1회로 제한하는 플래그(창마다 재스캔할 이유가 없다).</summary>
    private static bool s_legacyDisplayKeysCleaned;

    /// <summary>
    /// 창 AUMID에 표시 이름·아이콘을 등록한다(멱등 — 값이 이미 같으면 쓰지 않는다).
    /// 아이콘은 트레이·타이틀바가 읽는 그 파일(exe 옆 Assets\app.ico)을 절대 경로로 가리킨다.
    /// </summary>
    private static void RegisterDisplay(string aumid)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(DisplayKeyRoot + "\\" + aumid))
            {
                if (key is null) return;

                if (key.GetValue("DisplayName") as string != Branding.AppName)
                    key.SetValue("DisplayName", Branding.AppName, RegistryValueKind.String);

                // 아이콘 파일이 없으면 IconUri는 아예 쓰지 않는다 — 깨진 경로를 남기느니
                // 셸 기본 아이콘 폴백이 낫다.
                var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(icon) && key.GetValue("IconUri") as string != icon)
                    key.SetValue("IconUri", icon, RegistryValueKind.String);
            }

            CleanLegacyDisplayKeysOnce();
        }
        catch
        {
            // 등록 실패 = 플라이아웃이 앱 이름을 못 찾을 뿐 — 앱 동작에는 영향이 없다.
        }
    }

    /// <summary>
    /// 구 브랜드 접두 표시 키를 프로세스당 1회 스캔해 지운다(예: ZP.Instance.0).
    /// 현재는 KOTU 이전 브랜드(ZP·WinUtil)가 이 키를 만든 적이 없어 실제 삭제 대상은 0건이지만,
    /// 리브랜딩 규칙(구 등록은 발견되는 대로 지운다)상 스캔 코드를 선제로 둔다.
    /// </summary>
    private static void CleanLegacyDisplayKeysOnce()
    {
        if (s_legacyDisplayKeysCleaned) return;
        s_legacyDisplayKeysCleaned = true; // 실패해도 재시도하지 않는다(창마다 순회할 가치가 없다).
        DeleteDisplayKeys(ExplorerIntegration.LegacyBrandNames.Select(b => b + InstanceInfix));
    }

    /// <summary>
    /// 현재·구 브랜드의 모든 창 AUMID 표시 키를 지운다. 제거(uninstall) 직전에
    /// Velopack 훅(OnBeforeUninstallFastCallback)이 부른다 — Program.Main 참조.
    /// </summary>
    internal static void RemoveAllDisplayKeys()
    {
        DeleteDisplayKeys(
            new[] { Branding.AppName + InstanceInfix }
                .Concat(ExplorerIntegration.LegacyBrandNames.Select(b => b + InstanceInfix)));

        // v0.343.1: 프로세스 AUMID 키는 브랜드 이름 **그대로**라 위 접두사 스캔에 걸리지 않는다
        // (접두사는 ".Instance."까지 포함한다) — 이름이 정확히 일치하는 키를 따로 지운다.
        // 구 브랜드 이름(ZP·WinUtil)은 이 키를 만든 적이 없지만, 구 등록은 발견되는 대로 지운다는
        // 리브랜딩 규칙에 맞춰 함께 넣는다(없으면 조용히 지나간다).
        DeleteDisplayKeysExact(
            new[] { Branding.AppName }.Concat(ExplorerIntegration.LegacyBrandNames));
    }

    /// <summary>AppUserModelId 뿌리에서 이름이 정확히 일치하는 서브키를 지운다(접두 스캔과 별도).</summary>
    private static void DeleteDisplayKeysExact(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    DisplayKeyRoot + "\\" + name, throwOnMissingSubKey: false);
            }
            catch
            {
                // 이름마다 개별 try/catch — 하나가 권한·동시 삭제로 던져도 나머지는 계속 지운다.
            }
        }
    }

    /// <summary>AppUserModelId 뿌리에서 주어진 접두사로 시작하는 서브키를 전부 지운다.</summary>
    private static void DeleteDisplayKeys(IEnumerable<string> prefixes)
    {
        try
        {
            var list = prefixes.ToArray();
            if (list.Length == 0) return;

            using var root = Registry.CurrentUser.OpenSubKey(DisplayKeyRoot, writable: true);
            if (root is null) return; // 뿌리 자체가 없으면 지울 것도 없다

            foreach (var name in root.GetSubKeyNames())
            {
                if (!list.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                }
                catch
                {
                    // 항목마다 개별 try/catch — 하나가 권한·동시 삭제로 던져도 나머지는 계속 지운다.
                }
            }
        }
        catch
        {
            // 뿌리 접근 자체 실패(권한·정책)도 조용히 무시한다.
        }
    }

    // ---------- P/Invoke · COM ----------
    // 이 저장소 최초의 COM 인터페이스 interop(선례 없음 — 오케스트레이터 grep 확인).
    // 선언은 마이크로소프트 문서 시그니처 그대로이며, 다른 기능과 격리된 이 파일 하나에만 있다 —
    // CI 실패 시 최소 복구 = MainWindow 생성자의 Apply 호출 1곳 주석 처리로 끝난다.

    /// <summary>PROPERTYKEY (propkeydef.h) — fmtid + pid.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>
    /// PROPVARIANT (propidlbase.h) 최소 선언 — vt 헤더 8바이트 + 값 union 16바이트(x64 총 24).
    /// VT_LPWSTR만 쓰므로 union은 포인터 자리(p)만 의미가 있고 나머지는 크기 맞춤이다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;  // VT_LPWSTR: CoTaskMem 문자열 포인터
        public IntPtr p2; // union 잔여 크기 맞춤 (x64에서 union은 16바이트)
    }

    /// <summary>
    /// IPropertyStore (propsys.h). 실제로 부르는 것은 SetValue·Commit뿐이지만
    /// vtable 자리가 맞아야 하므로 다섯 메서드를 문서의 순서 그대로 전부 선언한다.
    /// </summary>
    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant propvar);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// <remarks>
    /// shell32의 SetCurrentProcessExplicitAppUserModelID(PCWSTR) — 반환은 HRESULT.
    /// 문자열 인자 하나뿐이라 마샬링 위험이 낮고, CharSet.Unicode 지정은 같은 저장소의
    /// Shell_NotifyIconW 선언(TrayIcon.cs)과 같은 형태다.
    /// </remarks>
    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
