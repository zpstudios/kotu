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
    /// ring이 있으면(A102 — 모듈 색, 창 개수 무관) 모듈 색 테두리를 합성한 아이콘
    /// (InstanceIcon — 역시 프로세스 수명 캐시)을 대신 지정한다. 합성 실패 시 무테두리 원본으로 폴백.
    /// A105 ②(v0.143.0): label이 있으면 32px 아이콘 하단에 모듈 3자 표기까지 합성한다 —
    /// 링이 없어도(정보 모듈 INF) label만으로 합성 경로에 들어간다.
    /// A79(v0.119.0): 링·글자가 없어도 브랜드 표식(BrandIcons)이 켜져 있으면 합성본을 쓴다.
    /// 표식이 꺼져 있으면 GetBranded가 0을 돌려주므로 지금까지처럼 파일을 그대로 로드한다.
    /// </summary>
    /// <param name="accent">현재 아이콘의 모듈 색(중립 아이콘이면 null) — A79 표식·A105 글자색 판단용.</param>
    /// <param name="ring">테두리 링 색(A102, Branding.IconRing) — null이면 링 없음.</param>
    /// <param name="label">모듈 3자 표기(A105 ② — MainWindow.IdleTrayLabel 단일 표) — null이면 글자 없음.</param>
    public static void Apply(Microsoft.UI.Xaml.Window window, string icoPath,
        Windows.UI.Color? accent = null, Windows.UI.Color? ring = null, string? label = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // A105 ②: 16px(타이틀바 소형)에는 글자를 넣지 않는다(label: null) — 줄 높이 절반 산정으로는
        // 폰트가 약 4.8px가 되어 A54 실증 하한(5px) 아래로 떨어지고, 다크 판이 16px 본체 글씨(A3)의
        // 절반 가까이를 덮는다. 뭉개질 바에는 생략(구현 시 결정 — 32px 가독 우선).
        // 링 없이 label만 있는 경우(정보 모듈)에도 16px을 같은 합성 경로로 태우는 이유:
        // 16px만 GetBranded로 보내면 표식 꺼짐(레벨 0)에서 0이 돌아와, 아래 "한쪽 실패 = 통째 폴백"
        // 규칙이 정상 조합(16 무글자 + 32 글자)을 실패로 오인해 32px 글자까지 잃는다.
        var icons = ring is not null || label is not null
            ? (Small: InstanceIcon.GetComposed(icoPath, 16, accent, ring, label: null),
               Big: InstanceIcon.GetComposed(icoPath, 32, accent, ring, label))
            : (Small: BrandIcons.GetBranded(icoPath, 16, accent),
               Big: BrandIcons.GetBranded(icoPath, 32, accent));
        if (icons.Small == IntPtr.Zero || icons.Big == IntPtr.Zero)
            icons = LoadPlain(icoPath); // 한쪽만 성공해도 혼합 표시가 되지 않게 통째로 폴백
        if (icons.Small != IntPtr.Zero) SendMessageW(hwnd, WmSetIcon, (IntPtr)IconSmall, icons.Small);
        if (icons.Big != IntPtr.Zero) SendMessageW(hwnd, WmSetIcon, (IntPtr)IconBig, icons.Big);
    }

    private static (IntPtr Small, IntPtr Big) LoadPlain(string icoPath)
    {
        if (!s_cache.TryGetValue(icoPath, out var icons))
        {
            icons = (LoadImageW(IntPtr.Zero, icoPath, ImageIcon, 16, 16, LrLoadFromFile),
                     LoadImageW(IntPtr.Zero, icoPath, ImageIcon, 32, 32, LrLoadFromFile));
            s_cache[icoPath] = icons;
        }
        return icons;
    }

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
