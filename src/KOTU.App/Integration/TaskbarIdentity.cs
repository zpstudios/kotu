using System.Runtime.InteropServices;

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
/// A350 이력(v0.343.2에서 정리): v0.343.0은 이 창별 AUMID마다 HKCU AppUserModelId 표시 키
/// (DisplayName·IconUri)를 등록했고, v0.343.1은 프로세스 AUMID를 "KOTU"로 못 박고 그 이름의 키를
/// 하나 더 등록했다 — 둘 다 v0.343.2에서 걷어내 이 파일은 v0.342.0 상태로 되돌아왔다.
/// 이유: 미디어 플라이아웃(SMTC)의 앱 이름은 AppUserModelId 레지스트리 키(그 키는 토스트 알림
/// 전용이다)가 아니라, 셸이 세션 소유 창의 AUMID로 시작 메뉴 바로가기를 역추적해 붙이는 값이다.
/// 그래서 어떤 바로가기와도 일치할 수 없는 "KOTU.Instance.{n}"으로는 매칭이 성립하지 않는다.
/// 게다가 프로세스 explicit AUMID는 Velopack 바로가기의 AUMID와 어긋나면 작업표시줄 고정을
/// 깨뜨릴 수 있다(velopack#104). 해법은 이 파일이 아니라 세션 창을 바꾸는 쪽이다 —
/// <see cref="MediaTransport"/>가 SMTC 세션을 창별 AUMID를 쓰지 않는 트레이 숨김 창에 건다.
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
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var iid = s_iidPropertyStore; // ref 인자용 복사 — readonly 원본 보호
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) < 0 || store is null)
                return;
            try
            {
                // 수동 PROPVARIANT(VT_LPWSTR): 문자열을 CoTaskMem에 두고, 쓰기가 끝나면
                // 성패와 무관하게 PropVariantClear로 해제한다(내부의 CoTaskMemFree까지 담당).
                var value = new PropVariant
                {
                    vt = VtLpwstr,
                    p = Marshal.StringToCoTaskMemUni($"{Branding.AppName}.Instance.{sequence}"),
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
        catch
        {
            // 어떤 실패(OS 변형·마샬링 오류)도 앱 동작에 영향을 주면 안 된다 — 공유 AUMID로 후퇴.
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
}
