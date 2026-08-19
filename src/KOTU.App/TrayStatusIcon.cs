using System.Runtime.InteropServices;
using KOTU.Core.Contracts;
using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 창별 트레이 아이콘의 내용 합성 (A54, v0.118.0) — 모듈이 내준 <see cref="TrayStatus"/>를
/// 16px 아이콘에 그린다. 도구·관용구는 구 A18 SensorTray(A101에서 폐지)의 2값 세로 표기 그대로
/// (System.Drawing/GDI+ → <c>GetHicon</c>), 색은 모듈 액센트(<see cref="Branding.ModuleAccent"/>)가
/// 기본이고 모듈이 줄별 색을 실어 보내면 그 색이 이긴다(A169 — 아래 규칙 참조).
///
/// 표시 규칙(사용자 확정):
///  · 유휴 = 1줄 중앙(모듈 3자 표기) / 열림 = 2줄·모듈 색.
///  · A102(v0.130.0): 테두리는 <b>모듈 색</b>이 되고 창 개수 조건이 사라졌다(구: 인스턴스 9색·
///    2개 이상일 때만). 링 유무 판단은 <see cref="Branding.IconRing"/> 한 곳 —
///    값 2줄을 채우는 정보(H/W) 모듈과 중립 화면은 링이 없다.
///    번호 표시는 창 제목의 접두 숫자(A103)가 전담한다.
///  · A139(v0.164.0): 테두리 두께·모서리 반경을 100% 배율에서 1px이 되게 줄였다
///    (<see cref="EdgeUnit"/> — 종전 2.0·3.5). 남는 공간을 글자가 쓴다.
///  · A140(v0.164.0): <b>열림 = 테두리만 모듈 색</b>(배경은 다크 판 유지) /
///    <b>유휴 = 아이콘 전면을 모듈 색으로 채우고 글자는 흰색</b>. 규칙 밖(하드웨어·중립)은
///    호출자가 <see cref="Branding.IdleFill"/>로 null을 줘서 종전 모습이 유지된다.
///  · A169(v0.172.0): <b>열림의 글자색만</b> 줄별로 갈릴 수 있다 — 모듈이
///    <c>TrayStatus.Line1Color</c>·<c>Line2Color</c>(ARGB)를 실으면 그 색을 액센트와 같은 방식
///    (<see cref="Lighten"/> 0.30)으로 밝혀 쓴다(하드웨어의 센서 채널 색 복원). 안 실으면 종전 그대로.
///    유휴 경로·막대(<c>Line2Bars</c>)·테두리 링은 이 축과 무관하다.
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

    // ---------- 색 상수 (A54 → A140/v0.164.0) — 한 곳에 모아 둔 실기기 미세조정 지점 ----------

    /// <summary>액센트 색이 없는 화면(설정·미지원 파일)에서 쓰는 중립 글자색.</summary>
    private static readonly GdiColor Neutral = GdiColor.FromArgb(0xD0, 0xD4, 0xDA);

    /// <summary>
    /// 배경 판 — 밝은 작업표시줄에서도 대비가 나오는 반투명 다크 배지(A18과 같은 값).
    /// A140(v0.164.0)부터 <b>콘텐츠가 열린 상태</b>와 <b>색 규칙 밖 모듈</b>(하드웨어·중립) 전용이다.
    /// 규칙 안 모듈의 유휴는 아래 전면 채움으로 간다.
    /// </summary>
    private static readonly GdiColor Plate = GdiColor.FromArgb(0xE0, 0x20, 0x20, 0x24);

    /// <summary>
    /// 유휴 전면 채움(A140) 위의 글자색 — <b>흰색 고정</b>이다(사용자 확정: 대비 계산을 넣지 않는다).
    /// 채움 자체는 모듈 액센트 원색·불투명(알파 0xFF) — 반투명 판은 열림 상태에서만 의미가 있다.
    /// </summary>
    private static readonly GdiColor IdleFillText = GdiColor.White;

    /// <summary>
    /// 재합성 판단용 키(A18 ComposeKey 방식) — 같으면 GDI 작업을 통째로 건너뛴다.
    /// 아이콘 모양을 바꾸는 입력(내용·모듈·<b>줄 색</b>)을 전부 포함해야 한다. A102(v0.130.0)에서
    /// 인스턴스 번호가 빠졌다 — 번호는 더 이상 아이콘 모양에 관여하지 않고,
    /// 링 색·유무는 모듈 ID에서 나오므로 moduleId가 그 변화를 이미 대표한다.
    ///
    /// A169(v0.172.0)에서 <b>줄 색 두 축이 들어왔다</b>. A139/A140 때는 색을 정하는 입력이
    /// 전부 (moduleId, IsIdle)뿐이라 키 무수정이 안전했지만, 줄 색이 생기면서 그 전제가 깨진다 —
    /// 같은 값을 내는 다른 채널로 갈아타면(예: 62% CPU → 62% GPU) 문자열은 같고 색만 달라지므로
    /// 색을 키에 안 넣으면 아이콘이 갱신되지 않는다. "없음"도 값으로 적어(<c>none</c>)
    /// null과 실색이 절대 안 섞이게 한다(InstanceIcon 키의 <c>ring:none</c>과 같은 관례).
    /// </summary>
    public static string ComposeKey(TrayStatus? status, string? moduleId)
    {
        if (status is null) return $"ico|{moduleId}";
        var bars = status.Line2Bars is { } list
            ? string.Join(',', list.Select(v => Math.Round(v, 2).ToString("0.00")))
            : string.Empty;
        return $"{moduleId}|{status.Line1}|{status.Line2}|{bars}"
             + $"|c1:{KeyColor(status.Line1Color)}|c2:{KeyColor(status.Line2Color)}";
    }

    /// <summary>키에 적는 줄 색 표기(A169) — 없으면 "none".</summary>
    private static string KeyColor(uint? argb) => argb is { } v ? v.ToString("X8") : "none";

    /// <summary>
    /// 상태를 그린 HICON을 만든다(실패하면 IntPtr.Zero — 호출자는 아이콘을 그대로 두면 된다).
    /// </summary>
    /// <param name="ring">테두리 링 색(A102) — null이면 링 없음.</param>
    /// <param name="idleFill">
    /// 유휴(콘텐츠 미개방) 상태에서 아이콘 전면을 채울 색 (A140) — 호출자가
    /// <see cref="Branding.IdleFill"/>로 정한다. null이면 채우지 않고 종전 다크 판을 쓴다
    /// (하드웨어 = 규칙 밖 모듈 / 중립 화면). ring과 값이 같아 보여도 근거가 다른 축이라
    /// <b>ring으로 대신 판정하지 말 것</b> — ring은 하드웨어와 중립을 구분하지 못한다.
    /// </param>
    public static IntPtr Compose(TrayStatus status, Windows.UI.Color? accent, Windows.UI.Color? ring,
        Windows.UI.Color? idleFill)
    {
        try
        {
            return Render(status, accent, ring, idleFill);
        }
        catch
        {
            return IntPtr.Zero; // GDI 리소스 고갈 등 일시 실패 — 다음 값 변화 때 다시 그린다
        }
    }

    private static IntPtr Render(TrayStatus status, Windows.UI.Color? accent, Windows.UI.Color? ring,
        Windows.UI.Color? idleFill)
    {
        var size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        var baseColor = accent is { } c ? GdiColor.FromArgb(c.R, c.G, c.B) : Neutral;

        // A140(v0.164.0) 색 규칙 — 배경과 글자색을 한 쌍으로 정한다.
        //  · 유휴 + 규칙 안 모듈(idleFill 있음) = 모듈 액센트 원색으로 전면 불투명 채움 + 흰 글자.
        //  · 유휴 + 규칙 밖(idleFill null: 하드웨어·중립) = 종전 다크 판 + 저채도 글자(IdleColor).
        //  · 열림 = 상태 무관하게 다크 판 + 액센트 Lighten 0.30 글자. 모듈 색은 링만 진다.
        //    (A169: 모듈이 줄 색을 실었으면 그 줄만 자기 색을 같은 방식으로 밝혀 쓴다 — 아래 ②)
        var fill = status.IsIdle && idleFill is { } f
            ? GdiColor.FromArgb(0xFF, f.R, f.G, f.B)
            : Plate;
        var color = status.IsIdle
            ? (idleFill is null ? IdleColor(baseColor) : IdleFillText)
            : Lighten(baseColor, 0.30);

        // A139(v0.164.0): 배경 판 반경·링 두께·링 반경이 모두 이 한 값이다(종전 3·2.0·3.5).
        var edge = EdgeUnit(size);

        using var bitmap = new System.Drawing.Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // ClearType은 배경 없는 32bpp에 알파를 망가뜨린다 — 회색조 AA (InstanceIcon과 동일, 구 SensorTray에서 온 관용구)
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // ① 배경: 다크 판(열림·규칙 밖) 또는 모듈 색 전면 채움(유휴 — A140).
            //    A139: 모서리 반경도 링과 같은 1px 기준(edge)이라 판과 링의 곡률이 어긋나지 않는다.
            using (var path = RoundedRectPath(0f, 0f, size, size, edge))
            using (var background = new System.Drawing.SolidBrush(fill))
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
                // A169: 열림 두 줄만 줄별 색을 받는다(막대는 범위 밖 — 종전대로 공용 color).
                var lineHeight = size / 2f;
                DrawTextLine(g, status.Line1, LineColor(status.Line1Color, color),
                    margin, 0, textWidth, lineHeight, lineHeight * 0.94f * FontScale);
                if (status.Line2Bars is { Count: > 0 } bars)
                    DrawBars(g, bars, color, margin, lineHeight, textWidth, lineHeight);
                else
                    DrawTextLine(g, status.Line2 ?? TrayStatus.Unknown, LineColor(status.Line2Color, color),
                        margin, lineHeight, textWidth, lineHeight, lineHeight * 0.94f * FontScale);
            }

            // ③ 모듈 색 테두리(A102 — 구 인스턴스 색·창 2개 이상 조건 대체).
            //    링 유무는 호출자가 Branding.IconRing으로 이미 판단해 넘긴다.
            //    스트로크 중심이 edge/2라 링은 바깥 [0..edge] 대역만 차지한다 — 유휴 전면 채움과
            //    겹치는 부분은 둘 다 같은 모듈 색이라(accent 원색) 이음매가 보이지 않는다.
            if (ring is { } ringColor)
            {
                using var pen = new System.Drawing.Pen(
                    GdiColor.FromArgb(ringColor.R, ringColor.G, ringColor.B), edge);
                using var ringPath = RoundedRectPath(edge / 2f, edge / 2f,
                    size - edge, size - edge, edge);
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

    /// <summary>
    /// 열림 한 줄의 글자색 (A169, v0.172.0) — 모듈이 줄 색(ARGB)을 실었으면 그 색을,
    /// 안 실었으면 액센트에서 이미 계산해 둔 기본색(<paramref name="fallback"/>)을 쓴다.
    /// 밝히는 계산은 두 경로가 같다(Lighten 0.30) — 어두운 판 위 가독성 규칙이 색 출처와 무관해야
    /// 하기 때문. 알파는 버린다: 계약이 담는 알파에 의미를 두지 않는다(글자는 늘 불투명).
    /// </summary>
    private static GdiColor LineColor(uint? argb, GdiColor fallback)
    {
        if (argb is not { } v) return fallback;
        return Lighten(GdiColor.FromArgb(
            (int)((v >> 16) & 0xFF), (int)((v >> 8) & 0xFF), (int)(v & 0xFF)), 0.30);
    }

    /// <summary>어두운 배지 위에서 읽히도록 밝힌다(구 A18 SensorTray.Lighten에서 온 계산).</summary>
    private static GdiColor Lighten(GdiColor c, double amount) => GdiColor.FromArgb(
        c.R + (int)((255 - c.R) * amount),
        c.G + (int)((255 - c.G) * amount),
        c.B + (int)((255 - c.B) * amount));

    /// <summary>
    /// 유휴 색 = 채도를 1/4로 낮춘 뒤 밝히기 — "열림"의 선명한 모듈 색과 한눈에 갈린다.
    /// A140(v0.164.0)부터 <b>색 규칙 밖 경로 전용</b>이다(하드웨어 = 콘텐츠 열림·닫힘 구분이
    /// 없는 모듈, 부록 B 67 / 액센트 없는 중립 화면). 규칙 안 모듈의 유휴는 전면 채움 + 흰 글자로
    /// 가므로 이 계산을 타지 않는다 — 두 규칙 밖 경로가 남아 있어 메서드는 존치한다.
    /// </summary>
    private static GdiColor IdleColor(GdiColor c)
    {
        var gray = (int)(c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
        var muted = GdiColor.FromArgb((c.R + gray * 3) / 4, (c.G + gray * 3) / 4, (c.B + gray * 3) / 4);
        return Lighten(muted, 0.55);
    }

    /// <summary>
    /// 테두리 두께 겸 모서리 반경 (A139, v0.164.0) — 100% 배율(트레이 16px)에서 정확히 1px이 되게
    /// size에 비례시킨다: 16px→1 · 24px(150%)→2 · 32px→2 · 48px→3. 트레이 크기는
    /// SM_CXSMICON이라 DPI 배율이 그대로 size에 실린다(배율이 오르면 테두리도 함께 굵어진다).
    /// 두께와 반경을 한 값으로 합친 이유: 사용자 지시가 둘 다 "1px만"이라 값이 늘 같기 때문
    /// (<c>InstanceIcon.EdgeUnit</c>도 같은 식 — 두 파일이 같은 규격을 공유한다).
    /// 종전 식은 두께 <c>Max(1.5f, size/8f)</c>·판 반경 <c>Max(2, size*3/16)</c>·링 반경
    /// <c>Max(2f, size*56f/256f)</c>로 16px에서 2.0·3·3.5였다 — 글자 자리를 크게 깎아먹었다.
    /// MathF는 이 저장소에 선례가 0건이라 <c>(float)Math.Round</c>를 쓴다(반올림 규칙 동일).
    /// </summary>
    private static float EdgeUnit(int size) => Math.Max(1f, (float)Math.Round(size / 16f));

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
