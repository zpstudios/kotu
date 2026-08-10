using System.Runtime.InteropServices;

namespace KOTU.App;

/// <summary>
/// 작업표시줄·Alt-Tab 아이콘 보정(v0.20.1). unpackaged 앱에서 AppWindow.SetIcon만으로는
/// 작업표시줄에 기본 문서 아이콘이 나오는 문제(실기기 스크린샷 확인)가 있어,
/// Win32 WM_SETICON(ICON_SMALL/ICON_BIG)을 창 HWND에 직접 보낸다.
/// </summary>
internal static class WindowIcon
{
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const uint ImageIcon = 1;       // LoadImage: IMAGE_ICON
    private const uint LrLoadFromFile = 0x0010;

    /// <summary>경로별 로드 핸들 캐시 — 모듈 전환마다 교체(v0.26.0)해도 핸들이 새지 않게.</summary>
    private static readonly Dictionary<string, (IntPtr Small, IntPtr Big)> s_cache = new();

    /// <summary>
    /// 창의 작업표시줄/타이틀바 아이콘을 .ico 파일로 강제 지정.
    /// 핸들은 캐시에 남겨 창 수명 동안 유효(프로세스 종료 시 OS 정리).
    /// </summary>
    public static void Apply(Microsoft.UI.Xaml.Window window, string icoPath)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!s_cache.TryGetValue(icoPath, out var icons))
        {
            icons = (LoadImageW(IntPtr.Zero, icoPath, ImageIcon, 16, 16, LrLoadFromFile),
                     LoadImageW(IntPtr.Zero, icoPath, ImageIcon, 32, 32, LrLoadFromFile));
            s_cache[icoPath] = icons;
        }
        if (icons.Small != IntPtr.Zero) SendMessageW(hwnd, WmSetIcon, (IntPtr)IconSmall, icons.Small);
        if (icons.Big != IntPtr.Zero) SendMessageW(hwnd, WmSetIcon, (IntPtr)IconBig, icons.Big);
    }

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
