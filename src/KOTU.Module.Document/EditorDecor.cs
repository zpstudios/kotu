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
/// <b>A286·A288 좌표 계약(실측 확정)</b>: GetRectFromCharacterIndex가 주는 Rect는 ① <b>캐럿 상자</b>라
/// Width가 항상 0이고(글자 폭이 아니다 — 줄 끝은 "마지막 글자 rect + 폭"이 아니라 인덱스 len의
/// rect다) ② X도 Y도 <b>본문(Padding 안쪽) 기준 상대 좌표</b>라 캔버스에 찍으려면 각각 pad.Left ·
/// pad.Top을 더해야 한다. A286은 X만 마커 호출부에서 개별로 더했고, 남은 Y 누락이 장식 전체를
/// pad.Top만큼 위로 밀고 있었다(A285 계측: 1번 줄 rect.Y=0 · 2번 줄 16인데 화면은 16 · 32).
/// <b>A288(v0.279.0)에서 두 축의 보정을 RectOf/TopOf 한 곳으로 모았다</b> — 그 둘이 돌려주는 값은
/// 이제 캔버스 절대 좌표이고, <b>호출부는 pad를 다시 더하지 않는다</b>. 캔버스 좌표를 rect가 아니라
/// 패딩에서 직접 세우는 지점만 pad를 쓴다: 가이드는 가로 전체 사각형이라 Canvas.SetLeft(pad.Left),
/// 거터는 pad.Left에서 역산한 자체 x, DrawEnd 개행 분기의 줄 머리(pad.Left + 2), 진단 판 위치.
///
/// <b>A289(v0.280.0)</b>: 캐럿 상자의 세 번째 함정 — Height도 실제 줄 간격보다 크다(실측 24 vs 16).
/// "밑변 = Y + Height" 산술의 마지막 사용처(밑변 가이드·빈 마지막 줄 높이)를 걷어냈다: 줄의 진짜
/// 밑변은 다음 줄의 윗변이고(경계 병합선은 AddTopGuide가 그 값으로 긋는다), 다음 줄이 없는 자리는
/// 실측 줄 간격(이번 패스 → 캐시 → Height 폴백 — ResolveLineStep)으로 근사한다. EOF 폴백(len-1)은
/// 직전 글자와의 X 차이로 잰 진출 폭을 더해 마지막 글자 뒤에 선다. rect.Height를 줄 간격처럼 쓰는
/// 산술을 새로 넣지 말 것 — 유효성·가시성 가드로만 쓴다.
///
/// <b>A290(v0.281.0)</b>: 남은 한 자리 — "엔터만 치고 아무것도 안 친 빈 마지막 줄". 그 줄에는 글자가
/// 없어 줄 간격을 잴 기회가 한 번도 없고(뷰포트에 시각적 줄이 하나뿐이라 이번 패스 실측도 캐시도
/// 비어 있다) lineStep이 캐럿 상자 높이까지 폴백해 윗변이 8px 아래로 밀렸다(실측: 커서는 캔버스 32
/// 인데 EOF는 40). 이제 그 줄의 윗변은 <b>끝 캐럿(RectOf(len))의 Y</b>를 1순위로 쓴다 — 커서가 실제로
/// 서는 자리라 컨트롤이 그 좌표를 안다(무효·이상값이면 종전 lastLine.Y + lineStep으로 내려간다).
/// 그리고 그 줄에 얹히는 네 요소(·EOF · 줄 번호 · 윗변 가이드 · 밑변 가이드의 기준)가 <b>DrawEnd가
/// 돌려주는 하나의 top</b>을 공유한다 — 종전에는 줄 번호만 호출부에서 따로 계산해 3자가 어긋났다.
/// <b>ⓓ 보강</b>: 그 끝 캐럿이 유효한 패스에서는 <b>endCaret.Y − lastLine.Y가 곧 진짜 줄 간격의
/// 실측치</b>다(잴 두 번째 줄이 없어 stepHere가 NaN인 바로 그 패스에서 유일하게 잴 수 있는 값).
/// 그 값을 빈 줄 밑변 산술에 즉시 쓰고 줄 간격 캐시(_cachedLineStep)에도 먹인다 — 캐럿 상자 높이
/// 폴백(24)이 실제 간격(16)을 대신하던 마지막 경로가 이것으로 닫힌다.
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
    // 가이드는 본문 위에 얹히는 레이어라 아주 옅어야 한다(사양 ③). 0.08은 글자 위에서 대비가
    // 죽어 "글자 있는 데만 선이 끊겨 보인다"는 실기기 보고 — 2026-08-29 실기기 왕복 3차: 0.08→0.14.
    private const double GuideOpacity = 0.14;
    private const double MarkerOpacity = 0.25;
    private const double MarkerFontSize = 12;
    private const string NewlineGlyph = "¶";
    private const string EofGlyph = "·EOF";

    // A142 ⑤(부록 B 69 확정): 가이드를 글자 상·하에서 이만큼 띄운다 — 윗변 −gap / 밑변 +gap.
    // 실기기 왕복으로 정하는 값 — 2026-08-29 실기기 왕복 3차: 6→8(불투명도를 0.08→0.14로 올려
    // 겹침이 더 눈에 띄므로 간격을 함께 벌린다. A284: 2에서도 한 줄 밑변 가이드가
    // 한글 글립에 닿았다 = GetRectFromCharacterIndex의 줄 박스가 글립 잉크보다 짧다).
    // gap을 적용하면 인접 줄에서 윗줄 밑변+gap이 아랫줄 윗변−gap보다 아래로 "역전"되므로,
    // 병합 판정은 gap 적용 후 좌표로 다시 하고(아래 AddTopGuide) 겹침·역전이면 윗줄 밑변+gap
    // 위치에 한 선만 긋는다 — 아랫줄 ascent 여백에 놓여 윗줄 한글 글립에서 떨어진다(A283).
    // A289: 원 설계값 2로 복귀 — 2→6→8 상향은 실은 Y축 pad.Top 누락(A288)과 밑변의 캐럿 상자
    // 높이 합산(A289)이 선을 글자 속으로 밀던 것을 간격으로 밀어내려던 것이었고, 두 원인이 모두
    // 사라져 되돌린다("2에서도 한글 글립에 닿았다"던 위 A284 관찰도 같은 버그의 증상이었다).
    private const double GuideGap = 2; // 100% 기준값 — 실사용은 전부 ScaledGuideGap(× _scale)
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
    // A289: 값은 "윗변 + 줄 간격(ResolveLineStep) + gap"의 추정치다 — 병합 시 실제 위치는
    // AddTopGuide가 다음 줄 윗변(진짜 경계)으로 다시 잡고, 이 값은 병합 판정과 마지막 줄
    // 마감(FlushPendingGuide — 다음 줄이 없어 추정치가 최선)에만 쓰인다.
    private double _pendingGuideY = double.NaN; // gap 적용 후 y

    // A289 ⓓ: 마지막으로 성공적으로 잰 줄 간격(인접 시각적 줄의 Y 차이) 캐시 — 뷰포트에 시각적
    // 줄이 하나뿐인 패스(예: 엔터만 친 문서)는 이번 패스 실측(stepHere)이 NaN이라 캐럿 상자
    // Height(실측 24 vs 실제 간격 16)로 폴백해 아래 산술이 8px씩 밀렸다. 폴백 순서 =
    // 이번 패스 실측 → 이 캐시 → rect.Height(ResolveLineStep). 줌이 바뀌면 줄 간격도 바뀌므로
    // SetScale이 무효화한다(NaN).
    private double _cachedLineStep = double.NaN;

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

    // ---------- A285: EOF 오배치 계측(diag.editorDecor — 기본 꺼짐) ----------
    // A283·A284 2연속 블라인드 수리 실패 뒤의 계측 시설 — ShellDiagnostics(A234)와 같은 이유·같은
    // 태도("스크린샷 1장으로 원인 확정"). 꺼져 있으면(기본) 아래 어떤 요소도 만들지 않고 문자열
    // 조립도 하지 않는다 — 렌더 핫 패스에 남는 비용은 _diagOn 플래그 검사뿐이다.
    private bool _diagOn;
    private Border? _diagPanel;   // 1개 재사용(풀 관용구) — 패스마다 새로 만들지 않는다
    private TextBlock? _diagText;
    private bool _diagEndReached; // 이번 패스에 DrawEnd가 실제로 불렸는가(마지막 줄이 뷰포트 안)
    private Rect _diagLastRect;   // 이번 패스에 EOF 배치에 실제로 쓴 rect
    // A286: 그 rect를 어느 인덱스로 쟀는지 — 끝 캐럿(len)인지 폴백(len-1)인지. 종전에는 라벨이
    // "RectOf(len-1)" 고정이었는데 이제 분기마다 인덱스가 달라 라벨이 거짓말을 하게 된다.
    private string _diagRectSrc = "len-1";
    private Rect _diagLastLine;   // 이번 패스에 DrawEnd로 넘어온 마지막 시각적 줄 rect
    private string _diagEof = ""; // "x,y"(실제 그린 좌표) 또는 "skipped(가드 이름)"
    // A287 ⓒ: 이번 패스에서 실측한 줄 간격(인접 시각적 줄의 Y 차이) — NaN = 못 잼(뷰포트에 한
    // 줄뿐). 수리(윗변 판정·lineStep)가 맞았는지 다음 스크린샷에서 바로 대조하기 위한 값이다.
    private double _diagLineStep = double.NaN;
    // A290 ⓒ: 빈 마지막 줄(개행으로 끝나는 문서)의 윗변을 무엇으로 정했는가 —
    // endCaret(끝 캐럿 RectOf(len)) / step(이번 패스 실측 줄 간격) / cached(줄 간격 캐시) /
    // height(캐럿 상자 높이 최후 폴백) / n/a(개행으로 끝나지 않는 문서 · DrawEnd 미도달).
    private string _diagEndLineSrc = "n/a";
    // A290 ⓓ: 끝 캐럿으로 실측한 줄 간격(endCaret.Y − lastLine.Y) — 채택된 패스에만 실린다.
    // NaN = 이번 패스에 그 경로를 못 탔다(끝 캐럿 무효·이상값·상식 범위 밖). step= 표기가
    // 이 값을 (endCaret)으로 구분해 보여 준다 — 캐시 폴백(cached)과 헷갈리면 안 된다.
    private double _diagEndStep = double.NaN;

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
        _cachedLineStep = double.NaN; // A289 ⓓ: 배율이 바뀌면 줄 간격도 바뀐다 — 낡은 캐시 무효화
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
    /// A285: EOF 계측 오버레이 토글(diag.editorDecor) — 소유자(DocumentView)가 초기 1회 +
    /// EditorDecorDiagnostics.Changed마다 먹인다. SetViewSuppressed와 같은 관용구(같은 값 조기
    /// 반환 + Invalidate + 즉시 Render — 설정 토글은 레이아웃 패스를 보장하지 않으므로 Invalidate만
    /// 걸면 오버레이가 다음 편집·스크롤까지 안 나타나거나 안 걷힌다). 폴백 오프(_disabled)면 무동작.
    /// </summary>
    public void SetDiagnostics(bool on)
    {
        if (_disabled || on == _diagOn) return;
        _diagOn = on;
        if (!on && _diagPanel is { } panel) panel.Visibility = Visibility.Collapsed;
        Invalidate(); // 레이아웃이 뒤따라오면 그때 한 번 더(관용구 유지)
        Render();     // 전환 즉시 반영 — 켜는 순간 오버레이가 바로 보인다
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
        if (_diagOn)
        {
            // A285: 계측 초기화 — DrawEnd가 안 불리면(마지막 줄이 뷰포트 밖·이상값 중단·MaxLines)
            // 이 값이 그대로 남아 "이번 패스는 EOF 분기까지 못 갔다"를 화면에 말해 준다.
            _diagEndReached = false;
            _diagEof = "skipped(notReached)";
            _diagLineStep = double.NaN; // A287 ⓒ: 이번 패스 실측치만 보인다 — 낡은 값 잔존 금지
            _diagEndLineSrc = "n/a";    // A290 ⓒ: 개행 분기까지 가야 값이 실린다(그 외는 무의미)
            _diagEndStep = double.NaN;  // A290 ⓓ: 끝 캐럿 실측도 이번 패스 값만 보인다(잔존 금지)
        }
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
        // A287: 직전 시각적 줄의 윗변 Y — 실측 줄 간격(stepHere) 산출용. 캐럿 상자의 Height는
        // 실제 줄 간격보다 클 수 있으므로(2026-08-29 실측: 높이 24 vs 간격 16) 간격은 인접 줄의
        // Y 차이로만 잰다. NaN = 이번 패스에서 아직 두 번째 줄을 못 봤다(첫 줄 처리 중).
        var prevLineY = double.NaN;
        for (var lines = 0; idx >= 0 && lines < MaxLines; lines++)
        {
            var rect = RectOf(idx);
            if (rect.Height <= 0 || rect.Height > vh * 2) break; // 이상값 방어 — 이번 패스 중단
            if (rect.Y >= vh) break;                             // 뷰포트 아래 — 끝
            var stepHere = double.IsNaN(prevLineY) ? double.NaN : rect.Y - prevLineY; // A287 실측 줄 간격
            if (!double.IsNaN(stepHere)) _cachedLineStep = stepHere; // A289 ⓓ: 못 재는 패스가 쓸 캐시
            if (_diagOn && !double.IsNaN(stepHere)) _diagLineStep = stepHere; // A287 ⓒ: step= 표시용
            // A289 ⓐ: 이 줄 아래 산술(밑변 가이드·DrawEnd)이 공유하는 줄 간격. rect.Height는 캐럿
            // 상자 높이라 실제 줄 간격보다 크다(실측 24 vs 16) — 줄의 진짜 밑변은 "다음 줄의 윗변"
            // 이고, 그걸 모르는 지점만 이 값(실측 간격 → 캐시 → Height 폴백)으로 근사한다.
            // A290 ⓒ: 이 값이 어느 단계에서 나왔는지(step/cached/height)를 함께 받아 DrawEnd로
            // 넘긴다 — 빈 마지막 줄 윗변의 좌표원 표기(진단 endLine=)에 쓰인다.
            var lineStep = ResolveLineStep(stepHere, rect.Height, out var stepSrc);
            // A142 ③: 번호는 논리 줄의 첫 시각적 줄에만 — 자동 줄바꿈 연속 줄은 비워 둔다.
            if (gutterVisible && idx == _lineStarts[line])
                DrawLineNumber(line + 1, gutterX, gutterWidth, rect.Y, vh);
            AddTopGuide(rect.Y, vw, vh, pad);
            // A289 ⓐ: 종전 rect.Y + rect.Height는 캐럿 상자 높이(24)만큼 내려간 자리라 병합선이
            // 줄 경계가 아니라 다음 줄 바닥에 그어졌다("줄 사이에 선이 없다" — 사용자 실기기 보고).
            // 밑변 추정 = 윗변 + 줄 간격. 병합 시 실제 위치는 AddTopGuide가 다음 줄 윗변으로 다시
            // 잡으므로 이 추정치는 병합 판정과 마지막 줄 마감에만 쓰인다(_pendingGuideY 주석 참고).
            AddBottomGuide(rect.Y + lineStep);

            // A287: 기준은 현재 줄의 윗변(rect.Y)이다 — 밑변(rect.Y + rect.Height)을 넘기면 캐럿
            // 상자 높이 > 줄 간격인 환경에서 다음 줄을 영원히 못 찾는다(NextLineStart 주석 참고).
            var next = NextLineStart(idx, rect.Y, len);
            if (next < 0)
            {
                // A287 ⓑ·A289 ⓓ: 마지막 줄 아래 산술은 위에서 구한 줄 간격(실측 → 캐시 → Height)을
                // 그대로 쓴다 — 캐럿 상자 높이 폴백은 수 px 아래로 밀리므로 최후 순위다.
                var endTop = DrawEnd(text, len, rect, lineStep, stepSrc, vw, vh, pad); // 끝 개행 ¶·EOF
                // A142 ③: 파일이 개행으로 끝나면 캐럿이 갈 수 있는 빈 마지막 줄이 하나 더 있다
                // (DrawEnd가 가이드를 긋는 그 줄) — 번호도 단다.
                // A290 ⓑ: 종전에는 여기서 윗변을 따로 계산했다(rect.Y + lineStep) — DrawEnd가 끝 캐럿
                // 으로 자리를 고쳐 잡으면 번호만 옛 자리에 남아 ·EOF·가이드·커서와 어긋난다. 이제
                // DrawEnd가 실제로 쓴 윗변을 되돌려받아 그대로 쓴다(같은 하나의 값). 반환 NaN =
                // 개행으로 끝나지 않는 문서 = 빈 마지막 줄이 없다(종전 IsNewline 판정과 동치다).
                if (gutterVisible && !double.IsNaN(endTop))
                    DrawLineNumber(line + 2, gutterX, gutterWidth, endTop, vh);
                break;
            }
            // 다음 줄 직전 문자가 개행이면 하드 개행(¶), 아니면 자동 줄바꿈(표시 없음 — 실제 바이트가 아니다)
            if (IsNewline(text[next - 1]))
            {
                DrawNewlineGlyph(RectOf(next - 1), vh);
                line++; // 하드 개행 = 다음 논리 줄(자동 줄바꿈은 같은 줄이라 번호가 늘지 않는다)
            }
            prevLineY = rect.Y; // A287: 다음 반복이 이 줄과의 Y 차이로 간격을 잰다
            idx = next;
        }
        FlushPendingGuide(vw, vh, pad); // A142 ⑤: 마지막 줄 밑변 가이드 마감
        EndPass();
        // A285: 계측 오버레이는 패스마다 갱신 — RenderCore 안(= Render의 try 경계 안)이라
        // 문자열 조립·측정에서 예외가 나도 기존 폴백(Disable — 장식만 끄고 편집 무영향)이 받는다.
        if (_diagOn) UpdateDiagPanel(text, len, vw, vh, pad);
    }

    /// <summary>
    /// 문서 마지막 시각적 줄: 끝 개행의 ¶ + (개행으로 끝나면) 빈 마지막 줄 가이드 + EOF 표지.
    /// lineStep = 호출부(렌더 루프)가 구한 줄 간격(이번 패스 실측 → 캐시 → 캐럿 상자 높이 —
    /// A289 ⓓ ResolveLineStep), stepSrc = 그 값이 나온 단계 이름(진단 표기용).
    /// <para>A290: <b>반환값 = 빈 마지막 줄의 윗변(캔버스 y)</b>이다 — 개행으로 끝나지 않는 문서는
    /// 그런 줄이 없으므로 NaN. 호출부(렌더 루프)의 줄 번호가 이 값을 그대로 써서 ·EOF·가이드와
    /// 같은 한 좌표를 공유한다(종전에는 호출부가 같은 산식을 따로 계산해 어긋날 수 있었다).</para>
    /// <para>윗변 결정 순서(A290): ① 끝 캐럿 RectOf(len)의 Y — 커서가 실제로 서는 자리라 컨트롤이
    /// 아는 좌표다 ② lastLine.Y + lineStep(A287 ⓑ — 밑변 합산 산식은 폐기됐다. lastLine.Y +
    /// lastLine.Height는 "밑변 = 다음 줄 윗변" 전제라 캐럿 상자 높이 &gt; 줄 간격인 환경에서
    /// 아래로 밀린다 — 2026-08-29 실측: 높이 24 vs 간격 16으로 8px 밀림).
    /// 빈 줄의 밑변은 캐럿 상자 높이가 아니라 윗변 + 줄 간격이다(A289 ⓐ).</para>
    /// <para>A290 ⓓ: ①이 성립한 패스에서는 <b>top − lastLine.Y가 진짜 줄 간격의 실측치</b>다
    /// (두 줄의 윗변을 둘 다 알았다). 빈 줄 밑변은 호출부가 준 lineStep이 아니라 이 실측치를
    /// 쓰고, 같은 값을 줄 간격 캐시에도 먹여 <b>다음 패스부터 마지막 글자 줄의 밑변 가이드까지</b>
    /// 캐럿 상자 높이 폴백에서 벗어나게 한다. 채택 조건 = 양수 &amp;&amp; 캐럿 상자 높이 이하.</para>
    /// </summary>
    private double DrawEnd(string text, int len, Rect lastLine, double lineStep, string stepSrc,
        double vw, double vh, Thickness pad)
    {
        if (IsNewline(text[len - 1]))
        {
            // 파일이 개행으로 끝난다 — 캐럿이 갈 수 있는 빈 마지막 줄이 하나 더 있다.
            // (A290: "개행 문자의 셀 높이로 근사한다"던 옛 주석은 A287·A289가 그 산술을 걷어낸
            // 뒤로 이미 사실이 아니었다 — 아래 윗변 결정 사슬이 실제 산식이다.)
            var newlineRect = RectOf(len - 1);
            DrawNewlineGlyph(newlineRect, vh);
            // A290: 이 줄에는 글자가 없어 줄 간격을 잴 기회가 한 번도 없다 — "엔터만 친" 흐름은
            // 뷰포트에 시각적 줄이 하나뿐이라 이번 패스 실측(stepHere)도 캐시도 비고, lineStep이
            // 캐럿 상자 높이까지 폴백해 윗변이 실제보다 아래로 밀렸다(v0.280.0 실기기 계측:
            // 커서는 캔버스 32인데 EOF는 40 — 8px). 그런데 커서는 그 빈 줄에 정확히 서 있다
            // = 컨트롤이 그 좌표를 안다. 그래서 1순위로 끝 캐럿(RectOf(len))을 직접 잰다.
            // A286이 RectOf(len)을 무효로 판정한 것은 아래 else 분기(개행으로 끝나지 않는 문서)의
            // 실측이었고, 이 분기에서는 시험된 적이 없다 — 무효·이상값이면 종전 산식으로 내려간다.
            // 호출은 else 분기와 같은 관용구로 try/catch에 싼다: 인덱스 len을 범위 밖으로 보고
            // 던지는 구현이면 예외가 Render의 포괄 catch까지 새어 Disable()이 불리고, 가이드·거터·
            // 마커가 뷰 수명 동안 통째로 사라진다(마커 오배치보다 훨씬 나쁜 회귀다).
            Rect endCaret;
            try
            {
                endCaret = RectOf(len);
            }
            catch
            {
                endCaret = default;
            }
            var top = lastLine.Y + lineStep; // A287 ⓑ: 실측 줄 간격 — 밑변 합산 산식 폐기
            var topSrc = stepSrc;
            // A290 ⓓ: 이 줄 아래 산술(밑변 가이드)이 쓸 줄 간격. 끝 캐럿이 유효하면 아래에서
            // 실측치로 승격된다 — 그 전까지는 호출부가 준 값(실측 → 캐시 → 캐럿 상자 높이)이다.
            var endStep = lineStep;
            // 이상값 가드: 빈 줄은 마지막 글자 줄의 바로 아랫줄이라 그 사이(lastLine.Y 초과 ~
            // lastLine.Y + lineStep × 3 이하)를 벗어날 수 없다 — 벗어나면 2순위로 내려간다
            // (A284의 좌상단 오배치처럼 X·Y가 0으로 나오는 환경 방어. 아래 경계의 YEpsilon은
            // 이 파일이 같은 줄 판정에 쓰는 허용 오차와 같다 — 빈 줄은 한 줄 간격 아래라 무해하다).
            if (endCaret.Height > 0
                && endCaret.Y > lastLine.Y + YEpsilon
                && endCaret.Y <= lastLine.Y + lineStep * 3)
            {
                top = endCaret.Y;
                topSrc = "endCaret";
                // A290 ⓓ: 여기서 두 줄의 윗변을 둘 다 알았다 = 그 차이가 진짜 줄 간격의 실측치다
                // (실기기 계측: 32 − 16 = 16 — 캐럿 상자 높이 폴백 24가 8px 과대였다). 이 패스는
                // 시각적 줄이 하나뿐이라 렌더 루프의 stepHere가 NaN인, 줄 간격을 잴 수 있는
                // 유일한 자리다. 상식 범위 가드 = 양수이고 캐럿 상자 높이 이하 — 줄 간격이 캐럿
                // 상자 높이보다 클 수는 없다(A286·A289로 확정된 사실. 위 가드는 lineStep × 3까지
                // 허용하므로 폴백 lineStep이 부풀어 있으면 여기까지 통과할 수 있다).
                var measuredStep = top - lastLine.Y;
                if (measuredStep > 0 && measuredStep <= lastLine.Height)
                {
                    endStep = measuredStep;
                    // 캐시에도 먹인다 — 다음 패스부터는 마지막 글자 줄(1번 줄)의 lineStep도
                    // 이 값이 되어 캐럿 상자 높이 폴백이 렌더 산술에서 완전히 사라진다.
                    _cachedLineStep = measuredStep;
                    if (_diagOn) _diagEndStep = measuredStep; // ⓒ: step= 표기를 (endCaret)으로
                }
            }
            if (_diagOn) _diagEndLineSrc = topSrc; // A290 ⓒ: 좌표원 표기(endLine=)
            AddTopGuide(top, vw, vh, pad); // 빈 줄 윗변 — 직전 줄 밑변과 역전이라 경계 한 선으로 병합(A142 ⑤)
            AddBottomGuide(top + endStep); // A289 ⓐ·A290 ⓓ: 빈 줄 밑변 = 윗변 + 줄 간격(끝 캐럿 실측 우선)
            // A288: 이 분기의 x는 rect의 X를 쓰지 않고 줄 머리 위치를 직접 세우는 식이다 —
            // 본문 왼끝의 캔버스 좌표가 곧 pad.Left이므로 보정을 RectOf로 옮긴 뒤에도 pad.Left + 2가
            // 그대로 맞다(이중 가산이 아니다). else 분기의 caret.X + 4가 줄 머리에서 같은 값을 내는
            // 것이 대조 증거다(줄 머리 캐럿의 caret.X = pad.Left + 0).
            if (_diagOn) RecordEofAttempt(newlineRect, lastLine, "len-1", pad.Left + 2, top, vh); // A285
            DrawMarker(EofGlyph, pad.Left + 2, top, vh);
            return top; // A290 ⓑ: 호출부의 줄 번호가 같은 좌표를 쓴다
        }
        else
        {
            // A284 ⓒ: trailingEdge(GetRectFromCharacterIndex 두 번째 인자 true) 경로 폐기 — 파일 내
            // 유일한 true 호출이라 검증된 적이 없었고, 실기기에서 X·Y가 0으로 나와 EOF가 좌상단에
            // 찍혔다(Height는 정상이라 A283의 Height 가드로는 못 걸렀다). 전 파일이 쓰는 leading
            // 관용구(RectOf)로 줄 끝을 구한다.
            //
            // A286(v0.277.0): A285 계측이 두 가지를 실측으로 확정했다.
            // ① GetRectFromCharacterIndex는 캐럿 상자를 준다 — Width가 항상 0이다(한 글자씩 넣으며
            //    3연속 확인: X가 0→14→28로 전진하는 동안 W는 세 번 다 0.0). 그래서 A284의
            //    "마지막 글자 왼끝 + 폭"은 줄 끝이 아니라 마지막 글자 앞을 가리켰다. 인덱스
            //    len(= 마지막 글자 다음 캐럿 자리)이 곧 줄 끝이므로 그 자리를 직접 잰다.
            // ② rect의 X는 본문 기준 상대 좌표다(실측 eof=4.0인데 본문 왼끝은 pad.Left) —
            //    캔버스 좌표로 쓰려면 pad.Left를 더해야 한다. 위 개행 종료 분기가 pad.Left + 2로
            //    더하고 있었고 그 분기만 실기기에서 정상이었던 것이 같은 사실의 반증이다.
            //    A288(v0.279.0): 같은 사실이 Y에도 있었다(pad.Top 누락) — 이제 두 축의 보정을
            //    RectOf/TopOf 한 곳에 모았으므로 caret은 이미 캔버스 절대 좌표다. 여기서
            //    pad.Left를 다시 더하면 이중 가산이라 A286의 가산을 걷어 냈다(eofX 참고).
            // 인덱스 len은 "마지막 글자 다음 캐럿 자리"로 통용되지만 이 저장소에 선례가 0건이라,
            // 범위 밖으로 보고 던지는 구현일 가능성을 여기서 국소적으로 막는다 — 밖으로 새면
            // Render의 포괄 catch가 Disable()을 불러 가이드·거터·마커가 뷰 수명 동안 통째로
            // 사라진다(마커 오배치보다 훨씬 나쁜 회귀다). 던지면 아래 폴백과 같은 길로 합류한다.
            Rect caret;
            try
            {
                caret = RectOf(len);
            }
            catch
            {
                caret = default;
            }
            var rectSrc = "len";
            if (caret.Height <= 0)
            {
                // 폴백 — 끝 캐럿 자리를 못 재는 환경이면 마지막 글자 자리로 물러선다.
                // X가 한 글자만큼 왼쪽이지만 줄은 맞으므로 좌상단 오배치보다 낫다.
                caret = RectOf(len - 1);
                rectSrc = "len-1";
            }
            if (caret.Height <= 0)
            {
                // rect를 못 얻은 패스는 EOF 생략(어설픈 근사 배치 금지 — 사양 ③)
                if (_diagOn) RecordEofSkip(caret, lastLine, rectSrc, "skipped(height)"); // A285: 어느 가드였는지
                return double.NaN; // A290: 이 분기에는 빈 마지막 줄이 없다
            }
            if (caret.Y < lastLine.Y - YEpsilon)
            {
                // 마지막 줄 범위 밖 = 이상값 — 좌상단 오배치 재발 방지(A284 가드 유지)
                if (_diagOn) RecordEofSkip(caret, lastLine, rectSrc, "skipped(lastLine)"); // A285
                return double.NaN; // A290: 이 분기에는 빈 마지막 줄이 없다
            }
            var eofX = caret.X + 4; // A288: caret.X는 이미 pad.Left가 실린 캔버스 좌표다
            if (rectSrc == "len-1")
            {
                // A289 ⓒ: 폴백 rect는 마지막 글자의 "앞쪽" 캐럿 자리다(캐럿 상자라 Width가 0이라
                // 폭을 못 더한다 — A286) — EOF가 마지막 글자에 겹친다. lineStep을 잰 것과 같은
                // 요령으로 마지막 글자의 진출 폭(advance)을 직전 글자와의 X 차이로 실측해 더한다.
                // 같은 줄이 아니거나(직전이 개행) 못 재면 실측 줄 간격으로 근사한다(한글 글립은
                // 대체로 정사각에 가까워 줄 간격과 비슷하다 — 못 재는 것보다 낫다).
                // RectOf(len)이 유효한 환경(rectSrc == "len")은 이미 끝 캐럿이라 더하면 안 된다.
                var advance = lineStep;
                if (len >= 2)
                {
                    var prev = RectOf(len - 2);
                    if (Math.Abs(prev.Y - caret.Y) <= YEpsilon) advance = caret.X - prev.X;
                }
                if (!(advance > 0)) advance = 0; // 음수·NaN 클램프 — EOF가 왼쪽으로 튀면 안 된다
                eofX += advance;
            }
            if (_diagOn) RecordEofAttempt(caret, lastLine, rectSrc, eofX, caret.Y, vh); // A285
            DrawMarker(EofGlyph, eofX, caret.Y, vh);
            return double.NaN; // A290: 개행으로 끝나지 않는 문서 = 빈 마지막 줄이 없다
        }
    }

    // ---------- 줄 탐색(뷰포트 한정 — 전 문서를 걷지 않는다) ----------

    /// <summary>뷰포트에 조금이라도 보이는 첫 문자 인덱스(이진 탐색 — y는 인덱스에 대해 단조).
    /// <para>A288: RectOf가 캔버스 절대 좌표를 주게 되면서 이 판정(밑변 &gt; 0)은 실제 클립 상변
    /// (pad.Top)보다 pad.Top만큼 <b>너그러운</b> 컷이 됐다 — 화면 위로 딱 한 줄 더 이른 인덱스에서
    /// 시작할 수 있다. 의도적으로 그대로 둔다: 너그러운 쪽은 줄을 <b>빠뜨리지 않는</b> 방향이고,
    /// 넘치는 줄의 가이드는 EmitGuide의 y &lt; pad.Top 컷이, 번호·마커는 캔버스 Clip이 걷는다.
    /// pad.Top으로 조이는 것은 렌더 루프 시작점을 건드리는 변경이라 이 배치의 범위 밖이다.</para></summary>
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
    /// 다음 시각적 줄의 첫 문자 인덱스(-1 = 문서 끝까지 같은 줄). 기준은 현재 줄의 <b>윗변</b>
    /// (refTop) — 같은 시각적 줄의 셀은 Y(윗변)가 같고, 다음 줄은 <b>자동 줄바꿈(랩) 줄까지
    /// 포함해</b> Y가 그보다 크다. 이 성질만 쓰므로 rect의 Height와 무관하다.
    /// <para><b>밑변(윗변 + Height) 기준 판정 금지(A287)</b>: GetRectFromCharacterIndex의 rect는
    /// 캐럿 상자라(A286 — Width 항상 0) 그 Height가 실제 줄 간격보다 클 수 있다(2026-08-29 실측:
    /// 높이 24 vs 줄 간격 16). 그러면 "다음 줄 윗변 &gt; 현 밑변" 판정이 영원히 거짓이 되어
    /// 렌더 루프가 첫 줄에서 끝난다 — EOF 오배치·둘째 줄 이후 가이드/번호 소실이라는 A283~A286
    /// 4연속 수리 실패의 진짜 원인이 이것이었다(A285 계측 lastLine이 1번 줄 값으로 남은 것이
    /// 직접 증거).</para>
    /// <para>판정은 인덱스에 대해 단조이므로 지수 확장으로 다음 줄 이후 지점을 찾고 (low, high]
    /// 구간을 이진 탐색한다 — 시각적 줄 하나당 rect 호출 십수 회 수준이라 뷰포트 전체도 수백 회에
    /// 그친다. 종전 밑변 판정의 근거였던 "혼재 글꼴 줄에서 같은 줄 셀의 윗변이 흔들린다"는 우려는
    /// 캐럿 상자 확정(A286)으로 약해졌지만, 만에 하나 같은 줄 Y가 YEpsilon 이상 흔들리는 환경이
    /// 있으면 한 줄이 여러 줄로 쪼개져 보인다 — 그때는 이 허용 오차를 넓히는 것이 복구 지점이다.</para>
    /// </summary>
    private int NextLineStart(int index, double refTop, int len)
    {
        if (index >= len - 1) return -1;
        var threshold = refTop + YEpsilon; // 같은 줄 셀은 Y가 refTop과 같다 — 오차 이내는 같은 줄
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

    /// <summary>
    /// A289 ⓓ: 이 줄에서 쓸 줄 간격 — ① 이번 패스 실측(stepHere: 직전 시각적 줄과의 Y 차이)
    /// ② 마지막으로 성공한 실측 캐시(_cachedLineStep — 뷰포트에 한 줄뿐인 패스를 구제)
    /// ③ 캐럿 상자 높이(rect.Height — 실제 간격보다 커서 수 px 밀리지만 잴 값이 그것뿐인 최후 폴백).
    /// <para>A290 ⓒ: 어느 단계에서 나왔는지를 source로 함께 돌려준다("step" / "cached" / "height") —
    /// 빈 마지막 줄의 좌표원 표기(진단 endLine=)가 이 이름을 그대로 쓴다.</para>
    /// </summary>
    private double ResolveLineStep(double measured, double caretBoxHeight, out string source)
    {
        if (!double.IsNaN(measured))
        {
            source = "step";
            return measured;
        }
        if (!double.IsNaN(_cachedLineStep))
        {
            source = "cached";
            return _cachedLineStep;
        }
        source = "height";
        return caretBoxHeight;
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
    /// 겹치거나 역전되면 — 인접 줄에서는 항상 그렇다 — 두 선 대신 윗줄 밑변+gap 위치에
    /// 한 선만 긋는다(A283 — 줄 경계에서 gap만큼 내려 아랫줄 ascent 여백에 둔다).
    /// gap 적용 "후" 좌표로 판정하는 것이 핵심이다.
    /// <para>A289: 병합선의 위치는 보류값(_pendingGuideY — 윗줄 밑변의 <b>추정치</b>+gap)이 아니라
    /// rawTop + gap으로 긋는다. 보류 가이드는 언제나 바로 윗줄의 것이고, 윗줄의 진짜 밑변은
    /// 정의상 이 줄의 윗변(rawTop)이다 — 여기가 렌더 패스에서 줄 경계를 정확히 아는 유일한
    /// 지점이다. 종전에는 rect.Y + rect.Height(캐럿 상자 높이 24 &gt; 실제 간격 16) 보류값을 그대로
    /// 그어 병합선이 줄 경계가 아니라 아랫줄 바닥에 놓였다("줄 사이에 선이 없다" 실기기 보고).</para>
    /// </summary>
    private void AddTopGuide(double rawTop, double vw, double vh, Thickness pad)
    {
        var y = rawTop - ScaledGuideGap;
        if (!double.IsNaN(_pendingGuideY) && y <= _pendingGuideY + GuideMergeEpsilon)
        {
            EmitGuide(rawTop + ScaledGuideGap, vw, vh, pad); // A289: 진짜 경계(rawTop) 기준
            _pendingGuideY = double.NaN;
            return;
        }
        FlushPendingGuide(vw, vh, pad);
        EmitGuide(y, vw, vh, pad);
    }

    /// <summary>A142 ⑤: 줄 밑변 가이드는 즉시 긋지 않고 보류한다 — 다음 줄 윗변과의 병합 판정용.
    /// A289: rawBottom은 "윗변 + 줄 간격"의 추정치다(호출부 2곳 모두) — 병합되면 실제 위치는
    /// AddTopGuide가 다음 줄 윗변으로 다시 잡고, 이 값이 그대로 그어지는 것은 다음 줄이 없는
    /// 마지막 줄(FlushPendingGuide)뿐이다.</summary>
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
        // A288: y가 캔버스 절대 좌표가 되면서 이 비교가 비로소 문자 그대로 성립한다(종전에는 본문
        // 상대 y를 클립 상변과 견줬다). 첫 줄 윗변은 pad.Top - ScaledGuideGap < pad.Top이라
        // 보정 후에도 여전히 생략된다 — 사양(2026-08-29 사용자 확정) 유지.
        if (y < pad.Top) return;
        var guide = TakeGuide();
        guide.Width = Math.Max(0, vw - pad.Left - pad.Right);
        Canvas.SetLeft(guide, pad.Left);
        Canvas.SetTop(guide, Math.Round(y));
    }

    /// <summary>
    /// 개행 자리의 ¶. A288: rect는 이미 캔버스 절대 좌표다(RectOf가 pad.Left·pad.Top을 실어 준다)
    /// — 여기서 pad를 다시 더하면 이중 가산이라 A286이 넣었던 pad.Left 가산과 pad 매개변수를
    /// 함께 걷어 냈다. X를 rect에서 받는 요소는 마커 2종뿐이고, 가이드는 가로 전체 사각형이라
    /// Canvas.SetLeft(pad.Left)로, 거터는 pad.Left에서 역산한 자체 x로 따로 앉는다.
    /// </summary>
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
        // A285: 장식이 안 그려지는 상태(빈 문서·PDF 모드·폴백 오프)면 계측 오버레이도 걷는다 —
        // 낡은 값이 화면에 남으면 계측이 거짓말을 한다. 요소는 남겨 재사용한다(풀 관용구).
        if (_diagPanel is { } panel) panel.Visibility = Visibility.Collapsed;
    }

    // ---------- A285: EOF 계측 오버레이(diag.editorDecor 켜짐일 때만 — 호출부 전부 _diagOn 가드) ----------

    /// <summary>
    /// A285: DrawEnd가 DrawMarker(EofGlyph)에 실제로 넘긴 좌표를 기록한다. DrawMarker 안의 두
    /// 가드(<b>!MarksOn / y &gt; vh || y &lt; -30</b>)를 여기서 미러링해 "계산은 했으나 안 그린"
    /// 경우를 사유와 함께 남긴다 — DrawMarker의 가드를 고치면 이 미러도 함께 고칠 것(동기 의무).
    /// <para>A286: x는 <b>최종 캔버스 좌표</b>다 — 두 호출부 모두 바로 아래 DrawMarker에 넘기는 것과
    /// 같은 식을 넘긴다(개행 분기 pad.Left + 2 / else 분기 eofX). 여기에 보정 전 값을 넘기면 계측이
    /// 거짓말을 한다 — 호출부를 고칠 땐 두 줄을 함께 볼 것.</para>
    /// <para>A288: lastRect·lastLine도 이제 RectOf가 준 <b>캔버스 절대 좌표</b>다 — 화면에 찍히는
    /// Y가 A287까지의 스크린샷보다 pad.Top(기본 16)만큼 크다. 옛 실측값과 대조할 때 주의할 것.</para>
    /// </summary>
    private void RecordEofAttempt(Rect lastRect, Rect lastLine, string rectSrc, double x, double y, double vh)
    {
        _diagEndReached = true;
        _diagLastRect = lastRect;
        _diagRectSrc = rectSrc;
        _diagLastLine = lastLine;
        _diagEof = !MarksOn ? "skipped(marksOff)"
            : y > vh || y < -30 ? "skipped(viewport)"
            : $"{x:F1},{y:F1}";
    }

    /// <summary>A285: DrawEnd의 자체 가드(height·lastLine 대조)에 걸려 EOF를 생략한 패스의 기록.</summary>
    private void RecordEofSkip(Rect lastRect, Rect lastLine, string rectSrc, string reason)
    {
        _diagEndReached = true;
        _diagLastRect = lastRect;
        _diagRectSrc = rectSrc;
        _diagLastLine = lastLine;
        _diagEof = reason;
    }

    /// <summary>
    /// A285: 계측 오버레이 갱신 — 렌더 패스 끝마다 1회(RenderCore 말미, Render의 try 경계 안).
    /// 요소는 1개를 재사용하고(풀 관용구 — 패스마다 새로 만들지 않는다) 편집 영역 오른쪽 위
    /// 구석에 붙인다(IsHitTestVisible=false — 포커스·입력 무관, A115 계약 그대로).
    /// 판은 어두운 반투명 고정색(테마 무관 — ThumbnailExplorer 배지 판과 같은 관용구),
    /// 글자는 등폭 11px 고정(셸 DiagStrip과 같은 치수 — 줌 배율과 무관하게 읽혀야 한다).
    /// </summary>
    private void UpdateDiagPanel(string text, int len, double vw, double vh, Thickness pad)
    {
        if (_diagPanel is null)
        {
            _diagText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.White),
                TextWrapping = TextWrapping.NoWrap,
            };
            _diagPanel = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0x00, 0x00, 0x00)),
                Padding = new Thickness(6, 4, 6, 4),
                IsHitTestVisible = false,
                Child = _diagText,
            };
            _canvas.Children.Add(_diagPanel);
        }
        // A288 계측 표기 주의: 아래 RectOf(...)·lastLine·eof의 좌표는 전부 "보정 후 캔버스 절대
        // 좌표"다(RectOf가 pad.Left·pad.Top을 실어 준다) — A287까지의 스크린샷과 견주면 X는
        // pad.Left, Y는 pad.Top(기본 52 / 16)만큼 커 보이는 것이 정상이다. 본문 상대 좌표가 필요하면
        // 아래 pad= 줄의 값을 빼면 된다.
        // DrawEnd에 못 간 패스(마지막 줄이 뷰포트 밖 등)는 rect 값이 없다 — "-"로 표시한다.
        var rectLine = _diagEndReached
            ? $"RectOf({_diagRectSrc})={_diagLastRect.X:F1},{_diagLastRect.Y:F1},{_diagLastRect.Width:F1},{_diagLastRect.Height:F1}"
            : "RectOf(?)=-";
        var lastLineLine = _diagEndReached
            ? $"lastLine={_diagLastLine.Y:F1},{_diagLastLine.Height:F1}"
            : "lastLine=-";
        // A287 ⓒ: step = 이번 패스 실측 줄 간격. A289 ⓓ: 이번 패스에 못 쟀고 캐시가 살아 있으면
        // 렌더 산술이 그 캐시를 썼다는 뜻이다 — 값 뒤에 (cached)로 표시해 폴백 경로를 화면에서
        // 바로 가려낼 수 있게 한다. n/a = 실측도 캐시도 없음(캐럿 상자 Height 최후 폴백이 돌았다).
        // A290 ⓓ: 그 둘 사이에 끝 캐럿 실측(endCaret.Y − lastLine.Y)이 들어간다 — 이번 패스에
        // 잰 값이라 (cached)로 표기하면 거짓말이 된다(캐시에도 방금 먹였으므로 표기를 안 나누면
        // 낡은 캐시를 쓴 패스와 구분이 안 된다). 우선순위는 인접 줄 실측 > 끝 캐럿 실측 > 캐시.
        var stepLine = !double.IsNaN(_diagLineStep) ? _diagLineStep.ToString("F1")
            : !double.IsNaN(_diagEndStep) ? $"{_diagEndStep:F1}(endCaret)"
            : !double.IsNaN(_cachedLineStep) ? $"{_cachedLineStep:F1}(cached)"
            : "n/a";
        _diagText!.Text =
            $"len={len}  last='{DescribeChar(text[len - 1])}'\n" +
            rectLine + "\n" +
            lastLineLine + "\n" +
            $"yShift={_yShift:F1}  contentRel={_contentRelative}\n" +
            $"eof={_diagEof}\n" +
            // A286: 좌표 보정값(pad)을 화면에 띄운다 — 수리 후 스크린샷에서 "eof - pad"가 본문
            // 상대 좌표와 맞는지 사용자가 바로 대조할 수 있어야 한다. A288: 이제 X·Y 두 축 다
            // 이 값만큼 실려 있다(위 계측 표기 주의 참고).
            $"pad={pad.Left:F1},{pad.Top:F1}\n" +
            $"step={stepLine}\n" +
            // A290 ⓒ: 빈 마지막 줄 윗변의 좌표원 — endCaret(끝 캐럿) / step / cached / height /
            // n/a(개행으로 끝나지 않는 문서 · DrawEnd 미도달). 이 줄의 ·EOF·줄 번호·가이드는
            // 전부 같은 한 값을 쓰므로, endCaret이면 커서와 정확히 같은 자리다.
            $"endLine={_diagEndLineSrc}\n" +
            $"lineStarts={_lineStarts.Length}  scale={_scale:F2}";
        _diagPanel.Visibility = Visibility.Visible;
        // 오른쪽 위 구석 정렬 — 폭은 트리 밖 Measure/DesiredSize 실측(DocumentView 인쇄 프로브 관용구).
        // 클립(UpdateClip)의 우변 = vw - pad.Right, 상변 = pad.Top 안쪽에 4px 여유로 앉힌다.
        _diagPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_diagPanel, Math.Max(pad.Left, vw - pad.Right - _diagPanel.DesiredSize.Width - 4));
        Canvas.SetTop(_diagPanel, pad.Top + 4);
    }

    /// <summary>A285: text[len-1]의 사람이 읽는 표기 — 개행은 \r \n, 제어 문자는 U+ 코드로.</summary>
    private static string DescribeChar(char c) => c switch
    {
        '\r' => "\\r",
        '\n' => "\\n",
        '\t' => "\\t",
        _ => char.IsControl(c) ? $"U+{(int)c:X4}" : c.ToString(),
    };

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

    /// <summary>
    /// A288(v0.279.0): GetRectFromCharacterIndex가 주는 Rect를 <b>캔버스 절대 좌표</b>로 바꿔
    /// 돌려준다 — X에 pad.Left, Y에 pad.Top을 더한다(둘 다 본문 기준 상대 좌표다).
    /// A286이 X만 마커 호출부 3곳에서 개별로 더했고 세로축은 손대지 않아, 모든 장식이
    /// pad.Top만큼 위로 밀려 있었다(A285 계측 실측 2026-08-29: 1번 줄 rect.Y=0 · 2번 줄 16인데
    /// 화면에서는 각각 캔버스 16 · 32에 그려진다. 원 증상 "가이드선이 글자를 파고든다"(A283)도
    /// 이것이다 — 1번 줄 밑변 가이드 0+24=24가 캔버스 16~32를 차지하는 글자 한가운데를 지났다).
    /// <para>보정을 이 한 곳으로 모았으므로 <b>호출부에서 다시 더하지 말 것</b> — A286이 넣었던
    /// 마커 쪽 pad.Left 가산 3곳은 이 배치에서 함께 걷어 냈다(이중 가산 = 가로 두 배 밀림).
    /// 캔버스 좌표를 rect가 아니라 패딩에서 직접 세우는 지점(가이드의 Canvas.SetLeft(pad.Left),
    /// 거터 x 산식, DrawEnd 개행 분기의 pad.Left + 2, 진단 판 위치)은 보정 대상이 아니다.</para>
    /// <para>pad는 매개변수로 받지 않고 _editor.Padding을 직접 읽는다 — RenderCore가 패스 처음에
    /// 읽는 값과 같은 출처이고, 한 렌더 패스 중에는 패딩이 바뀌지 않으므로 일관된다.
    /// _yShift는 별개 축(콘텐츠 기준 좌표 환경의 스크롤 보정)이라 그대로 함께 실린다.</para>
    /// </summary>
    private Rect RectOf(int index)
    {
        var rect = _editor.GetRectFromCharacterIndex(index, false);
        var pad = _editor.Padding;
        return new Rect(rect.X + pad.Left, rect.Y + _yShift + pad.Top, rect.Width, rect.Height);
    }

    /// <summary>A288: RectOf와 같은 보정의 Y 전용 경로(줄 탐색 NextLineStart 전용) — 캔버스 절대 Y.</summary>
    private double TopOf(int index) =>
        _editor.GetRectFromCharacterIndex(index, false).Y + _yShift + _editor.Padding.Top;

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
