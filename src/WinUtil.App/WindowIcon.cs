using System.Runtime.InteropServices;

namespace WinUtil.App;

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

    /// <summary>
    /// 창의 작업표시줄/타이틀바 아이콘을 .ico 파일로 강제 지정.
    /// 로드한 핸들은 창 수명 동안 유효해야 하므로 해제하지 않는다(프로세스 종료 시 OS 정리).
    /// </summary>
    public static void Apply(Microsoft.UI.Xaml.Window window, string icoPath)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var small = LoadImageW(IntPtr.Zero, icoPath, ImageIcon, 16, 16, LrLoadFromFile);
        var big = LoadImageW(IntPtr.Zero, icoPath, ImageIcon, 32, 32, LrLoadFromFile);
        if (small != IntPtr.Zero) SendMessageW(hwnd, WmSetIcon, (IntPtr)IconSmall, small);
        if (big != IntPtr.Zero) SendMessageW(hwnd, WmSetIcon, (IntPtr)IconBig, big);
    }

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
