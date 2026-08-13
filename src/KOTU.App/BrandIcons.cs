using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 아이콘에 얹는 브랜드 표식 ①② (A79, v0.119.0) — 런타임 GDI+ 합성.
///
/// 창·작업표시줄·트레이가 쓰는 .ico는 커밋된 파일(packaging/gen_app_icon.py 생성)이라
/// 레벨 값이 파일 내용을 바꾸지 못한다. 그래서 원본 .ico를 그린 뒤 그 위에
/// 발바닥을 덧그린다 — A3(모듈 색 배경·흰 전경)·A68(인스턴스 테두리)은 그대로 유지된다.
///
///  · 중립 아이콘(accent 없음) = ① "KO/TU" 2줄 자리를 브랜드 색으로 덮고 흰 발바닥 하나.
///  · 모듈 색 아이콘(accent 있음) = ② 우하단 <c>kotu</c> 글자 자리를 모듈 색으로 덮고 작은 흰 발바닥.
///
/// 덮는 사각형·라운드 사각 반경은 생성 스크립트의 값(256 기준 inset 8 · radius 56 ·
/// 흰 테두리 alpha 70 두께 4)을 그대로 따른다 — 스크립트를 고치면 여기도 고칠 것.
///
/// 트레이의 <b>값 텍스트 아이콘(A54, TrayStatusIcon)에는 손대지 않는다</b> —
/// ①은 표시할 값이 없는 화면(설정·빈 셸)의 중립 아이콘 폴백에만 닿는다.
///
/// 반환 HICON은 (경로, 크기, 색)별로 캐시되어 프로세스 수명 동안 유효하다 —
/// InstanceIcon과 같은 규칙이니 호출자는 절대 DestroyIcon 하지 말 것. UI 스레드 전용.
/// </summary>
internal static class BrandIcons
{
    /// <summary>중립 아이콘 배경색 — 타이틀바와 같은 브랜드 색(단일 소스).</summary>
    private static GdiColor NeutralBackground => GdiColor.FromArgb(
        TitleBarTheming.Background.R, TitleBarTheming.Background.G, TitleBarTheming.Background.B);

    private static readonly Dictionary<string, IntPtr> s_cache = new();

    /// <summary>
    /// 이 아이콘에 얹을 브랜드 표식이 켜져 있는가.
    /// accent가 null이면 중립 아이콘(①), 있으면 모듈 색 아이콘(②)이다.
    /// </summary>
    public static bool AppliesTo(Windows.UI.Color? accent)
        => BrandAssets.IsEnabled(accent is null ? BrandPoint.NeutralPaw : BrandPoint.ModulePawMark);

    /// <summary>
    /// 브랜드 표식을 얹은 HICON. 표식이 꺼져 있거나 합성에 실패하면 <see cref="IntPtr.Zero"/> —
    /// 호출자는 지금까지처럼 원본 .ico를 그대로 쓰면 된다(레벨 0의 모습).
    /// </summary>
    public static IntPtr GetBranded(string icoPath, int size, Windows.UI.Color? accent)
    {
        if (!AppliesTo(accent) || size < 8 || !File.Exists(icoPath)) return IntPtr.Zero;

        var key = $"{icoPath}|{size}|{accent?.ToString() ?? "neutral"}";
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        IntPtr icon;
        try
        {
            using var bitmap = new System.Drawing.Bitmap(size, size,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                DrawBase(g, icoPath, size, accent);
            }
            icon = bitmap.GetHicon();
        }
        catch
        {
            return IntPtr.Zero; // 아이콘 파일 손상·GDI 고갈 — 장식 없이 원본으로 간다
        }
        s_cache[key] = icon;
        return icon;
    }

    /// <summary>
    /// 아이콘 본체를 그리고(A3 — .ico는 16/24/32… 프레임을 다 가진다) 켜져 있는 브랜드 표식을 얹는다.
    /// 인스턴스 테두리·번호 배지를 더 얹는 경로(<see cref="InstanceIcon"/>)도 이것으로 바탕을 그린다 —
    /// 표식이 꺼져 있으면 원본을 그대로 그리는 것과 같다.
    /// </summary>
    public static void DrawBase(System.Drawing.Graphics g, string icoPath, int size,
        Windows.UI.Color? accent)
    {
        using (var baseIcon = new System.Drawing.Icon(icoPath, size, size))
            g.DrawIcon(baseIcon, new System.Drawing.Rectangle(0, 0, size, size));

        if (!AppliesTo(accent)) return;

        if (accent is { } c)
            DrawModulePaw(g, size, GdiColor.FromArgb(c.R, c.G, c.B));
        else
            DrawNeutralPaw(g, size);
    }

    /// <summary>① 중립 아이콘: 라운드 사각을 브랜드 색으로 다시 칠해 글자를 지우고 흰 발바닥.</summary>
    private static void DrawNeutralPaw(System.Drawing.Graphics g, int size)
    {
        using (var body = Body(size))
        using (var fill = new System.Drawing.SolidBrush(NeutralBackground))
            g.FillPath(fill, body);
        StrokeEdge(g, size);

        var side = size * 0.66f;
        BrandPaw.Draw(g, new System.Drawing.RectangleF(
            (size - side) / 2f, size * 0.48f - side / 2f, side, side), GdiColor.White);
    }

    /// <summary>② 모듈 색 아이콘: 우하단 <c>kotu</c> 자리만 모듈 색으로 덮고 작은 흰 발바닥.</summary>
    private static void DrawModulePaw(System.Drawing.Graphics g, int size, GdiColor accent)
    {
        // 메인 글씨(중앙, 아래쪽 끝이 대략 0.64)는 건드리지 않도록 0.72 아래만 덮는다.
        using (var body = Body(size))
        {
            g.SetClip(body); // 라운드 사각 밖으로 색이 삐져나오지 않게
            using (var fill = new System.Drawing.SolidBrush(accent))
                g.FillRectangle(fill, size * 0.58f, size * 0.72f, size * 0.42f, size * 0.28f);
            g.ResetClip();
        }
        StrokeEdge(g, size);

        BrandPaw.Draw(g, new System.Drawing.RectangleF(
                size * 0.645f, size * 0.66f, size * 0.24f, size * 0.24f),
            GdiColor.FromArgb(235, 255, 255, 255)); // 생성 스크립트의 kotu 표식과 같은 밝기
    }

    /// <summary>아이콘 본체(라운드 사각) 경로 — 생성 스크립트의 inset 8/256 · radius 56/256.</summary>
    private static System.Drawing.Drawing2D.GraphicsPath Body(int size)
    {
        var inset = size * 8f / 256f;
        return RoundedRectPath(inset, inset, size - inset * 2f, size - inset * 2f, size * 56f / 256f);
    }

    /// <summary>생성 스크립트가 그리는 흰 반투명 테두리(alpha 70, 두께 4/256)를 다시 얹는다.</summary>
    private static void StrokeEdge(System.Drawing.Graphics g, int size)
    {
        using var body = Body(size);
        using var pen = new System.Drawing.Pen(
            GdiColor.FromArgb(70, 255, 255, 255), Math.Max(1f, size * 4f / 256f));
        g.DrawPath(pen, body);
    }

    /// <summary>라운드 사각 경로 — InstanceIcon·TrayStatusIcon의 같은 이름 헬퍼와 같은 계산.</summary>
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
