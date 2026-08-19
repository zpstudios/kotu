namespace KOTU.Core.Integration;

/// <summary>
/// Windows 기본 입력(마이크) 장치 변경 훅 (A164). 모듈은 Core에만 의존하고
/// "모듈 프로젝트에는 DllImport·COM interop을 두지 않는다"(전부 셸에 격리 —
/// TaskbarIdentity·DesktopWallpaper) 규약 때문에, 오디오 모듈의 장치 플라이아웃이
/// 셸의 구현(KOTU.App.Integration.DefaultAudioInput — 비공개 COM IPolicyConfig)을
/// 직접 부를 수 없다. 셸(App)이 시작 시 <see cref="Setter"/>를 배선하고, 모듈은
/// <see cref="TrySetDefault"/>만 부른다 — <see cref="DesktopWallpaperHook"/>과 같은 배선 방식이다.
///
/// 인자 = MMDevice 엔드포인트 ID("{0.0.1.00000000}.{guid}" 꼴 — WinRT DeviceInformation.Id에서
/// 추출은 부르는 쪽 몫). 이 변경은 앱 밖(시스템 전역)에 영향을 준다 — UI에 안내 문구를 병기할 것.
///
/// DesktopWallpaperHook처럼 <b>결과를 돌려준다</b>: 사용자가 직접 누른 동작이라 실패 시
/// 플라이아웃 안내("Could not change the default device")가 필요하기 때문이다.
/// 실패는 전부 false로 접히고 예외는 새지 않는다. UI 스레드에서 불린다(플라이아웃 클릭 직행).
/// </summary>
public static class DefaultAudioInputHook
{
    /// <summary>셸이 배선하는 기본 입력 장치 변경 동작. 인자 = MMDevice 엔드포인트 ID.</summary>
    public static Func<string, bool>? Setter { get; set; }

    /// <summary>
    /// 기본 입력 장치를 바꾼다. 배선 전(이론상 도달 불가 — 셸이 첫 창을 만들기 전에
    /// 배선한다)이거나 실패하면 false. 예외도 false로 접는다.
    /// </summary>
    public static bool TrySetDefault(string endpointId)
    {
        try { return Setter?.Invoke(endpointId) ?? false; }
        catch { return false; /* 장치 변경 실패가 뷰를 죽이면 안 된다 — 안내는 호출 쪽 몫 */ }
    }
}
