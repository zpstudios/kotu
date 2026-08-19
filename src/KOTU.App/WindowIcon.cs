using System.Runtime.InteropServices;

namespace KOTU.App;

/// <summary>
/// 작업표시줄·Alt-Tab 아이콘 보정(v0.20.1). unpackaged 앱에서 AppWindow.SetIcon만으로는
/// 작업표시줄에 기본 문서 아이콘이 나오는 문제(실기기 스크린샷 확인)가 있어,
/// Win32 WM_SETICON(ICON_SMALL/ICON_BIG)을 창 HWND에 직접 보낸다.
/// A137: 두 프레임이 <b>서로 다른 실시간 정보</b>를 담는다 — 16px(타이틀바) = 인스턴스 번호,
/// 32px(작업표시줄) = 열린 파일의 확장자/용량 2줄 또는 유휴 3자 이니셜(합성은 InstanceIcon).
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
    /// 창(HWND)별 마지막 <b>캐시 밖</b> 32px 합성본(A137 ② 열림 타일 — 파일별 용량이 들어가
    /// InstanceIcon.s_cache의 유계 전제를 깨므로 캐시 없이 만든 핸들). WM_SETICON은 핸들을
    /// 복사하지 않으므로 표시 중에는 살려 두고, <b>다음 교체가 창에 걸린 뒤에야</b> 이전 것을
    /// DestroyIcon 한다(트레이 TrayIcon.Swap의 지연 회수와 같은 규칙). 창이 닫히면 마지막 1개가
    /// 남지만 프로세스 수명 캐시들과 같은 수준의 잔존이라 별도 정리를 만들지 않는다
    /// (저장·파일 전환마다 교체-회수가 돌므로 증식하지 않는다).
    /// </summary>
    private static readonly Dictionary<IntPtr, IntPtr> s_dynamicBig = new();

    /// <summary>
    /// 창의 작업표시줄/타이틀바 아이콘을 지정한다. 반환 = 의도한 합성이 성립했는지
    /// (false = 무테두리 원본으로 통째 폴백 — 호출자는 재합성 스킵 키를 비워 다음 갱신 때 재시도).
    /// 캐시된 핸들(합성·원본)은 창 수명 동안 유효(프로세스 종료 시 OS 정리)하고,
    /// 캐시 밖 열림 타일만 s_dynamicBig로 지연 회수한다.
    ///
    /// A137 프레임 분리(같은 주제의 이력은 InstanceIcon.GetNumberTile 주석 참고):
    ///  · 16px = 인스턴스 번호 타일(<paramref name="instanceNumber"/> — A136부터 창이 하나여도 1).
    ///    번호가 아직 없는 생성 직후(0)만 종전 경로(합성 무번호·원본)로 간다.
    ///  · 32px = 열림(<paramref name="openLine1"/>/<paramref name="openLine2"/> — 셸이 현재 경로로
    ///    만든 확장자/용량)이면 2줄 타일, 유휴 + 규칙 안 모듈(<paramref name="idleFill"/> 있음)이면
    ///    3자 이니셜 전면 채움 타일, 규칙 밖(하드웨어·중립)은 종전 그대로 —
    ///    .ico 본체 + 링/라벨 합성(A102/A105) 또는 브랜드 원본(A79).
    /// A105 ②의 16px 무라벨 사유(줄 높이 절반 산정 시 폰트 4.8px &lt; 하한 5px)는 번호 0 폴백
    /// 경로에서 그대로 유효하다 — 번호 타일은 전면 1줄이라 그 제약에 걸리지 않는다.
    /// </summary>
    /// <param name="accent">현재 아이콘의 모듈 색(중립 아이콘이면 null) — A79 표식·A105 글자색 판단용.</param>
    /// <param name="ring">테두리 링 색(A102, Branding.IconRing — 실제 고른 .ico 기준) — null이면 링 없음.
    /// 타일 경로의 링은 이 값이 아니라 idleFill에서 나온다(InstanceIcon.TileRing 주석 참고).</param>
    /// <param name="label">모듈 3자 표기(A105 ② — MainWindow.IdleTrayLabel 단일 표) — null이면 글자 없음.</param>
    /// <param name="idleFill">A140 색 규칙의 판정 축(Branding.IdleFill — 모듈 ID 기준). null =
    /// 규칙 밖(하드웨어·중립) — 타일 대신 종전 합성이 유지된다(하드웨어 = 전용 색·링 없음).</param>
    /// <param name="instanceNumber">창 번호(A2/A136 — WindowManager가 1부터 배정). 0 = 미배정.</param>
    /// <param name="openLine1">열린 파일의 확장자 표기(A137 ② — TrayFormat.Extension). null = 유휴.</param>
    /// <param name="openLine2">열린 파일의 용량 표기(TrayFormat.Size) — openLine1과 늘 쌍으로 온다.</param>
    public static bool Apply(Microsoft.UI.Xaml.Window window, string icoPath,
        Windows.UI.Color? accent = null, Windows.UI.Color? ring = null, string? label = null,
        Windows.UI.Color? idleFill = null, int instanceNumber = 0,
        string? openLine1 = null, string? openLine2 = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // "열림" = 규칙 안 모듈에서 셸이 실경로 값을 만들어 준 경우뿐(둘 다 있어야 성립).
        var open = idleFill is not null && openLine1 is not null && openLine2 is not null;

        var small = instanceNumber > 0
            ? InstanceIcon.GetNumberTile(16, instanceNumber, idleFill, open)
            : ring is not null || label is not null
                ? InstanceIcon.GetComposed(icoPath, 16, accent, ring, label: null)
                : BrandIcons.GetBranded(icoPath, 16, accent);

        var bigDynamic = false;
        IntPtr big;
        if (open)
        {
            big = InstanceIcon.ComposeOpenTile(32, idleFill!.Value, openLine1!, openLine2!);
            bigDynamic = big != IntPtr.Zero;
        }
        else if (idleFill is { } fill && label is not null)
        {
            big = InstanceIcon.GetIdleTile(32, fill, label);
        }
        else
        {
            big = ring is not null || label is not null
                ? InstanceIcon.GetComposed(icoPath, 32, accent, ring, label)
                : BrandIcons.GetBranded(icoPath, 32, accent);
        }

        var ok = small != IntPtr.Zero && big != IntPtr.Zero;
        if (!ok)
        {
            // 한쪽만 성공해도 혼합 표시가 되지 않게 통째로 폴백. 버려지는 캐시 밖 합성본은 여기서만
            // 즉시 파괴가 안전하다 — 아직 WM_SETICON에 주지 않아 표시 참조가 없다.
            if (bigDynamic) _ = DestroyIcon(big);
            bigDynamic = false;
            (small, big) = LoadPlain(icoPath);
        }
        if (small != IntPtr.Zero) SendMessageW(hwnd, WmSetIcon, (IntPtr)IconSmall, small);
        if (big != IntPtr.Zero)
        {
            SendMessageW(hwnd, WmSetIcon, (IntPtr)IconBig, big);
            // 새 32px이 창에 걸린 뒤에만 이전 캐시 밖 합성본을 회수한다. big을 못 만든 경우는
            // 창이 이전 핸들을 계속 표시 중이므로 회수하면 안 된다 — 이 호출을 if 밖으로 빼지 말 것.
            ReplaceDynamicBig(hwnd, bigDynamic ? big : IntPtr.Zero);
        }
        return ok;
    }

    /// <summary>
    /// 창별 캐시 밖 32px 핸들 교체(A137) — 이전 것은 새 핸들이 이미 창에 걸린 뒤라 파괴해도 안전.
    /// current가 Zero(이번 표시가 캐시본·원본)면 추적만 끊고 이전 것을 회수한다.
    /// </summary>
    private static void ReplaceDynamicBig(IntPtr hwnd, IntPtr current)
    {
        if (s_dynamicBig.TryGetValue(hwnd, out var previous)
            && previous != IntPtr.Zero && previous != current)
        {
            _ = DestroyIcon(previous);
        }
        if (current == IntPtr.Zero) s_dynamicBig.Remove(hwnd);
        else s_dynamicBig[hwnd] = current;
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

    [DllImport("user32")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
