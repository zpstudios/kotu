using System.Runtime.InteropServices;
using KOTU.Core.Contracts;
using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 창별 트레이 아이콘의 내용 합성 (A54, v0.118.0) — 모듈이 내준 <see cref="TrayStatus"/>를
/// 16px 아이콘에 그린다. 도구·관용구는 구 A18 SensorTray(A101에서 폐지)의 2값 세로 표기 그대로
/// (System.Drawing/GDI+ → <c>GetHicon</c>), 색만 모듈 액센트(<see cref="Branding.ModuleAccent"/>)를 쓴다.
///
/// 표시 규칙(사용자 확정):
///  · 유휴 = 1줄 중앙·저채도(모듈 3자 표기) / 열림 = 2줄·모듈 색.
///  · A102(v0.130.0): 테두리는 <b>모듈 색</b>이 되고 창 개수 조건이 사라졌다(구: 인스턴스 9색·
///    2개 이상일 때만). 링 유무 판단은 <see cref="Branding.IconRing"/> 한 곳 —
///    값 2줄을 채우는 정보(H/W) 모듈과 중립 화면은 링이 없다.
///    번호 표시는 창 제목의 접두 숫자(A103)가 전담한다.
///
/// 반환 HICON은 <b>호출자 소유</b>다 — 교체 후 반드시 DestroyIcon 할 것
/// (창이 많을수록 곱해지는 GDI 핸들이라 InstanceIcon의 프로세스 수명 캐시와 달리 즉시 회수한다).
/// UI 스레드 전용.
/// </summary>
internal static class TrayStatusIcon
{
    private const int SmCxSmIcon = 49;

    /// <summary>
    /// 글자 크기 배수 (A102) — 값·3자 표기가 커서 테두리 링에 물리는 것을 줄인다.
    /// 1줄(유휴)·2줄(열림) 공통이며, 폭 초과 시 줄이는 루프의 하한(5px)은 건드리지 않는다.
    /// <b>실기기에서 눈으로 보고 미세 조정하는 단일 지점</b>이다.
    /// </summary>
    private const float FontScale = 0.85f;

    /// <summary>액센트 색이 없는 화면(설정·미지원 파일)에서 쓰는 중립 글자색.</summary>
    private static readonly GdiColor Neutral = GdiColor.FromArgb(0xD0, 0xD4, 0xDA);

    /// <summary>
    /// 재합성 판단용 키(A18 ComposeKey 방식) — 같으면 GDI 작업을 통째로 건너뛴다.
    /// 아이콘 모양을 바꾸는 입력(내용·모듈)을 전부 포함해야 한다. A102(v0.130.0)에서
    /// 인스턴스 번호가 빠졌다 — 번호는 더 이상 아이콘 모양에 관여하지 않고,
    /// 링 색·유무는 모듈 ID에서 나오므로 moduleId가 그 변화를 이미 대표한다.
    /// </summary>
    public static string ComposeKey(TrayStatus? status, string? moduleId)
    {
        if (status is null) return $"ico|{moduleId}";
        var bars = status.Line2Bars is { } list
            ? string.Join(',', list.Select(v => Math.Round(v, 2).ToString("0.00")))
            : string.Empty;
        return $"{moduleId}|{status.Line1}|{status.Line2}|{bars}";
    }

    /// <summary>
    /// 상태를 그린 HICON을 만든다(실패하면 IntPtr.Zero — 호출자는 아이콘을 그대로 두면 된다).
    /// </summary>
    /// <param name="ring">테두리 링 색(A102) — null이면 링 없음.</param>
    public static IntPtr Compose(TrayStatus status, Windows.UI.Color? accent, Windows.UI.Color? ring)
    {
        try
        {
            return Render(status, accent, ring);
        }
        catch
        {
            return IntPtr.Zero; // GDI 리소스 고갈 등 일시 실패 — 다음 값 변화 때 다시 그린다
        }
    }

    private static IntPtr Render(TrayStatus status, Windows.UI.Color? accent, Windows.UI.Color? ring)
    {
        var size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        var baseColor = accent is { } c ? GdiColor.FromArgb(c.R, c.G, c.B) : Neutral;
        var color = status.IsIdle ? IdleColor(baseColor) : Lighten(baseColor, 0.30);

        using var bitmap = new System.Drawing.Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // ClearType은 배경 없는 32bpp에 알파를 망가뜨린다 — 회색조 AA (InstanceIcon과 동일, 구 SensorTray에서 온 관용구)
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // ① 배경: 밝은 작업표시줄에서도 대비가 나오는 반투명 다크 배지(A18과 같은 값)
            using (var path = RoundedRect(size, size, Math.Max(2, size * 3 / 16)))
            using (var background = new System.Drawing.SolidBrush(GdiColor.FromArgb(0xE0, 0x20, 0x20, 0x24)))
                g.FillPath(background, path);

            // ② 내용: 유휴 1줄(중앙) / 열림 2줄(위 텍스트 + 아래 텍스트 또는 막대)
            var margin = ring is null ? 0f : 1f; // 테두리와 글자가 붙지 않게 좌우만 살짝 비운다
            var textWidth = size - margin * 2;
            if (status.IsIdle)
            {
                DrawTextLine(g, status.Line1, color, margin, 0, textWidth, size, size * 0.58f * FontScale);
            }
            else
            {
                var lineHeight = size / 2f;
                DrawTextLine(g, status.Line1, color, margin, 0, textWidth, lineHeight,
                    lineHeight * 0.94f * FontScale);
                if (status.Line2Bars is { Count: > 0 } bars)
                    DrawBars(g, bars, color, margin, lineHeight, textWidth, lineHeight);
                else
                    DrawTextLine(g, status.Line2 ?? TrayStatus.Unknown, color,
                        margin, lineHeight, textWidth, lineHeight, lineHeight * 0.94f * FontScale);
            }

            // ③ 모듈 색 테두리(A102 — 구 인스턴스 색·창 2개 이상 조건 대체).
            //    링 유무는 호출자가 Branding.IconRing으로 이미 판단해 넘긴다.
            if (ring is { } ringColor)
            {
                var thickness = Math.Max(1.5f, size / 8f);
                using var pen = new System.Drawing.Pen(
                    GdiColor.FromArgb(ringColor.R, ringColor.G, ringColor.B), thickness);
                using var ringPath = RoundedRectPath(thickness / 2f, thickness / 2f,
                    size - thickness, size - thickness, Math.Max(2f, size * 56f / 256f));
                g.DrawPath(pen, ringPath);
            }
        }
        return bitmap.GetHicon();
    }

    /// <summary>
    /// 한 줄을 폭에 맞춰 그린다. 폭을 넘치면 A18과 같이 글꼴을 줄이고(5px 하한),
    /// 그래도 안 들어가면 그때만 3자로 자른다(사용자 확정: 확장자 4자 → 3자).
    /// </summary>
    private static void DrawTextLine(System.Drawing.Graphics g, string text, GdiColor color,
        float x, float y, float width, float height, float fontPx)
    {
        using var format = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic)
        {
            Alignment = System.Drawing.StringAlignment.Center,
            LineAlignment = System.Drawing.StringAlignment.Center,
            FormatFlags = System.Drawing.StringFormatFlags.NoWrap,
        };

        var font = MakeFont(fontPx);
        while (fontPx > 5f && g.MeasureString(text, font, int.MaxValue, format).Width > width)
        {
            font.Dispose();
            fontPx -= 0.5f;
            font = MakeFont(fontPx);
        }
        if (text.Length > 3 && g.MeasureString(text, font, int.MaxValue, format).Width > width)
            text = text[..3];

        using (font)
        using (var brush = new System.Drawing.SolidBrush(color))
        {
            g.DrawString(text, font, brush, new System.Drawing.RectangleF(x, y, width, height), format);
        }
    }

    /// <summary>
    /// 아래 줄 막대 시각화(오디오 이퀄라이저 장식). 값은 0~1 높이 비율 —
    /// 실제 주파수 분석이 아니라 재생 중임을 알리는 의사 패턴이다(A54 구현 시 결정).
    /// </summary>
    private static void DrawBars(System.Drawing.Graphics g, IReadOnlyList<double> bars, GdiColor color,
        float x, float y, float width, float height)
    {
        var slot = width / bars.Count;
        var barWidth = Math.Max(1f, slot * 0.6f);
        var top = y + 1f;
        var maxHeight = Math.Max(1f, height - 2f);
        using var brush = new System.Drawing.SolidBrush(color);
        for (var i = 0; i < bars.Count; i++)
        {
            var level = (float)Math.Clamp(bars[i], 0, 1);
            var barHeight = Math.Max(1f, maxHeight * level);
            g.FillRectangle(brush,
                x + slot * i + (slot - barWidth) / 2f,
                top + (maxHeight - barHeight),
                barWidth, barHeight);
        }
    }

    private static System.Drawing.Font MakeFont(float px)
        => new("Segoe UI", px, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);

    /// <summary>어두운 배지 위에서 읽히도록 밝힌다(구 A18 SensorTray.Lighten에서 온 계산).</summary>
    private static GdiColor Lighten(GdiColor c, double amount) => GdiColor.FromArgb(
        c.R + (int)((255 - c.R) * amount),
        c.G + (int)((255 - c.G) * amount),
        c.B + (int)((255 - c.B) * amount));

    /// <summary>유휴 색 = 채도를 1/4로 낮춘 뒤 밝히기 — "열림"의 선명한 모듈 색과 한눈에 갈린다.</summary>
    private static GdiColor IdleColor(GdiColor c)
    {
        var gray = (int)(c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
        var muted = GdiColor.FromArgb((c.R + gray * 3) / 4, (c.G + gray * 3) / 4, (c.B + gray * 3) / 4);
        return Lighten(muted, 0.55);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(int w, int h, int r)
        => RoundedRectPath(0, 0, w, h, r);

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

    [DllImport("user32")]
    private static extern int GetSystemMetrics(int nIndex);
}
