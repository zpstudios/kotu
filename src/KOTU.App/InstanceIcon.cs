using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 인스턴스 색상 코딩 (A68) — 팔레트와 아이콘 런타임 합성.
///
/// 팔레트는 A2 타이틀바 배지의 9색 그대로이며, 10번째 인스턴스부터 1번 색부터
/// 순환한다(사용자 확정, 요구사항 부록 B 32번). 제목의 "[n]"(A56)이 실제 번호를
/// 계속 표시하므로 색이 겹쳐도 창 구분은 유지된다.
///
/// 합성(GetComposed): 기존 모듈 색 .ico(A3 — 본체는 그대로)를 그린 뒤
/// ① 인스턴스 색 라운드 사각 테두리 링(아이콘 가장자리),
/// ② 우하단 원형 번호 배지(타이틀바 배지와 같은 ①②③ 형태 — 인스턴스 색 원 + 흰 숫자)
/// 를 System.Drawing(GDI+)으로 얹는다 — A18 센서 트레이 아이콘(SensorTray)과 같은 도구.
/// 창이 하나뿐이면(번호 0) 합성 자체를 부르지 않는다 — 배지·번호 숨김 규칙과 일관.
/// ※ A54(v0.118.0): ②번 배지는 <b>창 아이콘 전용</b>이 됐다. 트레이 아이콘은 값 텍스트를
/// 2줄로 그리게 되어(TrayStatusIcon) 배지가 글자를 덮으므로 withBadge=false로 부른다.
///
/// 반환 HICON은 (경로, 번호, 크기)별로 캐시되어 프로세스 수명 동안 유효하다.
/// WM_SETICON은 핸들을 복사하지 않으므로(WindowIcon.cs와 같은 이유) 호출자는
/// 절대 DestroyIcon 하지 말 것. 창이 닫혔다 다시 그 번호가 되면 캐시가 재사용된다.
/// </summary>
internal static class InstanceIcon
{
    /// <summary>
    /// 인스턴스 색 팔레트 (A2, v0.58.0 — MainWindow에서 이동). 번호 1~9의 기준색이며
    /// A68에서 타이틀바 배지·아이콘 테두리·트레이 아이콘이 모두 이 표를 공유한다.
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

    /// <summary>합성 결과 캐시 — "경로|번호|크기" → HICON(프로세스 수명, 파괴 금지).</summary>
    private static readonly Dictionary<string, IntPtr> s_cache = new();

    /// <summary>
    /// 모듈 색 .ico 위에 인스턴스 테두리 링(+ 선택적으로 원형 번호 배지)을 얹은 HICON을 돌려준다.
    /// 실패(파일 없음·GDI 오류)하면 IntPtr.Zero — 호출자는 무테두리 아이콘으로 폴백할 것.
    /// UI 스레드 전용(캐시가 잠금 없음 — 호출 경로가 전부 UI 스레드라 충분).
    /// </summary>
    /// <param name="withBadge">
    /// 우하단 원형 번호 배지 표시 여부. 창 아이콘은 true(A68), 트레이 아이콘은 false(A54 — 값 텍스트가 있다).
    /// </param>
    public static IntPtr GetComposed(string icoPath, int number, int size, bool withBadge = true)
    {
        if (number <= 0 || size < 8 || !File.Exists(icoPath)) return IntPtr.Zero;

        var key = $"{icoPath}|{number}|{size}|{withBadge}";
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        IntPtr icon;
        try
        {
            icon = Compose(icoPath, number, size, withBadge);
        }
        catch
        {
            return IntPtr.Zero; // GDI 리소스 고갈 등 일시 실패 — 다음 갱신 때 다시 시도
        }
        s_cache[key] = icon;
        return icon;
    }

    private static IntPtr Compose(string icoPath, int number, int size, bool withBadge)
    {
        using var bitmap = new System.Drawing.Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // ClearType은 배경 없는 32bpp에 알파를 망가뜨린다 — 회색조 AA (SensorTray와 동일)
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // ① 본체: 모듈 색 아이콘 그대로 (A3 유지 — .ico는 16/24/32… 프레임을 다 가짐)
            using (var baseIcon = new System.Drawing.Icon(icoPath, size, size))
                g.DrawIcon(baseIcon, new System.Drawing.Rectangle(0, 0, size, size));

            var (r, gr, b) = Rgb(number);
            var color = GdiColor.FromArgb(r, gr, b);

            // ② 테두리 링: 아이콘 본체(라운드 사각, gen_app_icon.py 반경 56/256)의
            //    가장자리를 따라 인스턴스 색 라운드 사각 스트로크
            var thickness = Math.Max(1.5f, size / 8f);
            var radius = Math.Max(2f, size * 56f / 256f);
            using (var pen = new System.Drawing.Pen(color, thickness))
            using (var ring = RoundedRectPath(
                thickness / 2f, thickness / 2f, size - thickness, size - thickness, radius))
            {
                g.DrawPath(pen, ring);
            }

            // ③ 원형 번호 배지(우하단): 타이틀바 배지와 같은 형태 — 색 원 + 흰 굵은 숫자.
            //    흰 외곽선으로 링·본체와 분리. A3의 kotu 서브마크 위를 덮지만
            //    다중 창일 때 번호 구분이 우선(사용자 사양 — ①②③ 통일).
            //    A54(v0.118.0): 트레이 아이콘에서는 생략한다(withBadge=false) — 값 2줄을 덮기 때문.
            if (withBadge)
            {
                var d = Math.Max(9, size * 5 / 8);
                float bx = size - d, by = size - d;
                using (var fill = new System.Drawing.SolidBrush(color))
                    g.FillEllipse(fill, bx, by, d - 1f, d - 1f);
                using (var outline = new System.Drawing.Pen(GdiColor.White, Math.Max(1f, size / 24f)))
                    g.DrawEllipse(outline, bx, by, d - 1f, d - 1f);

                var text = number.ToString();
                using var format = new System.Drawing.StringFormat(
                    System.Drawing.StringFormat.GenericTypographic)
                {
                    Alignment = System.Drawing.StringAlignment.Center,
                    LineAlignment = System.Drawing.StringAlignment.Center,
                    FormatFlags = System.Drawing.StringFormatFlags.NoWrap,
                };
                // 폭 초과 시 축소 — 10 이상(두 자리, 색 순환 구간)에서도 원 안에 들어가게
                var fontPx = d * 0.72f;
                var font = MakeFont(fontPx);
                while (fontPx > 4f
                       && g.MeasureString(text, font, int.MaxValue, format).Width > d - 2)
                {
                    font.Dispose();
                    fontPx -= 0.5f;
                    font = MakeFont(fontPx);
                }
                using (font)
                using (var brush = new System.Drawing.SolidBrush(GdiColor.White))
                {
                    g.DrawString(text, font, brush,
                        new System.Drawing.RectangleF(bx, by, d - 1f, d - 1f), format);
                }
            }
        }
        return bitmap.GetHicon();
    }

    private static System.Drawing.Font MakeFont(float px)
        => new("Segoe UI", px, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);

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
