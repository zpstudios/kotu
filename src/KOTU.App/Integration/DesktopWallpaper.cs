using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KOTU.App.Integration;

/// <summary>
/// 바탕화면 배경 지정 (A161, v0.174.0) — HKCU 표시 방식 값 + user32 SPI_SETDESKWALLPAPER.
/// 모듈 프로젝트에는 DllImport를 두지 않는다는 규약에 따라 P/Invoke와 레지스트리 쓰기는
/// 여기(셸)에만 있고, 모듈 진입은 Core 훅(<see cref="KOTU.Core.Integration.DesktopWallpaperHook"/> —
/// App이 시작 시 배선) 경유다.
///
/// 넘어오는 경로는 <b>이미 PNG로 변환된 임시 파일</b>이다(이미지 모듈이 워커에서 만든다):
/// SPI_SETDESKWALLPAPER는 형식을 가려(psd·webp·ico 등은 못 읽는다) 원본을 그대로 넘길 수 없다.
///
/// 다중 모니터는 <b>별도 처리하지 않는다</b>(IDesktopWallpaper COM 미사용) — SPI 경로는
/// Windows가 모든 모니터에 같은 배경을 적용한다. 채우기 방식은 Fill 고정이라 선택 UI도 없다(A161 확정).
///
/// 호출은 모듈의 뷰 전용 워커 스레드에서 온다(UI 스레드 아님). 레지스트리·SPI 모두 스레드
/// 친화성이 없어 그대로 성립한다.
/// </summary>
internal static class DesktopWallpaper
{
    /// <summary>SPI_SETDESKWALLPAPER — pvParam이 배경 이미지 경로(널 종료 문자열)다.</summary>
    private const uint SpiSetDeskWallpaper = 0x0014;

    /// <summary>SPIF_UPDATEINIFILE — 사용자 프로필에 기록해 다음 로그온에도 유지되게 한다.</summary>
    private const uint SpifUpdateIniFile = 0x01;

    /// <summary>SPIF_SENDWININICHANGE — 변경을 방송해 데스크톱이 즉시 다시 그려지게 한다.</summary>
    private const uint SpifSendWinIniChange = 0x02;

    /// <summary>표시 방식 키 — OS가 만들어 두는 키다(우리가 만드는 키가 아니다).</summary>
    private const string DesktopKeyPath = @"Control Panel\Desktop";

    /// <summary>WallpaperStyle "10" = Fill(채우기). A161 확정 — 선택 UI 없이 이 값 고정.</summary>
    private const string StyleFill = "10";

    /// <summary>
    /// 이미지를 배경으로 건다. 표시 방식을 먼저 쓰고(SPI가 걸 때 그 값을 읽는다) SPI를 부른다.
    /// 반환 = SPI 성공 여부 — 표시 방식 쓰기 실패는 성공/실패를 가르지 않는다(아래 주석 참고).
    /// </summary>
    public static bool TrySet(string imagePath)
    {
        ApplyFillStyle();
        return SystemParametersInfoW(SpiSetDeskWallpaper, 0, imagePath,
            SpifUpdateIniFile | SpifSendWinIniChange);
    }

    /// <summary>
    /// HKCU\Control Panel\Desktop의 표시 방식을 Fill로 맞춘다. 실패해도 삼키고 계속 간다 —
    /// 못 쓰면 직전 방식(맞춤·가운데 등)으로 걸릴 뿐 배경 자체는 바뀌므로 사용자에겐 성공이다.
    /// using·조용한 실패는 TrayPromotion(NotifyIconSettings)·ExplorerIntegration의 HKCU 쓰기 관례 그대로.
    /// </summary>
    private static void ApplyFillStyle()
    {
        try
        {
            // OS가 이미 만들어 둔 키라 CreateSubKey가 아니라 쓰기로 열기 — 없으면(이론상 불가) 무동작.
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKeyPath, writable: true);
            if (key is null) return;
            // 종류 명시(REG_SZ) — 이 두 값은 Windows가 문자열로만 읽는다(DWORD로 쓰면 조용히 무효).
            key.SetValue("WallpaperStyle", StyleFill, RegistryValueKind.String);
            key.SetValue("TileWallpaper", "0", RegistryValueKind.String); // Fill과 타일은 배타
        }
        catch
        {
            // 권한·정책으로 못 써도 배경 지정 시도는 계속한다(현상 유지 폴백).
        }
    }

    // W 접미 명시 + CharSet.Unicode = 저장소 P/Invoke 관례(TrayIcon.Shell_NotifyIconW·
    // Program.MessageBoxW). pvParam은 SPI_SETDESKWALLPAPER에서 경로 문자열이므로 string으로 받는다
    // (CharSet.Unicode라 LPWStr로 마샬링된다). 반환 BOOL → bool 기본 마샬링도 저장소 관례 그대로.
    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam,
        uint fWinIni);
}
