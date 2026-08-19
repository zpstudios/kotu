using System.Runtime.InteropServices;

namespace KOTU.App.Integration;

/// <summary>
/// Windows 기본 입력(마이크) 장치 변경 (A164) — 비공개 COM IPolicyConfig의
/// SetDefaultEndpoint 하나만 부른다. 모듈 프로젝트에는 DllImport·COM interop을 두지 않는다는
/// 규약에 따라 이 파일(셸)에만 있고, 모듈 진입은 Core 훅
/// (<see cref="KOTU.Core.Integration.DefaultAudioInputHook"/> — App이 시작 시 배선) 경유다.
///
/// IPolicyConfig는 문서화되지 않은 인터페이스다(OS 사운드 설정이 쓰는 경로 — 공개 API에는
/// 기본 장치 "변경"이 없다, 부록 B 70 ⓒ 확정). CLSID·IID·vtable 순서는 커뮤니티에서 오래
/// 검증된 정의를 그대로 옮겼고, 실제로 부르는 슬롯은 SetDefaultEndpoint 하나다.
///
/// 실패는 전부 false로 접힌다(OS 변형·인터페이스 변경·권한). 호출은 UI 스레드(STA —
/// 플라이아웃 클릭 직행)에서 온다. TaskbarIdentity(A105)와 같은 격리 파일 패턴이라
/// CI 실패 시 최소 복구 = App.OnLaunched의 훅 배선 1줄 주석 처리로 끝난다(모듈은
/// 미배선이면 false를 받아 안내 폴백으로 접힌다).
/// </summary>
internal static class DefaultAudioInput
{
    /// <summary>
    /// 기본 입력 장치를 바꾼다. 인자 = MMDevice 엔드포인트 ID("{0.0.1.00000000}.{guid}" 꼴).
    /// Windows 설정의 "기본 장치"를 정의하는 콘솔·멀티미디어 역할을 함께 바꾸고, 별도 선택
    /// UI가 없는 통신 역할도 같은 장치로 맞춘다(A164 = 단일 선택 UI — 선택 하나가 전 역할을 덮는다).
    /// </summary>
    public static bool TrySetDefault(string endpointId)
    {
        try
        {
            // ComImport 코클래스의 new = CoCreateInstance (이 저장소 최초 사용 형태 —
            // TaskbarIdentity는 API 함수가 객체를 내줬고, 여기는 CLSID 직접 활성화가 필요하다).
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                // 성패 판정은 기본 장치를 정의하는 콘솔·멀티미디어 2건 — 통신 역할은
                // 부가 동기화라 실패해도 사용자 관점의 "기본 장치 변경"은 성립한다.
                var ok = policy.SetDefaultEndpoint(endpointId, Role.Console) >= 0;
                ok &= policy.SetDefaultEndpoint(endpointId, Role.Multimedia) >= 0;
                _ = policy.SetDefaultEndpoint(endpointId, Role.Communications);
                return ok;
            }
            finally
            {
                // RCW 즉시 해제 — 클릭마다 만드는 일회성 객체 (TaskbarIdentity와 같은 규칙).
                _ = Marshal.FinalReleaseComObject(policy);
            }
        }
        catch
        {
            // 활성화 실패·캐스팅 실패·마샬링 오류 전부 — 안내는 모듈(플라이아웃)이 한다.
            return false;
        }
    }

    /// <summary>ERole (mmdeviceapi.h) — 기본 장치의 용도 구분.</summary>
    private enum Role
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    /// <summary>CPolicyConfigClient 코클래스 — 멤버 없는 선언이 사양이다(활성화 전용).</summary>
    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }

    /// <summary>
    /// IPolicyConfig (비공개, Windows 10·11) 최소 선언. 실제로 부르는 것은 SetDefaultEndpoint뿐이지만
    /// vtable 자리가 맞아야 하므로 앞 메서드 10개를 알려진 순서 그대로 자리만 선언한다
    /// (TaskbarIdentity의 IPropertyStore 선언과 같은 방식). 미호출 슬롯은 호출되지 않는 한
    /// 인자 형식이 무관해 포인터 인자를 전부 IntPtr로 적는다.
    /// </summary>
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
        [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr deviceId, int storeType, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr deviceId, int storeType, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility(IntPtr deviceId, int visible);
    }
}
