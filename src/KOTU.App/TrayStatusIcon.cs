using System.Runtime.InteropServices;
using KOTU.Core.Contracts;
using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 창별 트레이 아이콘의 내용 합성 (A54, v0.118.0) — 모듈이 내준 <see cref="TrayStatus"/>를
/// 16px 아이콘에 그린다. 도구·관용구는 A18 <see cref="SensorTray"/>의 2값 세로 표기 그대로
/// (System.Drawing/GDI+ → <c>GetHicon</c>), 색만 모듈 액센트(<see cref="Branding.ModuleAccent"/>)를 쓴다.
///
/// 표시 규칙(사용자 확정):
///  · 유휴 = 1줄 중앙·저채도(모듈 3자 표기) / 열림 = 2줄·모듈 색.
///  · 인스턴스가 2개 이상이면 A68 인스턴스 색 <b>테두리만</b> 덧그린다 —
///    A68의 우하단 원형 번호 배지는 2줄 텍스트를 덮어 판독을 막으므로 트레이에서는 제거했다
///    (창 아이콘 쪽 배지는 그대로 유지 — 거긴 텍스트가 없다. 번호는 제목 "[n]"(A56)이 알려 준다).
///
/// 반환 HICON은 <b>호출자 소유</b>다 — 교체 후 반드시 DestroyIcon 할 것
/// (창이 많을수록 곱해지는 GDI 핸들이라 A68의 프로세스 수명 캐시와 달리 즉시 회수한다).
/// UI 스레드 전용.
/// </summary>
internal static class TrayStatusIcon
{
    private const int SmCxSmIcon = 49;

    /// <summary>액센트 색이 없는 화면(설정·미지원 파일)에서 쓰는 중립 글자색.</summary>
    private static readonly GdiColor Neutral = GdiColor.FromArgb(0xD0, 0xD4, 0xDA);

    /// <summary>
    /// 재합성 판단용 키(A18 ComposeKey 방식) — 같으면 GDI 작업을 통째로 건너뛴다.
    /// 아이콘 모양을 바꾸는 입력(내용·모듈 색·인스턴스 번호)을 전부 포함해야 한다.
    /// </summary>
    public static string ComposeKey(TrayStatus? status, string? moduleId, int instanceNumber)
    {
        if (status is null) return $"ico|{moduleId}|{instanceNumber}";
        var bars = status.Line2Bars is { } list
            ? string.Join(',', list.Select(v => Math.Round(v, 2).ToString("0.00")))
            : string.Empty;
        return $"{moduleId}|{instanceNumber}|{status.Line1}|{status.Line2}|{bars}";
    }

    /// <summary>
    /// 상태를 그린 HICON을 만든다(실패하면 IntPtr.Zero — 호출자는 아이콘을 그대로 두면 된다).
    /// </summary>
    public static IntPtr Compose(TrayStatus status, Windows.UI.Color? accent, int instanceNumber)
    {
        try
        {
            return Render(status, accent, instanceNumber);
        }
        catch
        {
            return IntPtr.Zero; // GDI 리소스 고갈 등 일시 실패 — 다음 값 변화 때 다시 그린다
        }
    }

    private static IntPtr Render(TrayStatus status, Windows.UI.Color? accent, int instanceNumber)
    {
        var size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        var baseColor = accent is { } c ? GdiColor.FromArgb(c.R, c.G, c.B) : Neutral;
        var color = status.IsIdle ? IdleColor(baseColor) : Lighten(baseColor, 0.30);

        using var bitmap = new System.Drawing.Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // ClearType은 배경 없는 32bpp에 알파를 망가뜨린다 — 회색조 AA (SensorTray·InstanceIcon과 동일)
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // ① 배경: 밝은 작업표시줄에서도 대비가 나오는 반투명 다크 배지(A18과 같은 값)
            using (var path = RoundedRect(size, size, Math.Max(2, size * 3 / 16)))
            using (var background = new System.Drawing.SolidBrush(GdiColor.FromArgb(0xE0, 0x20, 0x20, 0x24)))
                g.FillPath(background, path);

            // ② 내용: 유휴 1줄(중앙) / 열림 2줄(위 텍스트 + 아래 텍스트 또는 막대)
            var margin = instanceNumber > 0 ? 1f : 0f; // 테두리와 글자가 붙지 않게 좌우만 살짝 비운다
            var textWidth = size - margin * 2;
            if (status.IsIdle)
            {
                DrawTextLine(g, status.Line1, color, margin, 0, textWidth, size, size * 0.58f);
            }
            else
            {
                var lineHeight = size / 2f;
                DrawTextLine(g, status.Line1, color, margin, 0, textWidth, lineHeight, lineHeight * 0.94f);
                if (status.Line2Bars is { Count: > 0 } bars)
                    DrawBars(g, bars, color, margin, lineHeight, textWidth, lineHeight);
                else
                    DrawTextLine(g, status.Line2 ?? TrayStatus.Unknown, color,
                        margin, lineHeight, textWidth, lineHeight, lineHeight * 0.94f);
            }

            // ③ 인스턴스 색 테두리(A68 — 번호 배지는 A54에서 제거, 테두리만 유지)
            if (instanceNumber > 0)
            {
                var instance = InstanceIcon.ColorFor(instanceNumber);
                var thickness = Math.Max(1.5f, size / 8f);
                using var pen = new System.Drawing.Pen(
                    GdiColor.FromArgb(instance.R, instance.G, instance.B), thickness);
                using var ring = RoundedRectPath(thickness / 2f, thickness / 2f,
                    size - thickness, size - thickness, Math.Max(2f, size * 56f / 256f));
                g.DrawPath(pen, ring);
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

    /// <summary>어두운 배지 위에서 읽히도록 밝힌다(A18 SensorTray.Lighten과 같은 계산).</summary>
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
