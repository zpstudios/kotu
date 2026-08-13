using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 발바닥 도형 (A79, v0.119.0) — 큰 패드 1개 + 발가락 4개의 타원.
///
/// ①② 는 <b>16px에서 읽혀야</b> 한다. 브랜드 시트에서 잘라낸 래스터를 16px로 줄이면 뭉개지므로
/// (그래서 A46 결정 8번이 "KO/TU" 2줄을 골랐다) 아이콘의 발바닥만은 벡터로 그린다.
///
/// 도형 값은 두 벌이 존재한다 — 여기(GDI+, 런타임 합성)와 packaging/brand.py의 PAW 표
/// (Pillow, 커밋되는 .ico 생성). <b>둘은 반드시 같은 값이어야 한다.</b> 한쪽을 고치면 다른 쪽도 고칠 것.
/// 조각끼리 붙으면 실루엣이 뭉개져 발바닥으로 안 읽히므로 사이를 띄운 값이다.
/// </summary>
internal static class BrandPaw
{
    /// <summary>(중심 x, 중심 y, 폭, 높이) — 0~1 정규화. packaging/brand.py의 PAW와 같은 표.</summary>
    private static readonly (float Cx, float Cy, float W, float H)[] Shape =
    [
        (0.500f, 0.775f, 0.660f, 0.450f), // 패드
        (0.095f, 0.335f, 0.190f, 0.290f), // 바깥 왼쪽 발가락
        (0.345f, 0.185f, 0.215f, 0.320f), // 안쪽 왼쪽 발가락
        (0.655f, 0.185f, 0.215f, 0.320f), // 안쪽 오른쪽 발가락
        (0.905f, 0.335f, 0.190f, 0.290f), // 바깥 오른쪽 발가락
    ];

    /// <summary>사각형 안을 꽉 채우도록 발바닥을 그린다(단색 실루엣).</summary>
    public static void Draw(System.Drawing.Graphics g, System.Drawing.RectangleF box, GdiColor color)
    {
        using var brush = new System.Drawing.SolidBrush(color);
        foreach (var (cx, cy, w, h) in Shape)
        {
            g.FillEllipse(brush,
                box.X + (cx - w / 2f) * box.Width,
                box.Y + (cy - h / 2f) * box.Height,
                w * box.Width,
                h * box.Height);
        }
    }
}
