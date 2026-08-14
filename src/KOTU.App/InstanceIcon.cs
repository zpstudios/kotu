using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 아이콘 런타임 합성 (A68 시작 → A102/v0.130.0에서 의미 개편) + 인스턴스 색 팔레트.
///
/// A102 전에는 이 합성이 "몇 번째 창인가"를 알리는 장치였다(인스턴스 9색 링 + 우하단 원형
/// 번호 배지). 지금은 <b>어느 모듈의 창인가</b>를 알리는 장치다:
/// ① 테두리 링 색 = 그 창 모듈의 액센트 색(<see cref="Branding.IconRing"/>),
/// ② 원형 번호 배지는 렌더 코드째 제거 — 번호는 창 제목의 접두 숫자(A103)가 담당한다.
/// 링은 창 개수와 무관하게 항상 그린다(모듈 식별이 목적이라 "2개 이상일 때만" 조건이 사라졌다).
/// 링을 두르지 않는 화면(중립 아이콘·정보 모듈)은 아예 이 클래스를 부르지 않고
/// <see cref="BrandIcons.GetBranded"/>로 간다 — 판단 기준은 <see cref="Branding.IconRing"/> 한 곳뿐.
///
/// 합성 도구는 그대로 System.Drawing(GDI+) — 모듈 색 .ico(A3) 본체를 그린 뒤 링을 얹는다.
///
/// 반환 HICON은 (경로, 크기, 표식 색, 링 색)별로 캐시되어 프로세스 수명 동안 유효하다.
/// WM_SETICON은 핸들을 복사하지 않으므로(WindowIcon.cs와 같은 이유) 호출자는
/// 절대 DestroyIcon 하지 말 것.
/// </summary>
internal static class InstanceIcon
{
    /// <summary>
    /// 인스턴스 색 팔레트 (A2, v0.58.0 — MainWindow에서 이동). 번호 1~9의 기준색.
    /// A102(v0.130.0)부터 유일한 사용처는 타이틀바 원형 번호 배지(A2)다 —
    /// 아이콘 링은 모듈 색으로 넘어갔고 아이콘 번호 배지는 사라졌다.
    /// </summary>
    private static readonly (byte R, byte G, byte B)[] Palette =
    [
        (0xE8, 0x11, 0x23), // 1 red
        (0x00, 0x78, 0xD7), // 2 blue
        (0x10, 0x7C, 0x10), // 3 green
        (0xF7, 0x63, 0x0C), // 4 orange
        (0x8E, 0x24, 0xAA), // 5 purple
        (0x00, 0x99, 0xBC), // 6 teal
        (0xC3, 0x00, 0x52), // 7 magenta
        (0x76, 0x76, 0x76), // 8 gray
        (0x4A, 0x37, 0x8C), // 9 indigo
    ];

    /// <summary>번호의 인스턴스 색(XAML용). 10번째부터 1번 색부터 순환(부록 B 32번).</summary>
    public static Windows.UI.Color ColorFor(int number)
    {
        var (r, g, b) = Rgb(number);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private static (byte R, byte G, byte B) Rgb(int number)
        => Palette[(Math.Max(1, number) - 1) % Palette.Length];

    /// <summary>
    /// 합성 결과 캐시 — "경로|크기|표식 색|링 색" → HICON(프로세스 수명, 파괴 금지).
    /// A102(v0.130.0): 키에서 번호·배지 여부가 빠지고 <b>링 색</b>이 들어왔다 —
    /// 색을 정하는 원천이 인스턴스 번호에서 모듈로 바뀌었으므로, 같은 경로·크기라도
    /// 모듈이 다르면(=링 색이 다르면) 다른 항목이 된다. 옛 형식의 키와는 구성 자체가 달라
    /// 스테일 재사용도 성립하지 않는다(캐시는 프로세스 수명뿐이라 디스크 잔재도 없다).
    /// </summary>
    private static readonly Dictionary<string, IntPtr> s_cache = new();

    /// <summary>
    /// 모듈 색 .ico 위에 <paramref name="ring"/> 색 테두리 링을 얹은 HICON을 돌려준다.
    /// 실패(파일 없음·GDI 오류)하면 IntPtr.Zero — 호출자는 무테두리 아이콘으로 폴백할 것.
    /// UI 스레드 전용(캐시가 잠금 없음 — 호출 경로가 전부 UI 스레드라 충분).
    /// </summary>
    /// <param name="accent">
    /// 아이콘의 모듈 색(중립 아이콘이면 null). A79의 브랜드 표식이 켜져 있을 때
    /// 바탕을 어떻게 그릴지 정하는 데만 쓴다 — 레벨 0에서는 아무 영향이 없다.
    /// </param>
    /// <param name="ring">
    /// 테두리 링 색 (A102) — 호출자가 <see cref="Branding.IconRing"/>으로 정한다.
    /// 링이 없는 화면은 이 메서드를 부르지 않는다.
    /// </param>
    public static IntPtr GetComposed(string icoPath, int size,
        Windows.UI.Color? accent, Windows.UI.Color ring)
    {
        if (size < 8 || !File.Exists(icoPath)) return IntPtr.Zero;

        // 링 색은 ToString()에 기대지 않고 ARGB를 직접 적는다 — 색이 키에 확실히 반영돼야
        // 모듈이 바뀌었는데 옛 합성본이 재사용되는 일이 없다(A102 최대 함정).
        var key = $"{icoPath}|{size}|{accent?.ToString() ?? "neutral"}"
                  + $"|ring:{ring.A:X2}{ring.R:X2}{ring.G:X2}{ring.B:X2}";
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        IntPtr icon;
        try
        {
            icon = Compose(icoPath, size, accent, ring);
        }
        catch
        {
            return IntPtr.Zero; // GDI 리소스 고갈 등 일시 실패 — 다음 갱신 때 다시 시도
        }
        s_cache[key] = icon;
        return icon;
    }

    private static IntPtr Compose(string icoPath, int size,
        Windows.UI.Color? accent, Windows.UI.Color ring)
    {
        using var bitmap = new System.Drawing.Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // ClearType은 배경 없는 32bpp에 알파를 망가뜨린다 — 회색조 AA (SensorTray와 동일)
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // ① 본체: 모듈 색 아이콘 그대로 (A3 유지 — .ico는 16/24/32… 프레임을 다 가짐).
            //    A79(v0.119.0): 브랜드 레벨이 켜져 있으면 여기서 발바닥 표식까지 함께 그려진다.
            BrandIcons.DrawBase(g, icoPath, size, accent);

            var color = GdiColor.FromArgb(ring.R, ring.G, ring.B);

            // ② 테두리 링: 아이콘 본체(라운드 사각, gen_app_icon.py 반경 56/256)의
            //    가장자리를 따라 모듈 색 라운드 사각 스트로크 (A102 — 구 인스턴스 9색 순환 대체)
            var thickness = Math.Max(1.5f, size / 8f);
            var radius = Math.Max(2f, size * 56f / 256f);
            using (var pen = new System.Drawing.Pen(color, thickness))
            using (var ringPath = RoundedRectPath(
                thickness / 2f, thickness / 2f, size - thickness, size - thickness, radius))
            {
                g.DrawPath(pen, ringPath);
            }
            // ③ 우하단 원형 번호 배지는 A102(v0.130.0)에서 렌더 코드째 제거했다 —
            //    번호 표시는 창 제목의 접두 숫자(A103) 한 곳으로 모았고, 배지는 A3의
            //    kotu 서브마크와 트레이 값 텍스트를 동시에 덮고 있었다.
        }
        return bitmap.GetHicon();
    }

    /// <summary>라운드 사각 외곽선 경로(float 좌표 — 펜 두께 절반 안쪽으로 그릴 때 쓴다).</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(
        float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2f);
        var d = r * 2f;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
