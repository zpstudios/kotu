using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 아이콘 런타임 합성 (A68 시작 → A102/v0.130.0에서 의미 개편) + 인스턴스 색 팔레트.
///
/// A102 전에는 이 합성이 "몇 번째 창인가"를 알리는 장치였다(인스턴스 9색 링 + 우하단 원형
/// 번호 배지). 지금은 <b>어느 모듈의 창인가</b>를 알리는 장치다:
/// ① 테두리 링 색 = 그 창 모듈의 액센트 색(<see cref="Branding.IconRing"/>),
/// ② 원형 번호 배지는 렌더 코드째 제거 — 번호는 창 제목의 접두 숫자(A103)가 담당한다.
/// ③ A105 ②(v0.143.0): 창(태스크바) 32px 아이콘 하단에 모듈 3자 표기를 얹을 수 있다(label).
/// 링은 창 개수와 무관하게 항상 그린다(모듈 식별이 목적이라 "2개 이상일 때만" 조건이 사라졌다).
/// A105부터 링 없는 호출도 허용된다 — 정보(H/W) 모듈이 링 없이 3자 표기(INF)만 얹는 경우.
/// 링도 글자도 없는 화면(설정·빈 셸 = 중립 아이콘)만 이 클래스를 부르지 않고
/// <see cref="BrandIcons.GetBranded"/>로 간다.
///
/// 합성 도구는 그대로 System.Drawing(GDI+) — 모듈 색 .ico(A3) 본체를 그린 뒤 링·글자를 얹는다.
///
/// 반환 HICON은 (경로, 크기, 표식 색, 링 색, 3자 표기)별로 캐시되어 프로세스 수명 동안 유효하다.
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
    /// 합성 결과 캐시 — "경로|크기|표식 색|링 색|3자" → HICON(프로세스 수명, 파괴 금지).
    /// A102(v0.130.0): 키에서 번호·배지 여부가 빠지고 <b>링 색</b>이 들어왔다 —
    /// 색을 정하는 원천이 인스턴스 번호에서 모듈로 바뀌었으므로, 같은 경로·크기라도
    /// 모듈이 다르면(=링 색이 다르면) 다른 항목이 된다. 옛 형식의 키와는 구성 자체가 달라
    /// 스테일 재사용도 성립하지 않는다(캐시는 프로세스 수명뿐이라 디스크 잔재도 없다).
    /// A105(v0.143.0): <b>3자 표기</b>도 키에 명시로 들어간다 — 아래 GetComposed 주석 참고.
    /// 키 조성이 전부 모듈 축(경로×크기×액센트×링×3자)이라 항목 수는 유계다 —
    /// 인스턴스(창) 수와 무관(A104 상한 점검에서 확인한 성질을 유지할 것).
    /// </summary>
    private static readonly Dictionary<string, IntPtr> s_cache = new();

    /// <summary>
    /// 모듈 색 .ico 위에 <paramref name="ring"/> 색 테두리 링과(A102)
    /// 하단 모듈 3자 표기(<paramref name="label"/>, A105 ②)를 얹은 HICON을 돌려준다.
    /// 실패(파일 없음·GDI 오류)하면 IntPtr.Zero — 호출자는 무테두리 아이콘으로 폴백할 것.
    /// UI 스레드 전용(캐시가 잠금 없음 — 호출 경로가 전부 UI 스레드라 충분).
    /// </summary>
    /// <param name="accent">
    /// 아이콘의 모듈 색(중립 아이콘이면 null). A79의 브랜드 표식 바탕 판단과
    /// 3자 표기 글자색(A105 ② — null이면 중립 글자색)에 쓴다.
    /// </param>
    /// <param name="ring">
    /// 테두리 링 색 (A102) — 호출자가 <see cref="Branding.IconRing"/>으로 정한다.
    /// A105부터 null 허용: 링 없이 3자 표기만 얹는 호출(정보 모듈의 INF)이 생겼다.
    /// </param>
    /// <param name="label">
    /// 하단 모듈 3자 표기 (A105 ②) — null/빈 문자열이면 글자 없음. 출처는 트레이 유휴 표기와
    /// 같은 표(MainWindow.IdleTrayLabel — 단일 출처)여야 한다. 16px에는 호출자가 넣지 않는다.
    /// </param>
    public static IntPtr GetComposed(string icoPath, int size,
        Windows.UI.Color? accent, Windows.UI.Color? ring, string? label)
    {
        if (size < 8 || !File.Exists(icoPath)) return IntPtr.Zero;

        // 링 색은 ToString()에 기대지 않고 ARGB를 직접 적는다 — 색이 키에 확실히 반영돼야
        // 모듈이 바뀌었는데 옛 합성본이 재사용되는 일이 없다(A102 최대 함정).
        // A105: 3자 표기도 명시로 넣는다 — 링 색을 구분 프록시로 쓰면 무링 2종
        // (정보 INF·설정 무글자)이 같은 키가 될 수 있다(모듈 .ico 부재로 중립 폴백하면
        // 경로·액센트까지 같아진다). "없음"도 값으로 적어 null과 실값이 절대 안 섞이게 한다.
        var key = $"{icoPath}|{size}|{accent?.ToString() ?? "neutral"}"
                  + (ring is { } r ? $"|ring:{r.A:X2}{r.R:X2}{r.G:X2}{r.B:X2}" : "|ring:none")
                  + $"|label:{(string.IsNullOrEmpty(label) ? "none" : label)}";
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        IntPtr icon;
        try
        {
            icon = Compose(icoPath, size, accent, ring, label);
        }
        catch
        {
            return IntPtr.Zero; // GDI 리소스 고갈 등 일시 실패 — 다음 갱신 때 다시 시도
        }
        s_cache[key] = icon;
        return icon;
    }

    private static IntPtr Compose(string icoPath, int size,
        Windows.UI.Color? accent, Windows.UI.Color? ring, string? label)
    {
        using var bitmap = new System.Drawing.Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // ClearType은 배경 없는 32bpp에 알파를 망가뜨린다 — 회색조 AA (구 SensorTray에서 온 관용구)
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // ① 본체: 모듈 색 아이콘 그대로 (A3 유지 — .ico는 16/24/32… 프레임을 다 가짐).
            //    A79(v0.119.0): 브랜드 레벨이 켜져 있으면 여기서 발바닥 표식까지 함께 그려진다.
            BrandIcons.DrawBase(g, icoPath, size, accent);

            // 링·글자 공통 기하 — 3자 표기 자리(A105)가 링 상수에서 파생되므로 한 곳에서 계산한다.
            var thickness = Math.Max(1.5f, size / 8f);
            var radius = Math.Max(2f, size * 56f / 256f);

            // ② 테두리 링: 아이콘 본체(라운드 사각, gen_app_icon.py 반경 56/256)의
            //    가장자리를 따라 모듈 색 라운드 사각 스트로크 (A102 — 구 인스턴스 9색 순환 대체).
            //    A105부터 링 없는 호출(정보 모듈의 3자 표기 전용 합성)이 있어 조건부가 됐다.
            if (ring is { } ringColor)
            {
                var color = GdiColor.FromArgb(ringColor.R, ringColor.G, ringColor.B);
                using var pen = new System.Drawing.Pen(color, thickness);
                using var ringPath = RoundedRectPath(
                    thickness / 2f, thickness / 2f, size - thickness, size - thickness, radius);
                g.DrawPath(pen, ringPath);
            }
            // ③ 우하단 원형 번호 배지는 A102(v0.130.0)에서 렌더 코드째 제거했다 —
            //    번호 표시는 창 제목의 접두 숫자(A103) 한 곳으로 모았고, 배지는 A3의
            //    kotu 서브마크와 트레이 값 텍스트를 동시에 덮고 있었다.

            // ④ 하단 모듈 3자 표기 (A105 ②) — 링 안쪽에 안착시킨다.
            if (!string.IsNullOrEmpty(label))
                DrawLabel(g, label, size, accent, ring is not null, thickness, radius);
        }
        return bitmap.GetHicon();
    }

    /// <summary>
    /// 3자 표기 글자 크기 배수 (A105 ②) — 출발값은 TrayStatusIcon.FontScale(A102)과 같은 0.85.
    /// 대상이 달라(창 32px 아이콘) 별도 상수로 두며, <b>실기기에서 눈으로 보고
    /// 미세 조정하는 단일 지점</b>이다(트레이 쪽 상수를 건드리지 않고 창 쪽만 조정 가능).
    /// </summary>
    private const float LabelFontScale = 0.85f;

    /// <summary>3자 표기 대비판 — TrayStatusIcon(A54, 구 A18 배지)과 같은 반투명 다크 ARGB.</summary>
    private static readonly GdiColor LabelPlate = GdiColor.FromArgb(0xE0, 0x20, 0x20, 0x24);

    /// <summary>액센트 없는 폴백(모듈 .ico 부재)의 글자색 — TrayStatusIcon.Neutral과 같은 값.</summary>
    private static readonly GdiColor LabelNeutral = GdiColor.FromArgb(0xD0, 0xD4, 0xDA);

    /// <summary>
    /// 모듈 3자 표기(A105 ②)를 아이콘 하단에 1줄로 안착시킨다. 자리는 하드코딩하지 않고
    /// 링 상수에서 파생한다(A102 링이 항상 가장자리를 쓰므로 — 겹침 방지가 이 파생의 목적):
    ///  · 안쪽 여백 = 링이 있으면 링 두께(스트로크가 가장자리 [0..두께] 대역을 차지),
    ///    없으면(정보 모듈) 본체 inset 8/256 — gen_app_icon.py·BrandIcons.Body와 같은 값.
    ///  · 글자 줄 높이 = 안쪽 폭의 절반 — TrayStatusIcon(A54)의 2줄 산정(줄 = 전체 절반)을
    ///    안쪽 영역에 적용한 것. 폰트 = 줄 높이 × 0.94 × 0.85(같은 식·같은 배수) →
    ///    32px 링 기준 약 9.6px로, A54가 16px 트레이에서 실증한 6.4px보다 커 가독이 선다.
    ///  · 대비판: 바탕 .ico가 이미 모듈 색이라 모듈 액센트 글자가 그대로는 안 보인다 —
    ///    TrayStatusIcon과 같은 반투명 다크 배지를 글자 줄에만 깔고, 글자도 같은
    ///    Lighten 0.30 처리로 밝힌다(A54에서 가독이 실증된 "다크 판 + 모듈 색 글자" 조합).
    /// </summary>
    private static void DrawLabel(System.Drawing.Graphics g, string label, int size,
        Windows.UI.Color? accent, bool hasRing, float ringThickness, float ringRadius)
    {
        var inset = hasRing ? ringThickness : size * 8f / 256f;
        var width = size - inset * 2f;
        var bandHeight = width / 2f;
        var top = size - inset - bandHeight;

        // 대비판은 링(또는 본체) 라운드 안쪽으로 클립 — 모서리 곡선 밖으로 판이 새지 않게.
        // 링 안쪽 모서리 반경 = 링 반경에서 스트로크 절반을 뺀 값(스트로크 중심이 링 반경 위치).
        var clipRadius = hasRing ? Math.Max(0f, ringRadius - ringThickness / 2f) : ringRadius;
        using (var clip = RoundedRectPath(inset, inset, width, size - inset * 2f, clipRadius))
        {
            g.SetClip(clip);
            using var plate = new System.Drawing.SolidBrush(LabelPlate);
            g.FillRectangle(plate, inset, top, width, bandHeight);
            g.ResetClip();
        }

        var color = accent is { } c
            ? Lighten(GdiColor.FromArgb(c.R, c.G, c.B), 0.30)
            : LabelNeutral;

        using var format = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic)
        {
            Alignment = System.Drawing.StringAlignment.Center,
            LineAlignment = System.Drawing.StringAlignment.Center,
            FormatFlags = System.Drawing.StringFormatFlags.NoWrap,
        };

        // 폭 초과 시 줄이는 루프(하한 5px)는 TrayStatusIcon.DrawTextLine(A54)과 같은 안전장치 —
        // 표기가 3자 고정이라 정상 경로에서는 돌지 않는다.
        var fontPx = bandHeight * 0.94f * LabelFontScale;
        var font = MakeFont(fontPx);
        while (fontPx > 5f && g.MeasureString(label, font, int.MaxValue, format).Width > width)
        {
            font.Dispose();
            fontPx -= 0.5f;
            font = MakeFont(fontPx);
        }
        using (font)
        using (var brush = new System.Drawing.SolidBrush(color))
        {
            g.DrawString(label, font, brush,
                new System.Drawing.RectangleF(inset, top, width, bandHeight), format);
        }
    }

    /// <summary>Segoe UI Bold 픽셀 단위 — TrayStatusIcon.MakeFont(A54)와 같은 글꼴 선택.</summary>
    private static System.Drawing.Font MakeFont(float px)
        => new("Segoe UI", px, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);

    /// <summary>다크 판 위에서 읽히도록 밝힌다 — TrayStatusIcon.Lighten(A54, 구 A18)과 같은 계산.</summary>
    private static GdiColor Lighten(GdiColor c, double amount) => GdiColor.FromArgb(
        c.R + (int)((255 - c.R) * amount),
        c.G + (int)((255 - c.G) * amount),
        c.B + (int)((255 - c.B) * amount));

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
