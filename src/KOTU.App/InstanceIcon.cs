using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 아이콘 런타임 합성 (A68 시작 → A102/v0.130.0에서 의미 개편 → A137에서 번호 부분 부활).
/// 이름에 남은 "Instance"는 A102~A136 동안 이력일 뿐이었으나, A137의 16px 번호 타일
/// (<see cref="GetNumberTile"/>)로 이 클래스가 다시 인스턴스 번호를 그린다
/// (인스턴스 9색 팔레트는 A141/v0.162.0에서 사라진 그대로 — 색은 모듈 축이다).
///
/// A102 전에는 이 합성이 "몇 번째 창인가"를 알리는 장치였다(인스턴스 9색 링 + 우하단 원형
/// 번호 배지). 지금은 <b>어느 모듈의 창인가</b>를 알리는 장치다:
/// ① 테두리 링 색 = 그 창 모듈의 액센트 색(<see cref="Branding.IconRing"/>),
/// ② 원형 번호 배지는 렌더 코드째 제거 — 번호는 창 제목의 접두 숫자(A103)가 담당한다.
/// ③ A105 ②(v0.143.0): 창(태스크바) 32px 아이콘 하단에 모듈 3자 표기를 얹을 수 있다(label).
/// ④ A126(v0.148.0): <b>링이 있는 합성은 본체를 링 두께만큼 안쪽으로 줄여 그린다</b> —
///    링이 본체 글리프 가장자리를 덮던 문제(2026-08-14 실기기 보고)의 해결. 링 없는 합성은 전폭.
/// ⑤ A139(v0.164.0): 링 두께·모서리 반경이 <b>100% 배율 1px 비례</b>로 줄었다
///    (<see cref="EdgeUnit"/> — 32px에서 종전 4.0·7.0 → 2·2). ④의 inset이 그만큼 얕아져
///    본체가 커지고, A105 라벨 띠도 넓어진다.
/// ⑥ A137: 창 아이콘 2종이 <b>서로 다른 실시간 정보 타일</b>이 됐다 — 16px = 인스턴스 번호
///    (<see cref="GetNumberTile"/>), 32px = 열림이면 확장자/용량 2줄(<see cref="ComposeOpenTile"/>),
///    유휴면 모듈 3자 전면 채움(<see cref="GetIdleTile"/>). 색은 A140 트레이 규칙을 창에 배선한 것.
///    <b>규칙 밖(하드웨어·중립)의 32px만</b> 종전 .ico 본체 합성(<see cref="GetComposed"/>) 경로로 남는다.
/// ※ A140(v0.164.0)의 색 규칙(열림 = 테두리만 모듈 색 / 유휴 = 전면 모듈 색)은 처음엔
///    <b>트레이에만</b> 적용됐다 — 창 아이콘 배선(⑥)은 A137이 정보 배선과 함께 넣었다.
/// 링은 창 개수와 무관하게 항상 그린다(모듈 식별이 목적이라 "2개 이상일 때만" 조건이 사라졌다).
/// A105부터 링 없는 호출도 허용된다 — 정보(H/W) 모듈이 링 없이 3자 표기(INF)만 얹는 경우.
/// 링도 글자도 없는 화면(설정·빈 셸 = 중립 아이콘)만 이 클래스를 부르지 않고
/// <see cref="BrandIcons.GetBranded"/>로 간다.
///
/// 합성 도구는 그대로 System.Drawing(GDI+) — 모듈 색 .ico(A3) 본체를 그린 뒤 링·글자를 얹는다.
///
/// 반환 HICON은 (경로, 크기, 표식 색, 링 색, 3자 표기)별로 캐시되어 프로세스 수명 동안 유효하다.
/// WM_SETICON은 핸들을 복사하지 않으므로(WindowIcon.cs와 같은 이유) 호출자는
/// 절대 DestroyIcon 하지 말 것. <b>유일한 예외 = <see cref="ComposeOpenTile"/></b> —
/// 파일별 값이 들어가 캐시할 수 없어 호출자 소유로 돌려준다(해당 메서드 주석 참고).
/// </summary>
internal static class InstanceIcon
{
    // 인스턴스 9색 팔레트(A2, v0.58.0 — MainWindow에서 이동)와 그 접근자 ColorFor는
    // A141(v0.162.0)에서 제거했다. A102(v0.130.0) 이후 유일한 소비처가 하단 바 원형 번호
    // 배지였는데 그 배지가 사라졌고(번호 표기는 창 제목 접두 하나로 통합 — A103/A136),
    // 미사용 심볼은 TreatWarningsAsErrors에 걸린다. 색이 다시 필요해지면 부록 B 32번 참조.

    /// <summary>
    /// 합성 결과 캐시 — "경로|크기|표식 색|링 색|3자" → HICON(프로세스 수명, 파괴 금지).
    /// A102(v0.130.0): 키에서 번호·배지 여부가 빠지고 <b>링 색</b>이 들어왔다 —
    /// 색을 정하는 원천이 인스턴스 번호에서 모듈로 바뀌었으므로, 같은 경로·크기라도
    /// 모듈이 다르면(=링 색이 다르면) 다른 항목이 된다. 옛 형식의 키와는 구성 자체가 달라
    /// 스테일 재사용도 성립하지 않는다(캐시는 프로세스 수명뿐이라 디스크 잔재도 없다).
    /// A105(v0.143.0): <b>3자 표기</b>도 키에 명시로 들어간다 — 아래 GetComposed 주석 참고.
    /// 키 조성이 전부 모듈 축(경로×크기×액센트×링×3자)이라 항목 수는 유계다 —
    /// 인스턴스(창) 수와 무관(A104 상한 점검에서 확인한 성질을 유지할 것).
    /// A137: 타일 키("tile:" 접두 — GetNumberTile·GetIdleTile)가 추가됐다. 번호 축은 창별 값이지만
    /// 상한이 "최대 동시 창 수"라 유계가 유지된다(GetNumberTile 주석의 근거). <b>파일별 값(용량)이
    /// 들어가는 열림 타일(ComposeOpenTile)만은 이 캐시에 절대 넣지 않는다</b> — 유일한 비유계 축이다.
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

            // 본체·링·글자 공통 기하 — 3자 표기 자리(A105)와 본체 inset(A126)이 모두 링 상수에서
            // 파생되므로 한 곳에서 계산한다(그래서 링보다 먼저 나온다).
            // A139(v0.164.0): 두께·반경을 100% 배율 1px 기준(EdgeUnit)으로 통일했다 —
            // 종전 식은 두께 Max(1.5f, size/8f)·반경 Max(2f, size*56f/256f)라 16px에서 2.0·3.5,
            // 32px에서 4.0·7.0이었다. 아래 파생 2종(A126 본체 inset·A105 라벨 자리)은 식을 그대로
            // 두므로 두께가 얇아진 만큼 본체와 글자가 자동으로 커진다(의도된 부수 효과).
            var thickness = EdgeUnit(size);
            var radius = thickness;

            // ① 본체: 모듈 색 아이콘 그대로 (A3 유지 — .ico는 16/24/32… 프레임을 다 가짐).
            //    A79(v0.119.0): 브랜드 레벨이 켜져 있으면 여기서 발바닥 표식까지 함께 그려진다 —
            //    표식도 본체와 '같이' inset된다(A126 의도: 좌표계째 옮겨 줄이는 방식이라
            //    표식만 따로 놀 수 없다).
            //    A126(v0.148.0): 링이 있으면 본체를 링 안쪽으로 밀어 넣어 그린다. 전폭으로 그린 뒤
            //    링을 위에 덮던 종전 순서에서는 본체 글리프 가장자리가 링에 먹혔다(2026-08-14 실기기
            //    보고 "글꼴이 테두리 색에 가려짐" — 트레이 글자 쪽은 A102가 0.85 축소로 해결했고
            //    창 아이콘 본체가 남은 몫이었다). inset = 링 두께라 링 스트로크가 차지하는
            //    [0..두께] 대역과 정확히 비껴간다 — A105 라벨의 inset과 같은 파생이라 셋이 한 기준이다.
            //    링 없는 합성(중립·정보 모듈)은 덮을 것이 없으니 지금까지처럼 전폭이다.
            //    축소 크기를 정수로 반올림하는 이유: DrawBase가 new Icon(path, n, n)으로 .ico 프레임을
            //    고르므로(32 → 24는 실제로 존재하는 프레임) 정수 쪽이 프레임 선택·보간에 유리하다.
            //    DrawBase 안의 파생 수치(라운드 사각 반경 56/256·발바닥 비율)는 전부 size 비례라
            //    작아진 크기로 그려도 모양은 그대로 유지된다.
            if (ring is not null)
            {
                var inner = (int)Math.Round(size - thickness * 2f);
                var offset = (size - inner) / 2f; // 반올림 오차를 상하좌우로 고르게 나눈다
                g.TranslateTransform(offset, offset);
                BrandIcons.DrawBase(g, icoPath, inner, accent);
                g.ResetTransform(); // 아래 링·글자는 다시 전체 좌표계로 그린다
            }
            else
            {
                BrandIcons.DrawBase(g, icoPath, size, accent);
            }

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
    ///    A126(v0.148.0) 뒤로 <b>링이 있을 때는 본체 inset도 같은 링 두께</b>라 글자 줄과 본체가
    ///    같은 안쪽 사각을 공유한다(이 계산은 그대로 두면 정합이 오히려 좋아진다).
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
        // 링이 없으면 클립의 기준은 링이 아니라 .ico 본체 자체의 라운드다 — 그래서 A139로
        // 링 반경이 1px 기준으로 줄어든 뒤에도 여기만은 생성 스크립트 값(56/256)을 직접 쓴다
        // (종전에는 링 반경이 우연히 같은 식이라 ringRadius를 그대로 썼다 — A139에서 갈렸다).
        var clipRadius = hasRing
            ? Math.Max(0f, ringRadius - ringThickness / 2f)
            : size * 56f / 256f;
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

    // ---------- A137: 창 아이콘 실시간 정보 타일 (색 규칙 = A140 트레이 규칙의 창 배선) ----------

    /// <summary>
    /// 16px 번호 타일의 글자 크기 배수 — 1~2자 숫자가 아이콘에 꽉 차게 트레이 유휴(0.58)보다 크다.
    /// 두 자리 이상은 아래 DrawTextLine의 폭 축소 루프(하한 5px — A54 실증)가 자동으로 줄인다.
    /// <b>실기기에서 눈으로 보고 미세 조정하는 단일 지점</b>(FontScale·LabelFontScale과 같은 관행).
    /// </summary>
    private const float NumberFontScale = 0.72f;

    /// <summary>
    /// 타이틀바 16px = 인스턴스 번호 타일 (A137 ①).
    /// 같은 주제 세 번째 변경의 이력: A68 "창 아이콘에 번호 배지 존치" → A102 "번호 렌더 코드째
    /// 제거"(우하단 원형 배지 — 위 Compose ③ 주석) → A137 "번호를 아이콘 전면으로 부활"
    /// (<b>부분 반전</b> — 구 배지 형태가 아니라 아이콘에 꽉 차는 전면 타일이다. 부록 B 67 재확인).
    /// 색은 트레이 A140 규칙과 같은 축: 유휴 = 모듈 색(<paramref name="fill"/> =
    /// Branding.IdleFill) 전면 채움 + 흰 숫자 / 열림 = 다크 판 + 모듈 색 Lighten 0.30 숫자.
    /// 규칙 밖(fill null = 하드웨어·중립)은 다크 판 + 흰 숫자·링 없음(구현 시 결정 — 트레이의
    /// 저채도 IdleColor와 달리 숫자는 항상 밝게 유지한다).
    /// 링은 fill 색 그대로다 — 타일에는 .ico 바탕이 없어 "모듈 .ico 부재 → 링 없음"(A102)이
    /// 성립하지 않고, 규칙 안에서 IconRing == IdleFill == ModuleAccent(같은 값)이라 트레이가
    /// 모듈 축으로 긋는 것과 같은 결과가 된다.
    /// 캐시 유계 근거(A104 전제 유지): 키 축 = 크기 × 번호 × fill × 열림. 번호는 창별 값이지만
    /// 상한이 "동시에 연 창의 최대 개수"다 — WindowManager가 1..N을 연속 재배정하므로 창을
    /// 아무리 여닫아도 번호 집합은 1..최대 동시 창 수를 넘지 않는다. 나머지 축은 전부 모듈 축.
    /// 키 접두 "tile:"은 경로 기반 GetComposed 키와의 충돌 방지(경로는 드라이브 문자로 시작).
    /// </summary>
    public static IntPtr GetNumberTile(int size, int number, Windows.UI.Color? fill, bool open)
    {
        if (size < 8 || number <= 0) return IntPtr.Zero;
        var key = $"tile:num|{size}|{number}"
                  + $"|fill:{(fill is { } f ? $"{f.A:X2}{f.R:X2}{f.G:X2}{f.B:X2}" : "none")}"
                  + $"|open:{(open ? 1 : 0)}";
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        var background = open || fill is null
            ? LabelPlate
            : GdiColor.FromArgb(0xFF, fill.Value.R, fill.Value.G, fill.Value.B);
        var text = open && fill is { } a
            ? Lighten(GdiColor.FromArgb(a.R, a.G, a.B), 0.30)
            : GdiColor.White;

        IntPtr icon;
        try
        {
            icon = RenderTile(size, background, text, TileRing(fill), number.ToString(),
                null /* line2 없음 = 1줄 */, size * NumberFontScale);
        }
        catch
        {
            return IntPtr.Zero; // GDI 일시 실패 — 다음 갱신 때 다시 시도(GetComposed와 동일)
        }
        s_cache[key] = icon;
        return icon;
    }

    /// <summary>
    /// 작업표시줄 32px 유휴 타일 (A137 ② 유휴) — 모듈 3자 이니셜(내용은 A105 ② 유지, 색은 A140
    /// 유휴 규칙: 모듈 색 전면 불투명 채움 + 흰 글자). 트레이 유휴(TrayStatusIcon.Render의 전면
    /// 채움 경로)와 같은 모양을 창 크기로 그린 것 — 글자 식도 트레이 유휴(0.58 × 0.85)와 같다.
    /// 규칙 밖(하드웨어·중립)은 이 메서드에 오지 않고 종전 GetComposed/브랜드 경로다(호출자 분기).
    /// 키 축이 전부 모듈 축(크기 × fill × 3자)이라 캐시 유계(A104 전제 유지).
    /// </summary>
    public static IntPtr GetIdleTile(int size, Windows.UI.Color fill, string label)
    {
        if (size < 8 || string.IsNullOrEmpty(label)) return IntPtr.Zero;
        var key = $"tile:idle|{size}|fill:{fill.A:X2}{fill.R:X2}{fill.G:X2}{fill.B:X2}|{label}";
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        IntPtr icon;
        try
        {
            icon = RenderTile(size, GdiColor.FromArgb(0xFF, fill.R, fill.G, fill.B),
                GdiColor.White, TileRing(fill), label, null /* line2 없음 = 1줄 */,
                size * 0.58f * LabelFontScale);
        }
        catch
        {
            return IntPtr.Zero;
        }
        s_cache[key] = icon;
        return icon;
    }

    /// <summary>
    /// 작업표시줄 32px 열림 타일 (A137 ② — "TXT / 40K" 2줄). 색은 A140 열림 규칙(다크 판 +
    /// 모듈 색 Lighten 0.30 글자 + 모듈 색 링 — 트레이 열림과 동일).
    /// <b>캐시하지 않는다</b>: 아래 줄(용량)은 파일별 값이라 s_cache에 넣으면 "키 축이 전부
    /// 모듈 축이라 항목 수 유계"라는 캐시 불변조건(A104 무제한 증빙의 전제)이 깨진다 —
    /// 트레이(TrayStatusIcon.Compose)처럼 즉시 합성하고, 반환 HICON은 <b>호출자(WindowIcon)
    /// 소유</b>로 넘긴다. 단 트레이(Shell_NotifyIcon은 아이콘을 복사한다)와 달리 WM_SETICON은
    /// 핸들을 복사하지 않으므로 <b>즉시 파괴가 아니라 다음 교체가 창에 걸린 뒤 지연 회수</b>다
    /// (WindowIcon.ReplaceDynamicBig — 이 클래스의 다른 반환값과 소유가 다르니 절대 캐시에 넣지
    /// 말 것). 실패는 IntPtr.Zero.
    /// </summary>
    public static IntPtr ComposeOpenTile(int size, Windows.UI.Color accent, string line1, string line2)
    {
        if (size < 8) return IntPtr.Zero;
        try
        {
            var lineHeight = size / 2f;
            return RenderTile(size, LabelPlate,
                Lighten(GdiColor.FromArgb(accent.R, accent.G, accent.B), 0.30),
                TileRing(accent), line1, line2, lineHeight * 0.94f * LabelFontScale);
        }
        catch
        {
            return IntPtr.Zero; // GDI 일시 실패 — 호출자가 키를 비워 다음 갱신 때 재시도
        }
    }

    /// <summary>타일 링 색 = 채움/액센트 색 그대로(위 GetNumberTile 주석의 등가 근거). null = 링 없음.</summary>
    private static GdiColor? TileRing(Windows.UI.Color? fill)
        => fill is { } f ? GdiColor.FromArgb(f.R, f.G, f.B) : null;

    /// <summary>
    /// 타일 공통 렌더 (A137) — 전면 라운드 판(반경 = EdgeUnit, A139) + 글자 1줄(중앙) 또는
    /// 2줄(상/하 반씩) + 링 스트로크. 배치·완충(margin)·줄 산정은 TrayStatusIcon.Render(A54/A140)의
    /// 관용구 복제다(두 파일이 같은 아이콘 규격을 공유하지만 서로 private — EdgeUnit과 같은 관행).
    /// <paramref name="fontPx"/>는 1줄일 때의 글자 크기, 2줄이면 두 줄 모두 같은 값을 쓴다
    /// (호출자가 줄 높이 기준 식으로 만들어 넘긴다).
    /// </summary>
    private static IntPtr RenderTile(int size, GdiColor fill, GdiColor text, GdiColor? ring,
        string line1, string? line2, float fontPx)
    {
        var edge = EdgeUnit(size);
        using var bitmap = new System.Drawing.Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using (var path = RoundedRectPath(0f, 0f, size, size, edge))
            using (var background = new System.Drawing.SolidBrush(fill))
                g.FillPath(background, path);

            var margin = ring is null ? 0f : 1f; // 링과 글자의 완충 — TrayStatusIcon.Render와 동일
            var textWidth = size - margin * 2;
            if (line2 is null)
            {
                DrawTextLine(g, line1, text, margin, 0, textWidth, size, fontPx);
            }
            else
            {
                var lineHeight = size / 2f;
                DrawTextLine(g, line1, text, margin, 0, textWidth, lineHeight, fontPx);
                DrawTextLine(g, line2, text, margin, lineHeight, textWidth, lineHeight, fontPx);
            }

            // 유휴 전면 채움과 링이 같은 모듈 색이라 이음매가 보이지 않는 것도 트레이(A140)와 같다.
            if (ring is { } ringColor)
            {
                using var pen = new System.Drawing.Pen(ringColor, edge);
                using var ringPath = RoundedRectPath(edge / 2f, edge / 2f,
                    size - edge, size - edge, edge);
                g.DrawPath(pen, ringPath);
            }
        }
        return bitmap.GetHicon();
    }

    /// <summary>
    /// 한 줄을 폭에 맞춰 그린다 — TrayStatusIcon.DrawTextLine(A54)의 관용구 복제(하한 5px 축소
    /// 루프 + 최후의 3자 자르기). DrawLabel의 인라인 루프와 달리 타일 2줄·번호가 함께 쓰는 공용이다.
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

    /// <summary>Segoe UI Bold 픽셀 단위 — TrayStatusIcon.MakeFont(A54)와 같은 글꼴 선택.</summary>
    private static System.Drawing.Font MakeFont(float px)
        => new("Segoe UI", px, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);

    /// <summary>다크 판 위에서 읽히도록 밝힌다 — TrayStatusIcon.Lighten(A54, 구 A18)과 같은 계산.</summary>
    private static GdiColor Lighten(GdiColor c, double amount) => GdiColor.FromArgb(
        c.R + (int)((255 - c.R) * amount),
        c.G + (int)((255 - c.G) * amount),
        c.B + (int)((255 - c.B) * amount));

    /// <summary>
    /// 테두리 두께 겸 모서리 반경 (A139, v0.164.0) — <c>TrayStatusIcon.EdgeUnit</c>과 같은 식이다
    /// (두 파일이 같은 아이콘 규격을 공유하지만 서로 private이라 관용구를 복제한다 —
    /// MakeFont·Lighten·RoundedRectPath와 같은 방식). 100% 배율 16px에서 1px이 되게 비례:
    /// 16px→1 · 24px(150%)→2 · 32px→2 · 48px→3. 값을 바꿀 때는 <b>두 파일을 함께</b> 고칠 것.
    /// MathF는 이 저장소에 선례가 0건이라 <c>(float)Math.Round</c>를 쓴다.
    /// </summary>
    private static float EdgeUnit(int size) => Math.Max(1f, (float)Math.Round(size / 16f));

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
