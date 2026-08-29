using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace KOTU.Module.Document;

/// <summary>
/// A115(v0.142.0): 에디터 장식 레이어 — ① 각 줄 폰트 상단·하단 경계의 라인 가이드
/// ② 비가시 문자 표시(줄바꿈 ¶ · 파일 끝 ·EOF). 캔버스는 EditorBox와 같은 칸에 z 위로 겹치고
/// IsHitTestVisible=False라 포커스·입력·편집(A113 더티/저장 흐름)에 일절 관여하지 않는다.
///
/// <b>좌표 출처</b> = TextBox.GetRectFromCharacterIndex(WinAppSDK 공식 API) — 반환 Rect는
/// TextBox 로컬(뷰포트) 좌표로 스크롤을 이미 반영한다. 줄 위치·높이를 전부 이 실측값으로
/// 그리므로 한글·이모지 혼재로 줄 높이가 들쭉날쭉해도 가이드가 어긋나지 않는다 — TextBox에는
/// LineHeight·LineStackingStrategy가 없어(TextBlock 전용) 균일 줄 높이를 강제할 수 없고,
/// 실측이 유일한 정합 경로다(A115 조사 확정).
///
/// <b>A142 추가분</b>: ①ⓐ 스크롤 중간(IsIntermediate) 이벤트는 50ms 간격으로 합쳐 그리고 최종
/// 이벤트만 즉시 그린다(마지막 상태 렌더 보장) · ①ⓑ 전문 텍스트는 소유자(DocumentView)의
/// 편집당 1회 스냅샷(_textProvider)을 함께 써서 Text 게터의 전문 마샬링 복사를 늘리지 않는다.
/// ③ 행 번호 거터를 본문 텍스트 왼끝(pad.Left)의 왼쪽에 그린다 — 레이아웃(정렬·폭)은
/// 일절 건드리지 않으므로 A115의 "EditorBox·DecorLayer 같은 제약 = 같은 원점" 계약이
/// 그대로다(자리가 모자라면 거터만 숨긴다. A181: 폭 제한 폐지로 그 자리는 컬럼 왼쪽 여백이
/// 아니라 소유자가 왼쪽 패딩에 예약한 구간이 됐다 — GutterReserveWidth 주석 참고).
/// ⑤ 가이드는 글자 상·하에서 GuideGap(× 배율 — A284)만큼 띄우고, 인접 줄과 겹침·역전이면 윗줄
/// 밑변+gap 위치에 한 선으로 병합한다(A283 — 경계선은 em 박스를 꽉 채우는 한글 글립을 파고들었다).
/// 첫 줄 윗변은 그리지 않는다(클립 상변과 글자 top이 같아 공간 없음 — 2026-08-29 사용자 확정, A284).
///
/// <b>A277 추가분</b>: 잠금 뷰(A224 — 에디터 그대로 + IsReadOnly) 동안은 위 2축(가이드·¶/EOF)을
/// <b>표시만</b> 억제한다(SetViewSuppressed) — 사용자 토글 상태(_showGuides/_showMarks)와 설정 키는
/// 건드리지 않으므로 편집 모드로 돌아오면 켜져 있던 그대로 되돌아온다. 행 번호 거터(A142 ③)는
/// 축 밖이라 억제 대상이 아니다(읽기에 방해가 아니라 도움 — A277 사양 범위는 "편집 전용 시각 요소").
///
/// <b>실패 안전(설계 의무)</b>: 내부 ScrollViewer(템플릿 ContentElement) 취득은 기본 템플릿
/// 구조 의존이라 WinAppSDK 업데이트에 깨질 수 있는 부류다(v0.113.1 지연 로딩 스타일 사례와
/// 같은 급) — 취득 실패·렌더 중 예외는 장식만 조용히 끄고(Disable) 편집 본기능은 그대로 둔다.
/// 렌더는 뷰포트 안 줄만 걷고(첫 표시 인덱스 이진 탐색 + 줄 단위 지수/이진 전진),
/// TextChanged에서는 플래그만 세운다(A113 디바운스 경로에 무게를 얹지 않는다).
/// </summary>
internal sealed class EditorDecor
{
    // ---- 체감 조정 지점(색·불투명도·글리프 전부 여기 한 곳) ----
    // 가이드는 본문 위에 얹히는 레이어라 아주 옅어야 한다(사양 ③) — 0.08이면 읽기 방해 없음 판정.
    private const double GuideOpacity = 0.08;
    private const double MarkerOpacity = 0.25;
    private const double MarkerFontSize = 12;
    private const string NewlineGlyph = "¶";
    private const string EofGlyph = "·EOF";

    // A142 ⑤(부록 B 69 확정): 가이드를 글자 상·하에서 이만큼 띄운다 — 윗변 −gap / 밑변 +gap.
    // 실기기 왕복으로 정하는 값 — 2026-08-29 2→6(과하면 줄인다. A284: 2에서도 한 줄 밑변 가이드가
    // 한글 글립에 닿았다 = GetRectFromCharacterIndex의 줄 박스가 글립 잉크보다 짧다).
    // gap을 적용하면 인접 줄에서 윗줄 밑변+gap이 아랫줄 윗변−gap보다 아래로 "역전"되므로,
    // 병합 판정은 gap 적용 후 좌표로 다시 하고(아래 AddTopGuide) 겹침·역전이면 윗줄 밑변+gap
    // 위치에 한 선만 긋는다 — 아랫줄 ascent 여백에 놓여 윗줄 한글 글립에서 떨어진다(A283).
    private const double GuideGap = 6; // 100% 기준값 — 실사용은 전부 ScaledGuideGap(× _scale)
    private const double GuideMergeEpsilon = 0.75; // gap 적용 후에도 이 이내로 맞닿으면 같은 선(배율 무관)

    // A284: gap이 고정 px면 A181 줌 확대에서 상대적으로 얇아져 겹침이 확대 상태에서 더 심해진다 —
    // 본문 배율에 비례시킨다. _scale은 필드라 상수 식이 못 되므로 읽기 전용 프로퍼티다.
    private double ScaledGuideGap => GuideGap * _scale;

    // A142 ③: 행 번호 거터. 불투명도는 본문(1.0)보다 옅고 가이드(0.08)보다 진한 중간값(0.4 확정).
    // 폭 = 최대 줄 번호 자릿수 × GutterDigitWidth(우측 정렬 고정 폭이라 별도 측정이 필요 없다).
    // A181: 아래 세 치수(폰트·자릿폭·간격)는 100% 기준값이다 — 실값은 전부 × _scale(본문과 같은
    // 배율로 함께 커지고 작아진다). GuideGap도 배율 비례다(A284 — ScaledGuideGap). GuideOpacity 등
    // 나머지는 배율과 무관하다(헤어라인 미학 — 가이드 위치·높이는 어차피 실측 rect가 배율을 반영한다).
    private const double GutterOpacity = 0.4;
    private const double GutterFontSize = 12;
    private const double GutterDigitWidth = 8;  // 자릿수당 예약 폭(px) — 12px 폰트 숫자에 여유 포함
    private const double GutterTextGap = 12;    // 번호 오른끝과 본문 텍스트 왼끝(pad.Left) 사이 간격(px)
    // A208: 자리가 빠듯할 때 허용하는 최소 간격(px, × _scale). 선호 간격(GutterTextGap)으로 자리가
    // 안 나오면 이 간격까지 줄여서라도 그리고, 이마저 안 되면(번호가 본문을 덮는다) 그때만 숨긴다.
    private const double GutterMinTextGap = 2;

    /// <summary>
    /// A181: 소유자(DocumentView)가 거터 자리를 왼쪽 패딩에 예약할 때 쓰는 산식 — 렌더의 거터
    /// 지오메트리(RenderCore)와 같은 치수 상수에서 나와야 예약과 실그림이 어긋나지 않는다.
    /// </summary>
    internal static double GutterReserveWidth(int digits, double scale) =>
        (GutterTextGap + digits * GutterDigitWidth) * scale;

    // A142 ①ⓐ: 스크롤 중간 이벤트의 렌더 상한(초당 ~20회). 최종(IsIntermediate=false) 이벤트는
    // 이 스로틀을 거치지 않고 즉시 그린다 — 스크롤이 멈춘 화면에 옛 장식이 남지 않게(함정 3).
    private const int ScrollRenderThrottleMs = 50;

    private const int MaxLines = 600;    // 방어: 4K 세로 화면도 수백 줄이 상한 — 그 이상은 비정상
    private const double YEpsilon = 0.5; // 같은 시각적 줄 판정의 y 허용 오차(px)

    private readonly FrameworkElement _themeSource;
    private readonly TextBox _editor;
    private readonly Canvas _canvas;

    // A142 ①ⓑ: 전문 텍스트 공급원 — DocumentView의 편집당 1회 스냅샷(EditorText)을 넘겨받는다.
    // 종전의 자체 _text 캐시를 대체한다(더티 판정과 렌더가 같은 복사본을 나눠 쓴다).
    private readonly Func<string> _textProvider;

    // A181: 본문 줌 배율(1.0 = 100%). 거터·마커 폰트와 거터 지오메트리에만 곱한다 — 가이드
    // 위치·높이는 실측 rect가 이미 배율(FontSize)을 반영하므로 여기서 더 곱할 것이 없다.
    private double _scale = 1.0;

    private ScrollViewer? _scroll; // 템플릿 내부 ContentElement — 표시 후 지연 취득(PdfPane 선례)
    private bool _disabled;        // 폴백: 한 번 끄면 뷰 수명 동안 유지(예외·크래시 금지)
    private int _hookAttempts;     // 레이아웃이 끝났는데도 ScrollViewer가 없으면 포기하는 카운터
    private bool _pending;         // "다음 레이아웃 반영 후 렌더" 예약 — LayoutUpdated가 소비
    private SolidColorBrush _brush;

    private DispatcherTimer? _scrollTimer; // A142 ①ⓐ: 스크롤 중간 이벤트 코얼레싱(1회 예약형)

    // 좌표 기준 자가 확인: 공식 예제상 Rect는 뷰포트 기준이지만, 만에 하나 콘텐츠(문서) 기준으로
    // 나오는 환경이면 세로 오프셋을 빼서 맞춘다. 충분히 스크롤된 첫 순간 1회 판별하고 고정한다.
    private bool _originChecked;
    private bool _contentRelative;
    private double _yShift; // 이번 패스의 y 보정값(뷰포트 기준이면 0)

    // A142 ⑤: 아직 긋지 않고 보류 중인 직전 줄 밑변 가이드 — 다음 줄 윗변과의 병합 판정을 위해
    // 그리기를 한 박자 미룬다(AddTopGuide가 소비, 패스 끝은 FlushPendingGuide가 마감).
    private double _pendingGuideY = double.NaN; // gap 적용 후 y

    // A142 ③: 논리 줄 시작 인덱스(0 포함, 오름차순) — 텍스트 버전당 1회 스캔 캐시.
    // 스냅샷(_textProvider)이 편집당 1회 새 인스턴스를 주므로 참조 비교로 재빌드를 판정한다.
    private int[] _lineStarts = [];
    private string? _lineStartsSource;

    // A215(2026-08-24): 표시 토글 2축 — 라인 가이드 / ¶·EOF 마커. 거터(A142 행 번호)는 축 밖
    // (항상 표시 — 사용자 지시 범위가 "감싸는 줄"과 "펑츄에이션" 둘뿐이다). 끈 축의 요소는
    // 렌더 패스가 아예 안 만들고, EndPass의 잔여 풀 정리(Collapsed)가 이전 패스 요소를 걷는다.
    private bool _showGuides = true;
    private bool _showMarks = true;

    // A277(2026-08-28): 잠금 뷰 억제 축 — 위 2축과 AND로 걸린다(둘 다 켜져야 그린다). 사용자
    // 토글과 별개 축이라 억제 중 토글을 만져도 값만 기록되고, 억제가 풀리면 그 값대로 되살아난다.
    private bool _viewSuppressed;

    private bool GuidesOn => _showGuides && !_viewSuppressed;
    private bool MarksOn => _showMarks && !_viewSuppressed;

    private readonly List<Rectangle> _guides = [];  // 수평선 풀 — 패스마다 재사용
    private readonly List<TextBlock> _markers = []; // ¶·EOF 풀
    private readonly List<TextBlock> _numbers = []; // A142 ③: 행 번호 풀
    private int _guidesUsed;
    private int _markersUsed;
    private int _numbersUsed;

    public EditorDecor(FrameworkElement themeSource, TextBox editor, Canvas canvas,
        Func<string> textProvider)
    {
        _themeSource = themeSource;
        _editor = editor;
        _canvas = canvas;
        _textProvider = textProvider;
        _brush = MakeBrush();

        // 플래그만 — 무게 금지(A113). 전문 스냅샷 무효화는 소유자(DocumentView)가 한다(A142 ①ⓑ).
        _editor.TextChanged += (_, _) => Invalidate();
        _editor.SizeChanged += (_, _) => { UpdateClip(); Invalidate(); };
        // A142 ③: 창(뷰) 폭이 바뀌어도 에디터 쪽 SizeChanged가 안 오는 구간(당시 MaxWidth 초과
        // 폭)을 메우던 구독이다. A181에서 폭 제한이 사라져 둘은 함께 리사이즈되지만, 렌더 산술이
        // 뷰 폭(margin)을 읽는 이상 방어 구독으로 유지한다(플래그 검사뿐이라 비용 없음).
        _themeSource.SizeChanged += (_, _) => { UpdateClip(); Invalidate(); };
        // 트리 레이아웃마다 불리지만 플래그 검사뿐이라 상시 구독 비용이 없다. 텍스트·크기 변경은
        // 새 레이아웃이 반영된 뒤에 재야 해서(rect가 그 레이아웃을 읽는다) 이 시점으로 미룬다.
        _editor.LayoutUpdated += (_, _) =>
        {
            if (!_pending) return;
            _pending = false;
            Render();
        };
        // 스크롤은 레이아웃 패스 없이(컴포지션) 움직인다 — ViewChanged 구독은 EnsureScrollHook에서.
        _themeSource.ActualThemeChanged += (_, _) =>
        {
            _brush = MakeBrush();
            foreach (var guide in _guides) guide.Fill = _brush;
            foreach (var marker in _markers) marker.Foreground = _brush;
            foreach (var number in _numbers) number.Foreground = _brush;
            Render();
        };
    }

    /// <summary>다크 = 흰 선, 라이트 = 검은 선 — 테마 파생 한 쌍(불투명도는 위 상수가 정한다).</summary>
    private SolidColorBrush MakeBrush() =>
        new(_themeSource.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black);

    /// <summary>텍스트·크기·모드 변경 공용 진입점 — 다음 레이아웃 반영 후 다시 그린다.</summary>
    public void Invalidate()
    {
        if (!_disabled) _pending = true;
    }

    /// <summary>
    /// A181: 본문 줌 배율 — 거터·마커(¶·EOF) 폰트가 본문과 같은 배율로 따라온다. 풀에 이미 만들어
    /// 둔 요소의 FontSize도 함께 갱신한다(풀은 뷰 수명 동안 재사용된다 — TakeMarker/TakeNumber).
    /// 위치·폭 재계산은 Invalidate로 다음 레이아웃에 미룬다(렌더 경로가 _scale을 읽는다).
    /// </summary>
    public void SetScale(double scale)
    {
        if (_disabled || Math.Abs(scale - _scale) < 0.0001) return;
        _scale = scale;
        foreach (var marker in _markers) marker.FontSize = MarkerFontSize * scale;
        foreach (var number in _numbers) number.FontSize = GutterFontSize * scale;
        Invalidate();
    }

    /// <summary>
    /// A215: 표시 토글 — guides = 라인 가이드(줄 상/하단 선), marks = ¶·EOF 마커.
    /// SetScale과 같은 관용구(같은 값 조기 반환 + Invalidate — 다음 레이아웃에서 반영).
    /// 폴백 오프(_disabled) 상태에서는 무동작 — 켤 것이 없다.
    /// </summary>
    public void SetDecorVisibility(bool guides, bool marks)
    {
        if (_disabled || (guides == _showGuides && marks == _showMarks)) return;
        _showGuides = guides;
        _showMarks = marks;
        Invalidate();
    }

    /// <summary>
    /// A277: 잠금 뷰(뷰 모드) 표시 억제 — true면 사용자 토글과 무관하게 가이드·¶·EOF를 그리지
    /// 않는다(거터는 축 밖이라 그대로). 소유자(DocumentView)의 뷰 모드 축 단일 산출 지점
    /// (UpdateEditorReadOnly)이 모드 전환 전수에서 이 값을 다시 먹인다.
    /// <para>SetDecorVisibility·SetScale과 달리 <b>즉시 1회 렌더까지</b> 한다: 잠금 뷰 전환은
    /// IsReadOnly·포커스만 바뀌어 레이아웃 패스가 보장되지 않으므로, Invalidate(다음 LayoutUpdated
    /// 대기)만 걸면 장식이 몇 프레임 남거나 아예 안 걷힐 수 있다(A277 최대 함정). Render는
    /// 그 자체가 멱등한 전체 패스라 예약분과 겹쳐 돌아도 결과가 같다.</para>
    /// </summary>
    public void SetViewSuppressed(bool suppressed)
    {
        if (_disabled || suppressed == _viewSuppressed) return;
        _viewSuppressed = suppressed;
        Invalidate(); // 레이아웃이 뒤따라오면 그때 한 번 더(관용구 유지)
        Render();     // 전환 즉시 반영 — 사라짐·재등장이 모드 전환과 같은 프레임에 보인다
    }

    /// <summary>
    /// A177 ⓑ: 대용량 문서(임계 = DocumentView.LargeDocumentChars) — 장식(가이드·거터·¶/EOF)을
    /// 뷰 수명 동안 통째로 끈다. 실패 폴백(Disable)과 같은 기계의 재사용이다: 이후 Invalidate·
    /// Render는 전부 무동작이고, Text 대입 전에 불리면 스크롤 훅(ViewChanged)을 걸기도 전이라
    /// 스크롤마다 도는 실측(GetRectFromCharacterIndex)·편집마다 도는 줄 색인 전수 스캔
    /// (EnsureLineStarts) 비용이 0이 된다. 켜는 길은 없다(폴백과 같은 일방향) — 파일 열기마다
    /// 뷰(와 이 장식기)가 새로 만들어지므로 작은 파일은 자연히 켜진 채 시작한다.
    /// </summary>
    public void DisableForLargeDocument() => Disable();

    /// <summary>즉시 렌더(스크롤·테마 경로). 어떤 예외든 장식만 끈다 — 에디터 본기능 무영향.</summary>
    private void Render()
    {
        if (_disabled) return;
        try
        {
            RenderCore();
        }
        catch
        {
            Disable(); // 함정 1: 실패해도 그 장식만 안 돈다 — 예외를 밖으로 내보내지 않는다
        }
    }

    private void Disable()
    {
        _disabled = true;
        _pending = false;
        _scrollTimer?.Stop(); // 예약된 스로틀 렌더도 함께 무른다(A142 ①ⓐ)
        try
        {
            ClearVisual();
        }
        catch
        {
            // 최후 방어 — 더는 아무것도 하지 않는다(장식은 껐고 편집기는 무관하다)
        }
    }

    private void RenderCore()
    {
        if (_editor.Visibility != Visibility.Visible)
        {
            ClearVisual(); // PDF 모드·빈 화면 — 장식 없음(사양)
            return;
        }
        var vw = _editor.ActualWidth;
        var vh = _editor.ActualHeight;
        var text = _textProvider(); // A142 ①ⓑ: 편집당 1회 스냅샷 — 여기서 전문 재복사가 일어나지 않는다
        if (text.Length == 0 || vw <= 0 || vh <= 0)
        {
            ClearVisual(); // 0바이트 파일은 잴 문자가 없어 EOF 표지도 못 그린다(한계 — 보고됨)
            return;
        }
        if (!EnsureScrollHook())
        {
            ClearVisual();
            return;
        }

        _yShift = 0;
        if (_scroll is { } scroll)
        {
            if (!_originChecked && scroll.VerticalOffset > 48)
            {
                // 스크롤됐는데 첫 문자 y가 위로 밀려나지 않았다면 콘텐츠 기준 좌표다(자가 확인 1회)
                _contentRelative = _editor.GetRectFromCharacterIndex(0, false).Y > -YEpsilon;
                _originChecked = true;
            }
            if (_contentRelative) _yShift = -scroll.VerticalOffset;
        }

        BeginPass();
        var len = text.Length;
        var pad = _editor.Padding;

        // A142 ③: 거터 지오메트리. 번호는 본문 텍스트 왼끝(pad.Left)의 왼쪽에 우측 정렬로 얹는다 —
        // 레이아웃은 무변경이라 A115 계약(두 요소 같은 제약 = 같은 원점)은 그대로다.
        // A181: 폭 제한(A120/A171 MaxWidth) 폐지로 컬럼 왼쪽 여백(음수 x)은 보통 0이다 — 대신
        // 소유자가 거터 자리를 왼쪽 패딩에 예약해(DocumentView.UpdateEditorPadding, 산식은 위
        // GutterReserveWidth 공유) 거터는 패딩 안쪽(x ≥ 0)에 그려진다. 자리가 안 나오면(예약보다
        // 자릿수가 커졌다) 본문을 덮는 대신 거터를 통째로 숨긴다(경계 조건 처리 — 종전과 동일).
        // 치수 × _scale = 본문과 같은 배율(A181 — 상수 블록 주석 참고).
        EnsureLineStarts(text);
        var digits = 1;
        for (var n = _lineStarts.Length; n >= 10; n /= 10) digits++;
        var gutterWidth = digits * GutterDigitWidth * _scale;
        var margin = Math.Max(0, (_themeSource.ActualWidth - vw) / 2); // 컬럼 왼쪽의 가용 여백(보통 0)
        // A208(v0.217.0): 종전 판정은 "선호 간격(GutterTextGap) 기준 x가 클립 좌변(-margin)보다
        // 왼쪽이면 통째 숨김" 하나뿐이라, 실측 pad.Left가 예약 산식(GutterReserveWidth)의 전제와
        // 조금만 어긋나도 — 예약이 안 실린 기본 패딩 24에서는 소형 파일조차(2자릿수 = x −4 < 0) —
        // 거터만 침묵 소멸했다(실기기 보고: 가이드·¶ 정상, 줄번호만 없음). 이제 자리가 빠듯하면
        // 간격을 GutterMinTextGap까지 양보해 클립 좌변에 붙여서라도 그리고, 그래도 번호 오른끝이
        // 본문 왼끝(pad.Left)을 최소 간격 이내로 침범하는 진짜 자리 부족만 숨긴다(종전 폴백 유지 —
        // 본문을 덮는 그림은 여전히 없다). 예약이 정상 실린 상태의 좌표는 종전과 완전히 같다.
        var gutterX = Math.Max(-margin, pad.Left - GutterTextGap * _scale - gutterWidth);
        var gutterVisible = gutterX + gutterWidth <= pad.Left - GutterMinTextGap * _scale;

        var idx = FirstVisibleIndex(len);
        var line = idx >= 0 ? LineIndexOf(idx) : 0; // 첫 표시 줄이 속한 논리 줄(0-base)
        for (var lines = 0; idx >= 0 && lines < MaxLines; lines++)
        {
            var rect = RectOf(idx);
            if (rect.Height <= 0 || rect.Height > vh * 2) break; // 이상값 방어 — 이번 패스 중단
            if (rect.Y >= vh) break;                             // 뷰포트 아래 — 끝
            // A142 ③: 번호는 논리 줄의 첫 시각적 줄에만 — 자동 줄바꿈 연속 줄은 비워 둔다.
            if (gutterVisible && idx == _lineStarts[line])
                DrawLineNumber(line + 1, gutterX, gutterWidth, rect.Y, vh);
            AddTopGuide(rect.Y, vw, vh, pad);
            AddBottomGuide(rect.Y + rect.Height);

            var next = NextLineStart(idx, rect.Y + rect.Height, len);
            if (next < 0)
            {
                DrawEnd(text, len, rect, vw, vh, pad); // 문서 마지막 줄 — 끝 개행 ¶·EOF
                // A142 ③: 파일이 개행으로 끝나면 캐럿이 갈 수 있는 빈 마지막 줄이 하나 더 있다
                // (DrawEnd가 가이드를 긋는 그 줄) — 번호도 단다. 윗변 산술은 DrawEnd와 같다.
                if (gutterVisible && IsNewline(text[len - 1]))
                    DrawLineNumber(line + 2, gutterX, gutterWidth, rect.Y + rect.Height, vh);
                break;
            }
            // 다음 줄 직전 문자가 개행이면 하드 개행(¶), 아니면 자동 줄바꿈(표시 없음 — 실제 바이트가 아니다)
            if (IsNewline(text[next - 1]))
            {
                DrawNewlineGlyph(RectOf(next - 1), vh);
                line++; // 하드 개행 = 다음 논리 줄(자동 줄바꿈은 같은 줄이라 번호가 늘지 않는다)
            }
            idx = next;
        }
        FlushPendingGuide(vw, vh, pad); // A142 ⑤: 마지막 줄 밑변 가이드 마감
        EndPass();
    }

    /// <summary>문서 마지막 시각적 줄: 끝 개행의 ¶ + (개행으로 끝나면) 빈 마지막 줄 가이드 + EOF 표지.</summary>
    private void DrawEnd(string text, int len, Rect lastLine, double vw, double vh, Thickness pad)
    {
        if (IsNewline(text[len - 1]))
        {
            // 파일이 개행으로 끝난다 — 캐럿이 갈 수 있는 빈 마지막 줄이 하나 더 있다.
            // 빈 줄에는 잴 문자가 없어 개행 문자의 셀 높이로 근사한다(혼재 글꼴 줄이면 수 px 오차 허용).
            var newlineRect = RectOf(len - 1);
            DrawNewlineGlyph(newlineRect, vh);
            var top = lastLine.Y + lastLine.Height;
            var height = newlineRect.Height > 0 ? newlineRect.Height : lastLine.Height;
            AddTopGuide(top, vw, vh, pad); // 빈 줄 윗변 — 직전 줄 밑변과 역전이라 경계 한 선으로 병합(A142 ⑤)
            AddBottomGuide(top + height);  // 빈 줄 밑변 — 패스 끝 FlushPendingGuide가 긋는다
            DrawMarker(EofGlyph, pad.Left + 2, top, vh);
        }
        else
        {
            // A284 ⓒ: trailingEdge(GetRectFromCharacterIndex 두 번째 인자 true) 경로 폐기 — 파일 내
            // 유일한 true 호출이라 검증된 적이 없었고, 실기기에서 X·Y가 0으로 나와 EOF가 좌상단에
            // 찍혔다(Height는 정상이라 A283의 Height 가드로는 못 걸렀다). 전 파일이 쓰는 leading
            // 관용구(RectOf)로 줄 끝을 구한다 — 마지막 문자 왼끝 + 폭 = 줄 끝.
            var last = RectOf(len - 1);
            if (last.Height <= 0) return; // rect를 못 얻은 패스는 EOF 생략(어설픈 근사 배치 금지 — 사양 ③)
            if (last.Y < lastLine.Y - YEpsilon) return; // 마지막 줄 범위 밖 = 이상값 — 좌상단 오배치 재발 방지
            DrawMarker(EofGlyph, last.X + last.Width + 4, last.Y, vh);
        }
    }

    // ---------- 줄 탐색(뷰포트 한정 — 전 문서를 걷지 않는다) ----------

    /// <summary>뷰포트에 조금이라도 보이는 첫 문자 인덱스(이진 탐색 — y는 인덱스에 대해 단조).</summary>
    private int FirstVisibleIndex(int len)
    {
        int lo = 0, hi = len - 1, first = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var rect = RectOf(mid);
            if (rect.Y + rect.Height > 0)
            {
                first = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return first;
    }

    /// <summary>
    /// 다음 시각적 줄의 첫 문자 인덱스(-1 = 문서 끝까지 같은 줄). 기준은 현재 줄 기준 문자의
    /// <b>밑변</b>(refBottom) — 윗변끼리 비교하면 혼재 글꼴 줄에서 같은 줄의 키 작은 글자
    /// (이모지 옆 일반 글자는 셀 윗변이 더 낮다)를 다음 줄로 오판한다. 같은 줄 셀의 윗변은
    /// 모두 줄 박스 밑변보다 위, 다음 줄 셀의 윗변은 모두 그 아래이므로 이 판정은 인덱스에
    /// 대해 단조다. 지수 확장으로 다음 줄 이후 지점을 찾고 (low, high] 구간을 이진 탐색한다 —
    /// 시각적 줄 하나당 rect 호출 십수 회 수준이라 뷰포트 전체도 수백 회에 그친다.
    /// </summary>
    private int NextLineStart(int index, double refBottom, int len)
    {
        if (index >= len - 1) return -1;
        var threshold = refBottom - 1.0; // 균일 줄(다음 윗변 == 현 밑변)이 경계에서 새지 않게 1px 여유
        var low = index; // 아직 현재 줄
        var high = -1;   // 다음 줄 이후
        var step = 32;
        while (high < 0)
        {
            var probe = Math.Min(len - 1, low + step);
            if (TopOf(probe) > threshold)
            {
                high = probe;
                break;
            }
            if (probe == len - 1) return -1; // 끝까지 같은 줄
            low = probe;
            step *= 2;
        }
        while (low + 1 < high) // 처음으로 경계 아래로 내려가는 인덱스 = 다음 줄 시작
        {
            var mid = low + (high - low) / 2;
            if (TopOf(mid) > threshold) high = mid;
            else low = mid;
        }
        return high;
    }

    // ---------- 논리 줄 색인 (A142 ③ — 텍스트 버전당 1회 스캔) ----------

    /// <summary>
    /// 논리 줄(하드 개행 기준) 시작 인덱스 표를 스냅샷 버전당 1회만 만든다 — 렌더 패스는
    /// 이진 탐색·배열 조회만 한다. WinUI TextBox는 개행을 '\r'로 정규화하지만(A113) CRLF도
    /// 방어적으로 한 개행으로 센다(IsNewline과 같은 겸용 방침).
    /// </summary>
    private void EnsureLineStarts(string text)
    {
        if (ReferenceEquals(_lineStartsSource, text)) return;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsNewline(text[i])) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; // CRLF는 한 개행
            starts.Add(i + 1);
        }
        _lineStarts = [.. starts];
        _lineStartsSource = text;
    }

    /// <summary>index가 속한 논리 줄(0-base) — PdfPane.CurrentPageIndex와 같은 이진 탐색 관용구.</summary>
    private int LineIndexOf(int index)
    {
        var pos = Array.BinarySearch(_lineStarts, index);
        return pos >= 0 ? pos : ~pos - 1;
    }

    // ---------- 그리기(요소 풀 재사용) ----------

    /// <summary>
    /// A142 ⑤: 줄 윗변 가이드(원좌표 −ScaledGuideGap). 보류 중인 직전 줄 밑변 가이드(+ScaledGuideGap)와
    /// 겹치거나 역전되면 — 인접 줄에서는 항상 그렇다 — 두 선 대신 보류해 둔 밑변+gap 위치에
    /// 한 선만 긋는다(A283 — 줄 경계에서 gap만큼 내려 아랫줄 ascent 여백에 둔다).
    /// gap 적용 "후" 좌표로 판정하는 것이 핵심이다.
    /// </summary>
    private void AddTopGuide(double rawTop, double vw, double vh, Thickness pad)
    {
        var y = rawTop - ScaledGuideGap;
        if (!double.IsNaN(_pendingGuideY) && y <= _pendingGuideY + GuideMergeEpsilon)
        {
            EmitGuide(_pendingGuideY, vw, vh, pad);
            _pendingGuideY = double.NaN;
            return;
        }
        FlushPendingGuide(vw, vh, pad);
        EmitGuide(y, vw, vh, pad);
    }

    /// <summary>A142 ⑤: 줄 밑변 가이드는 즉시 긋지 않고 보류한다 — 다음 줄 윗변과의 병합 판정용.</summary>
    private void AddBottomGuide(double rawBottom)
    {
        _pendingGuideY = rawBottom + ScaledGuideGap;
    }

    private void FlushPendingGuide(double vw, double vh, Thickness pad)
    {
        if (double.IsNaN(_pendingGuideY)) return;
        EmitGuide(_pendingGuideY, vw, vh, pad);
        _pendingGuideY = double.NaN;
    }

    private void EmitGuide(double y, double vw, double vh, Thickness pad)
    {
        // A215 토글 오프 · A277 잠금 뷰 억제 — 병합 부기(pending)는 돌되 선은 안 긋는다
        if (!GuidesOn) return;
        if (y < -1 - ScaledGuideGap || y > vh + 1 + ScaledGuideGap) return; // 뷰포트 밖(클립도 있지만 요소를 아낀다)
        // A284 ⓑ: 클립 상변(UpdateClip — pad.Top) 위 = 첫 줄 윗변(첫 줄 글자 top도 pad.Top이라
        // 그릴 공간이 물리적으로 없다) — 조용히 생략한다(2026-08-29 사용자 확정: 첫 줄 위 선은
        // 필수가 아니고, 클립 확장은 거터·마커까지 영향 범위가 커 하지 않는다. A283의 클램프는
        // 선을 글자 top에 붙일 뿐이라 "위에 그은 선"으로 보이지 않았다). 위로 스크롤돼 클립 밖으로
        // 나간 줄의 가이드도 같은 조건에 걸려 생략되는데, 그게 올바른 동작이다(요소도 아낀다).
        if (y < pad.Top) return;
        var guide = TakeGuide();
        guide.Width = Math.Max(0, vw - pad.Left - pad.Right);
        Canvas.SetLeft(guide, pad.Left);
        Canvas.SetTop(guide, Math.Round(y));
    }

    private void DrawNewlineGlyph(Rect rect, double vh)
    {
        if (rect.Height <= 0) return; // rect를 못 얻은 개행은 건너뛴다(어설픈 근사 배치 금지 — 사양 ③)
        DrawMarker(NewlineGlyph, rect.X + 1, rect.Y, vh);
    }

    private void DrawMarker(string glyph, double x, double y, double vh)
    {
        if (!MarksOn) return; // A215 토글 오프 · A277 잠금 뷰 억제 — 단일 깔때기라 이 한 줄이 전부다
        if (y > vh || y < -30) return;
        var marker = TakeMarker();
        marker.Text = glyph;
        Canvas.SetLeft(marker, Math.Round(x));
        Canvas.SetTop(marker, Math.Round(y));
    }

    /// <summary>A142 ③: 행 번호 — 고정 폭 + 우측 정렬이라 자릿수가 달라도 오른끝이 맞는다(측정 불요).</summary>
    private void DrawLineNumber(int number, double x, double width, double y, double vh)
    {
        if (y > vh || y < -30) return; // 마커(DrawMarker)와 같은 상·하 여유
        var block = TakeNumber();
        block.Text = number.ToString();
        block.Width = width;
        Canvas.SetLeft(block, Math.Round(x));
        Canvas.SetTop(block, Math.Round(y));
    }

    private Rectangle TakeGuide()
    {
        if (_guidesUsed < _guides.Count)
        {
            var reused = _guides[_guidesUsed++];
            reused.Visibility = Visibility.Visible;
            return reused;
        }
        var made = new Rectangle
        {
            Height = 1,
            Fill = _brush,
            Opacity = GuideOpacity,
            IsHitTestVisible = false,
        };
        _guides.Add(made);
        _canvas.Children.Add(made);
        _guidesUsed++;
        return made;
    }

    private TextBlock TakeMarker()
    {
        if (_markersUsed < _markers.Count)
        {
            var reused = _markers[_markersUsed++];
            reused.Visibility = Visibility.Visible;
            return reused;
        }
        var made = new TextBlock
        {
            FontSize = MarkerFontSize * _scale, // A181: 본문과 같은 배율(변경은 SetScale이 일괄 갱신)
            Opacity = MarkerOpacity,
            Foreground = _brush,
            IsHitTestVisible = false,
        };
        _markers.Add(made);
        _canvas.Children.Add(made);
        _markersUsed++;
        return made;
    }

    private TextBlock TakeNumber()
    {
        if (_numbersUsed < _numbers.Count)
        {
            var reused = _numbers[_numbersUsed++];
            reused.Visibility = Visibility.Visible;
            return reused;
        }
        var made = new TextBlock
        {
            FontSize = GutterFontSize * _scale, // A181: 본문과 같은 배율(변경은 SetScale이 일괄 갱신)
            Opacity = GutterOpacity,
            Foreground = _brush,
            TextAlignment = TextAlignment.Right, // 우측 정렬 선례 = HardwareView.xaml.cs:470
            IsHitTestVisible = false,
        };
        _numbers.Add(made);
        _canvas.Children.Add(made);
        _numbersUsed++;
        return made;
    }

    private void BeginPass()
    {
        _guidesUsed = 0;
        _markersUsed = 0;
        _numbersUsed = 0;
        _pendingGuideY = double.NaN; // 직전 패스가 중단됐어도 보류분이 새지 않게(A142 ⑤)
    }

    private void EndPass()
    {
        for (var i = _guidesUsed; i < _guides.Count; i++) _guides[i].Visibility = Visibility.Collapsed;
        for (var i = _markersUsed; i < _markers.Count; i++) _markers[i].Visibility = Visibility.Collapsed;
        for (var i = _numbersUsed; i < _numbers.Count; i++) _numbers[i].Visibility = Visibility.Collapsed;
    }

    private void ClearVisual()
    {
        BeginPass();
        EndPass();
    }

    // ---------- 내부 ScrollViewer 훅·스로틀·클립·rect 헬퍼 ----------

    /// <summary>
    /// EditorBox 템플릿 내부 ScrollViewer(ContentElement)를 얻어 ViewChanged를 건다.
    /// 템플릿 적용 전이면 다음 레이아웃에서 재시도하고, 레이아웃이 끝났는데도 없으면
    /// (WinAppSDK가 템플릿 구조를 바꾼 경우) 장식을 조용히 포기한다 — 함정 1의 폴백.
    /// </summary>
    private bool EnsureScrollHook()
    {
        if (_scroll is not null) return true;
        if (FindDescendant<ScrollViewer>(_editor) is { } found)
        {
            _scroll = found;
            found.ViewChanged += OnScrollViewChanged; // 컴포지션 스크롤은 레이아웃 이벤트가 없다 — 스로틀 렌더(A142 ①ⓐ)
            return true;
        }
        if (_editor.ActualWidth > 0 && ++_hookAttempts >= 3)
        {
            Disable();
            return false;
        }
        _pending = true; // 아직 템플릿 적용 전일 수 있다 — 다음 레이아웃에서 재시도
        return false;
    }

    /// <summary>
    /// A142 ①ⓐ: 종전의 "ViewChanged마다 즉시 렌더"를 스로틀로 바꾼다. 중간(IsIntermediate)
    /// 이벤트는 50ms 타이머로 합쳐 그리고(드래그·관성 중 초당 ~20회 상한), 최종 이벤트는
    /// 즉시 그린다 — 스크롤이 멈춘 뒤 장식이 옛 위치에 남는 일이 없다(함정 3). 최종 이벤트가
    /// 오지 않는 비정상 경로라도 마지막 중간 이벤트가 걸어 둔 타이머가 1회 렌더를 보장한다.
    /// </summary>
    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate)
        {
            _scrollTimer?.Stop(); // 예약분은 이 최종 렌더가 대체한다
            Render();
            return;
        }
        if (_scrollTimer is { IsEnabled: true }) return; // 이번 간격의 렌더는 이미 예약돼 있다
        (_scrollTimer ??= CreateScrollTimer()).Start();
    }

    /// <summary>1회 예약형 타이머(A113 디바운스와 같은 DispatcherTimer 관용구) — Tick에서 스스로 멈춘다.</summary>
    private DispatcherTimer CreateScrollTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollRenderThrottleMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // 다음 중간 이벤트가 다시 시작한다
            Render();
        };
        return timer;
    }

    /// <summary>
    /// 장식을 텍스트와 같은 영역(Padding 안쪽)으로 클립 — 스크롤로 잘리는 줄과 일치시킨다.
    /// A142 ③: 좌변만 컬럼 왼쪽 여백(-margin)까지 내어 거터를 덮는다 — 세로 클립은 종전 그대로다.
    /// 종전 좌변(pad.Left)과 달라져도 기존 장식은 전부 x ≥ pad.Left에 그려져 보이는 결과가 같다.
    /// </summary>
    private void UpdateClip()
    {
        var w = _editor.ActualWidth;
        var h = _editor.ActualHeight;
        var pad = _editor.Padding;
        var margin = Math.Max(0, (_themeSource.ActualWidth - w) / 2);
        _canvas.Clip = w > 0 && h > 0
            ? new RectangleGeometry
            {
                Rect = new Rect(-margin, pad.Top,
                    Math.Max(0, w - pad.Right) + margin,
                    Math.Max(0, h - pad.Top - pad.Bottom)),
            }
            : null;
    }

    private Rect RectOf(int index)
    {
        var rect = _editor.GetRectFromCharacterIndex(index, false);
        return new Rect(rect.X, rect.Y + _yShift, rect.Width, rect.Height);
    }

    private double TopOf(int index) => _editor.GetRectFromCharacterIndex(index, false).Y + _yShift;

    /// <summary>WinUI TextBox는 줄바꿈을 '\r'로 정규화한다(A113 확인) — '\n'은 방어적 겸용.</summary>
    private static bool IsNewline(char c) => c is '\r' or '\n';

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
