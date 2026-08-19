using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using KOTU.Core.Contracts;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using KOTU.Input;

namespace KOTU.Module.Document;

/// <summary>
/// 문서 화면: 플레인 텍스트(txt·md·log·ini)는 열어서 바로 편집·저장까지 하고(A37 — 뷰어→에디터 승격),
/// PDF는 PdfPane으로 본다(A16). 텍스트 인코딩은 열 때 감지한 것(UTF-8/UTF-8 BOM/UTF-16/CP949)을
/// 저장 시 그대로 보존하고, 줄바꿈도 원본 스타일(CRLF/LF)을 유지한다.
/// 큰 파일(4MB 초과)은 앞부분만 읽으므로 읽기 전용.
/// 파일 I/O는 뷰 전용 워커(A42)에서 수행하고 UI 스레드는 결과 반영만 한다.
///
/// <b>편집 범위(A113 ② 명문화)</b>: 편집·저장은 <b>플레인 텍스트 계열만</b>이다 — PDF는 뷰 전용
/// (<c>_path</c>가 null이라 저장 경로 자체가 없다), 4MB 잘림 텍스트는 IsReadOnly(잘린 채 저장 방지),
/// 비텍스트 포맷(HWP 등)은 뷰어가 생겨도 편집 대상이 아니다.
/// <b>런타임 정합성 체크(A113 ⓐ~ⓓ)</b> — 강행 금지, 항상 사용자에게 선택권을 준다:
/// ⓐ 저장 직후 파일을 다시 읽어 쓴 바이트와 대조(실패 = Retry/Save as.../Cancel),
/// ⓑ 로드 시 라운드트립 판정(무수정 저장이 원본 바이트를 재현 못 하면 저장 전에 예고),
/// ⓒ 더티 = 기준 텍스트와의 실제 내용 비교(길이 우선 + 250ms 디바운스 — undo 원복이면 ●가 꺼진다),
/// ⓓ 저장 직전 디스크 스탬프(수정 시각·크기) 대조로 외부 변경 검출. 전부 잘림·PDF에는 비적용.
/// </summary>
public sealed partial class DocumentView : UserControl,
    IContentStateSource, IBottomBarProvider, IDriveStripHost, ICloseGuard, ITrayStatusProvider
{
    /// <summary>파일을 열면 셸에 알린다(빈 상태 탐색기 내림·오버레이 기준 갱신).</summary>
    public event Action<string>? ContentOpened;

    /// <summary>트레이 아이콘 표시 값이 바뀌었다(A54) — 텍스트·PDF 열기와 닫기,
    /// 페이지 이동(A138), 저장 성공(A137 — 셸의 작업표시줄 용량 갱신 훅) 시점.</summary>
    public event Action? TrayStatusChanged;

    /// <summary>
    /// 지금 보고 있는 파일(트레이 표기용, A54). 편집 대상인 <c>_path</c>와 별개다 —
    /// PDF는 편집하지 않아 <c>_path</c>가 null이지만 트레이에는 열린 파일로 보여야 한다.
    /// </summary>
    private string? _shownPath;

    /// <summary>
    /// A138: 트레이 표기용 PDF 페이지 위치(1-base). 유일한 공급원 = PdfPane.PageChanged
    /// (OpenPdf의 구독 — 하단 바 텍스트와 같은 이벤트). 0 = PDF가 아니다(텍스트 모드는
    /// HidePdf → Clear가 (0,0)으로 되돌린다) — 그때 트레이는 "1/1"로 나간다.
    /// </summary>
    private int _pdfCurrentPage;
    private int _pdfTotalPages;

    /// <summary>
    /// 트레이 아이콘 내용(A54 → A138 개편): 열림 = <b>페이지 위치 대각 표기</b>(좌상 = 현재,
    /// 우하 = 전체 — PDF는 실제 페이지, 텍스트는 페이지 개념이 없어 "1/1", 부록 B 67),
    /// 유휴 = "DOC"(현행 유지). 자릿수는 현재·전체 각각 3자리까지, 4자리 이상 "999+"
    /// (TrayFormat.PageNumber — 부록 B 69). 종전의 확장자·용량 2줄은 A137 ②가 작업표시줄
    /// 32px 아이콘으로 가져갔다(셸이 경로에서 직접 계산 — 중복 표시가 아니라 이관이다).
    /// </summary>
    public TrayStatus GetTrayStatus()
    {
        if (_shownPath is null) return TrayStatus.Idle("DOC");
        return _pdfTotalPages > 0
            ? TrayStatus.OpenDiagonal(
                TrayFormat.PageNumber(_pdfCurrentPage), TrayFormat.PageNumber(_pdfTotalPages))
            : TrayStatus.OpenDiagonal(TrayFormat.PageNumber(1), TrayFormat.PageNumber(1));
    }

    /// <summary>미저장 상태 변화(A37) — 셸이 창 제목 ● 표시에 쓴다.</summary>
    public event Action<bool>? UnsavedChanged;

    /// <summary>4MB 초과 텍스트는 앞부분만 표시(TextBox 성능 보호) — 이때는 편집 불가.</summary>
    private const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// A177: "대용량 문서" 판정 임계(문자 수 — TextBox의 대입·랩 측정 비용은 바이트가 아니라
    /// 문자 수에 비례한다). 이 임계를 넘으면 두 가지가 걸린다:
    /// ⓐ Text 대입을 "로딩 표시가 담긴 첫 프레임이 제출된 뒤"로 미룬다(DeferApplyAfterRender) —
    ///    종전에는 파일 인자 시작 시 대입(수 초 UI 점유)이 첫 표시보다 먼저 와 "창 프레임만 뜨고
    ///    내부가 안 그려지는" 기동 블로킹이 있었다(A178 매끄러움 원칙의 1호 적용).
    /// ⓑ 장식(A115 가이드·A142 거터·¶/EOF)을 뷰 수명 동안 끈다(EditorDecor.DisableForLargeDocument) —
    ///    스크롤 렌더 패스마다 도는 GetRectFromCharacterIndex 실측과 편집마다 도는 줄 색인
    ///    전수 스캔(EnsureLineStarts)이 대용량 스크롤 지연의 잔여 원인이었다.
    /// 값 근거: A177 사양의 제안 임계 1MB 그대로(ASCII/UTF-8에서 1M 문자 ≈ 1MB). 잘림 상한
    /// (MaxBytes 4MB)의 1/4 지점이라 잘림 직전의 최악 구간 전체가 보호권에 들어온다.
    /// </summary>
    private const int LargeDocumentChars = 1024 * 1024;

    private int _openSeq; // 느린 읽기가 최신 열기를 덮지 않게
    private ModuleWorker? _worker; // 파일 읽기·쓰기 전용(A42) — 뷰별 분리
                                   // (드라이브 조회는 A22에서 셸의 드라이브 줄 워커로 옮겼다)

    // ---- 편집 상태 (A37) ----
    private string? _path;                 // 지금 편집 중인 파일
    private TextEncodingKind _encoding;    // 열 때 감지한 인코딩 — 저장 시 보존
    private string _newLine = "\r\n";      // 원본 줄바꿈 스타일 — 저장 시 보존
    private bool _truncated;               // 4MB 잘림 → 읽기 전용
    private bool _dirty;                   // 미저장 변경 여부
    private bool _loadingText;             // 프로그램적 Text 설정 중 TextChanged 무시용

    // ---- A113 런타임 정합성 체크 상태 ----
    private byte[]? _originalBytes;        // ⓑ: 로드한 원본 바이트(잘림이면 null) — 저장 성공 시 쓴 바이트로 재기준화
    private bool _lossyAtLoad;             // ⓑ: 무수정 저장이 원본 바이트를 재현하지 못한다(로드 시 1회 판정)
    private RoundTripLoss _lossyReason;    // ⓑ: 사유 — 저장 전 예고 대화상자의 본문이 갈린다
    private DateTime _diskWriteTimeUtc;    // ⓓ: 열 때·저장 때 기록한 디스크 스탬프(수정 시각)
    private long _diskLength;              // ⓓ: 디스크 스탬프(크기)
    private string _baselineText = string.Empty; // ⓒ: 더티 판정 기준(\n 정규화) — 저장 성공 시 재기준화
    private bool _saving;                  // 저장 흐름(대화상자 포함) 중복 진입 방지 — ContentDialog는 동시 1개

    /// <summary>
    /// A142 ①ⓑ: <c>TextBox.Text</c> 게터는 호출마다 전문을 마샬링 복사한다(4MB급에서 지배적 비용) —
    /// 편집(TextChanged)당 1회만 뜨는 공유 스냅샷. 더티 판정(길이·내용 비교)과 장식 렌더(EditorDecor)가
    /// 같은 인스턴스를 재사용한다. A113 ⓒ의 비교 계약(길이 우선 + 250ms 디바운스 + 전문 비교)은
    /// 무변경이다 — 비교를 없애는 게 아니라 복사 횟수만 줄인다.
    /// </summary>
    private string? _textSnapshot;

    /// <summary>스냅샷 경유 전문 접근 — 에디터 텍스트를 읽는 모든 경로가 이걸 쓴다(A142 ①ⓑ).</summary>
    private string EditorText => _textSnapshot ??= EditorBox.Text;

    /// <summary>
    /// A177 ⓐ: 지연 대입을 기다리는 CompositionTarget.Rendering 핸들러(null = 대기 없음).
    /// static 이벤트라 Unloaded에서 반드시 해제한다(A88 규칙 — 남기면 뷰·창이 통째로 누수되고
    /// UI 스레드를 매 프레임 깨운다). 해제되면 보류 중이던 대입은 조용히 무산된다(뷰가 내려갔다).
    /// </summary>
    private EventHandler<object>? _pendingApplyHandler;

    // A115: 라인 가이드·비가시 문자 장식. 자체 무효화(TextChanged/SizeChanged/ViewChanged)로 돌고
    // 실패하면 스스로 꺼진다 — 이 뷰는 모드 전환(열기·PDF) 시점만 알려 주면 된다.
    private readonly EditorDecor _decor;

    /// <summary>A171→A181: 줌 배율(document.zoom)을 읽고 쓴다(선례 = AudioPlayerView의 _settings).
    /// A171의 폭 설정은 A181에서 사라졌지만 주입 배선은 이 줌 저장이 그대로 재사용한다.</summary>
    private readonly ISettingsService _settings;

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU document worker");

    static DocumentView() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // CP949 사용 전 1회 등록

    public DocumentView(OpenContext context, ISettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        SetupHotkeys(); // A34: 하단 바 버튼 핫키 + 툴팁 표기
        // A115: 에디터 장식(가이드·¶·EOF·A142 행 번호). 전문 텍스트는 공유 스냅샷으로 공급한다(A142 ①ⓑ).
        _decor = new EditorDecor(this, EditorBox, DecorLayer, () => EditorText);

        // A181: 저장된 줌 배율(전역 1벌)을 XAML 기본값(FontSize 14 = 100%) 위에 얹는다.
        // 파일을 열기 전에 적용해야 대용량 텍스트의 랩 계산이 최종 폰트로 한 번에 끝난다(A177).
        // 손으로 고친 settings.json의 범위 밖 값은 조용히 범위로 접는다(A171 음수 방어와 같은 태도).
        _zoomPercent = Math.Clamp(
            settings.Get(DocumentModule.ZoomSettingKey, DefaultZoomPercent),
            MinZoomPercent, MaxZoomPercent);
        ApplyZoom();
        // A181: Ctrl+휠 배선 지점(TextBox 내장 ScrollViewer의 콘텐츠 프레젠터)은 템플릿 전개 후에야
        // 존재하고, EditorBox는 Collapsed로 시작해 Loaded 시점에도 템플릿이 없다 — EditorDecor의
        // ScrollViewer 훅과 같은 사정이라 같은 방식(레이아웃마다 재시도·상한 후 조용히 포기)을 쓴다.
        // 플래그 검사뿐이라 상시 구독 비용은 없다(EnsureZoomWheelHook 주석 참고).
        EditorBox.LayoutUpdated += (_, _) => EnsureZoomWheelHook();

        // A121: PDF 키보드 스크롤의 키 수신 지점. **터널링**(PreviewKeyDown)이라 PdfPane 안쪽
        // ListView·ScrollViewer의 내장 키 내비게이션보다 먼저 온다 — 버블링 KeyDown이면 그것들이
        // 먼저 소비해 우리 비율 스크롤이 성립하지 않는다. KeyboardAccelerator는 쓰지 않는다:
        // 조건부 통과(Handled=false 되돌리기)가 화살표에서는 불안정해 에디터의 커서 이동을
        // 뺏을 위험이 있고, 그게 이 항목의 절대 금지선이다(OnRootPreviewKeyDown 주석 참고).
        // (되돌리기 지점: 이 한 줄을 KeyDown으로 바꾸면 배선은 그대로 살아 있고 우선순위만 내려간다.)
        // 이름은 Control의 protected virtual OnPreviewKeyDown(1인자)과 겹치지 않게 Root를 붙인다
        // (셸 MainWindow.OnRootKeyDown과 같은 관용구).
        PreviewKeyDown += OnRootPreviewKeyDown;

        // A22(v0.108.0): A49의 "좁으면 드라이브 텍스트 숨김"(임계 760) 규칙은 제거했다 —
        // 드라이브 표시가 Auto 폭 텍스트에서 남는 폭(star 칸)을 쓰는 슬롯으로 바뀌어
        // 더는 버튼들을 밀어내지 않는다(넘치면 줄 자체가 스크롤한다). 게다가 이제는
        // 파일이 열려 있지 않을 때만 뜨는데, 그때는 페이지·Fit 표시가 아예 없어 자리도 넉넉하다.

        Loaded += (_, _) => Focus(FocusState.Programmatic);
        Unloaded += (_, _) =>
        {
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
            _dirtyTimer?.Stop(); // A113 ⓒ: 뷰가 내려간 뒤 디바운스 판정이 발화하지 않게
            // A177 ⓐ: 보류 중 지연 대입 해제 — CompositionTarget.Rendering은 static 이벤트라
            // 남기면 뷰가 누수된다(A88 규칙, HardwareView의 맥박 루프 해제와 같은 의무).
            if (_pendingApplyHandler is { } pendingApply)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= pendingApply;
                _pendingApplyHandler = null;
            }
        };

        if (context.FilePath is { } path && File.Exists(path))
            OpenAny(path);
        else
            PlaceholderText.Visibility = Visibility.Visible;
    }

    // ---------- 본문 줌 (A181 — A171 폭 설정 대체) ----------

    /// <summary>
    /// A181 기본 폰트 크기(= 100%). <b>XAML의 FontSize="14"와 반드시 같은 값</b> — 코드가 못 도는
    /// 경로(디자이너)에서도 100% 모양이 남게 XAML 값을 지우지 않는 A171 관용구를 따른다.
    /// </summary>
    private const double BaseEditorFontSize = 14;

    /// <summary>A181 기본 왼쪽 패딩. <b>XAML Padding="24,16,24,68"의 왼쪽 값과 반드시 같은 값</b> —
    /// 거터 예약(UpdateEditorPadding)이 이 위에 얹힌다(위·오른쪽·아래는 건드리지 않는다).</summary>
    private const double BaseEditorPaddingLeft = 24;

    private const int DefaultZoomPercent = 100;
    private const int MinZoomPercent = 50;   // A181 사양 범위 50~300, 단계 10
    private const int MaxZoomPercent = 300;
    private const int ZoomStepPercent = 10;

    /// <summary>현재 줌(%) — 표시(ZoomText)·적용(FontSize)·저장(document.zoom)의 단일 출처.</summary>
    private int _zoomPercent = DefaultZoomPercent;

    /// <summary>정밀 휠(노치 120 미만 delta) 누적 — 한 노치가 될 때까지 모아 한 단계씩 스텝.</summary>
    private int _wheelDeltaAccum;

    /// <summary>거터 예약 자릿수(0 = 예약 없음 — 파일 없음·대용량(A177 장식 오프)·PDF).</summary>
    private int _gutterDigits;

    private int _zoomHookAttempts;
    private bool _zoomHookDone; // 성공 또는 포기 — 이후 LayoutUpdated 검사는 즉시 반환

    /// <summary>
    /// A181: 현재 배율을 에디터에 적용한다 — ⓐ 본문 FontSize(랩 재계산 — ScaleTransform이 아니라
    /// 실측 계열(A115 장식·A142 거터)과 정합하는 유일한 방식), ⓑ 장식 배율(거터·마커 폰트 —
    /// EditorDecor.SetScale), ⓒ 거터 예약 패딩(배율에 비례), ⓓ 하단 바 % 표기.
    /// EditorBox·DecorLayer의 레이아웃 제약(정렬·폭)은 일절 건드리지 않는다 — A115 좌표계 계약은
    /// "두 요소 같은 제약"이고, FontSize·Padding은 EditorBox 내부 값이라 장식이 실측(rect·Padding
    /// 실시간 읽기)으로 자연히 따라온다.
    /// PDF 모드에는 아무 영향이 없다 — PdfPane은 별개 줌 체계(ZoomFactor·Fit)다.
    /// </summary>
    private void ApplyZoom()
    {
        var scale = _zoomPercent / 100.0;
        EditorBox.FontSize = BaseEditorFontSize * scale;
        _decor.SetScale(scale);
        UpdateEditorPadding();
        UpdateZoomText();
    }

    /// <summary>
    /// A181: 왼쪽 패딩 = 기본 24 + 거터 예약 폭. 폭 제한(A120/A171 MaxWidth) 폐지로 본문이 창
    /// 전체를 쓰면서 거터(A142 ③)가 살던 컬럼 왼쪽 여백(캔버스 음수 x)이 사라졌다 — 대신
    /// 에디터 자신의 왼쪽 패딩에 자리를 상시 확보한다. EditorDecor는 Padding을 실시간으로 읽어
    /// (A152 주석) 그 안쪽(x ≥ 0, 클립 안)에 번호를 그리므로 별도 좌표 보정이 없다.
    /// 예약 폭은 파일의 자릿수 + 1(편집으로 줄 수가 한 자릿수 늘어도 바로 안 숨게)이고 배율에
    /// 비례한다(거터 폰트도 같은 배율 — 산식은 EditorDecor.GutterReserveWidth 한 곳).
    /// </summary>
    private void UpdateEditorPadding()
    {
        var reserve = _gutterDigits > 0
            ? EditorDecor.GutterReserveWidth(_gutterDigits, _zoomPercent / 100.0)
            : 0.0;
        var pad = EditorBox.Padding;
        EditorBox.Padding = new Thickness(BaseEditorPaddingLeft + reserve, pad.Top, pad.Right, pad.Bottom);
    }

    /// <summary>
    /// A181: 하단 바 % 표기(이미지 ZoomText/A149 관용구 — 같은 값이면 대입하지 않는다).
    /// 텍스트 문서가 열려 있는 동안 항상 표시(100% 포함), PDF·빈 화면·대용량 지연 대입 대기
    /// (A177 — 그동안 _path는 null)는 빈 문자열.
    /// </summary>
    private void UpdateZoomText()
    {
        var text = _path is not null ? $"{_zoomPercent}%" : string.Empty;
        if (ZoomText.Text == text) return;
        ZoomText.Text = text;
    }

    /// <summary>
    /// A181: 배율 변경의 단일 경로 — 범위로 접고, 적용(ApplyZoom)하고, 즉시 저장한다
    /// (전역 1벌 — 다음에 여는 문서·창부터 자연 반영. 살아 있는 다른 창에 밀어 넣는 전파는
    /// 만들지 않는다 — A171의 "실시간 전파 없음" 결정과 같은 이유).
    /// </summary>
    private void SetZoom(int percent)
    {
        var clamped = Math.Clamp(percent, MinZoomPercent, MaxZoomPercent);
        if (clamped == _zoomPercent) return;
        _zoomPercent = clamped;
        ApplyZoom();
        _settings.Set(DocumentModule.ZoomSettingKey, clamped);
        _settings.Save(); // 즉시 저장(사양) — 설정 화면의 Set/Save 쌍과 같은 관용구
    }

    /// <summary>
    /// A181: Ctrl+휠 배선 — TextBox 내장 ScrollViewer의 콘텐츠 프레젠터에 건다(버블 순서상
    /// ScrollViewer보다 먼저 받아 기본 스크롤을 대체할 수 있는 지점 — A98/PdfPane.HookScroll과
    /// 같은 이유·같은 방식). 템플릿 구조 의존이라 취득 실패는 EditorDecor.EnsureScrollHook과
    /// 같은 폴백을 쓴다: 표시 후 레이아웃 3회까지 재시도하고 그래도 없으면 조용히 포기
    /// (휠 줌만 비활성 — 편집 본기능 무영향).
    /// </summary>
    private void EnsureZoomWheelHook()
    {
        if (_zoomHookDone || EditorBox.Visibility != Visibility.Visible) return;
        if (FindDescendant<ScrollContentPresenter>(EditorBox) is { } presenter)
        {
            // 참조는 보관하지 않는다 — 해제 경로가 없고(뷰와 함께 내려간다) 재탐색도 없다.
            presenter.PointerWheelChanged += OnEditorWheel;
            _zoomHookDone = true;
            return;
        }
        if (EditorBox.ActualWidth > 0 && ++_zoomHookAttempts >= 3) _zoomHookDone = true;
    }

    /// <summary>
    /// A181: 에디터 본문 위 Ctrl+휠 = 줌(노치당 10%p). 휠 단독은 손대지 않는다 — 문서는 스크롤이
    /// 본분이라(이미지의 "휠 단독 = 줌" 사진 특례와 다르고, PDF의 Ctrl 게이트와 같다 —
    /// PdfPane.OnPresenterWheel 관용구). Shift 등 다른 수정키 조합도 기본 처리에 양보한다.
    /// 정밀 터치패드(120 미만 delta)는 한 노치만큼 모일 때까지 누적한다 — 부호만 보면 미세
    /// 이벤트마다 10%씩 튀어 과속한다.
    /// </summary>
    private void OnEditorWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return; // 휠 단독 = 스크롤(기본 처리)
        e.Handled = true; // 내장 처리(Ctrl+휠 스크롤)보다 먼저 소비 — A98 관용구
        var delta = e.GetCurrentPoint(EditorBox).Properties.MouseWheelDelta;
        if (delta == 0) return;
        _wheelDeltaAccum += delta;
        var notches = _wheelDeltaAccum / 120; // 0을 향해 자르는 정수 나눗셈 — 잔여분은 다음 이벤트로
        if (notches == 0) return;
        _wheelDeltaAccum -= notches * 120;
        SetZoom(_zoomPercent + notches * ZoomStepPercent);
    }

    /// <summary>
    /// A181: 거터 예약용 논리 줄 수 — EditorDecor.EnsureLineStarts와 같은 셈법(CRLF는 한 개행,
    /// 끝 개행 뒤의 빈 마지막 줄도 한 줄). 대용량(A177)은 부르지 않으므로 상한 1M 문자 1패스다.
    /// </summary>
    private static int CountLines(string text)
    {
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lines++;
        }
        return lines;
    }

    private static int DigitCount(int value)
    {
        var digits = 1;
        for (var n = value; n >= 10; n /= 10) digits++;
        return digits;
    }

    /// <summary>EditorDecor.FindDescendant와 동일(모듈 파일 간 복제 선례 = PdfPane도 자체 보유).</summary>
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

    /// <summary>확장자로 텍스트/PDF 경로를 나눈다(A16).</summary>
    private void OpenAny(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            OpenPdf(path);
        else
            OpenPath(path);
    }

    /// <summary>하단 상태바를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(이미지 v0.27.0과 동일 패턴).</summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(StatusBar);
        return StatusBar;
    }

    /// <summary>
    /// A22(v0.108.0): 셸이 만든 공용 드라이브 줄을 하단 바 슬롯에 끼운다.
    /// v0.47.0의 모듈별 드라이브 텍스트(DriveInfoText)를 대체한다.
    /// </summary>
    public void AttachDriveStrip(object strip) => DriveStripHost.Content = strip as UIElement;

    /// <summary>
    /// 드라이브 줄과 파일명은 같은 칸을 나눠 쓴다 — 줄이 뜨는 동안(파일 없음, 파일명은
    /// "No file open"뿐)에는 파일명을 비켜준다.
    /// </summary>
    public void ShowDriveStrip(bool show)
    {
        DriveStripHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- 파일 열기 ----------

    private async void OpenPath(string path)
    {
        var seq = ++_openSeq;
        // A177 ⓐ: 워커 읽기·(대용량이면) 대입 지연 동안 첫 프레임에 보일 로딩 표시. 새 UI 없이
        // 기존 플레이스홀더를 문구만 바꿔 재사용한다(문구 관용구 = ContentInfoOverlay의
        // "Loading info..."). 종전에는 읽기 동안 빈 화면이었다 — 작은 파일은 읽기가 프레임보다
        // 빨리 끝나 이 표시가 화면에 오르기 전에 내려가는 게 보통이다(깜빡임 아님).
        // 파일 인자 시작과 모듈 내 열기(탐색기 더블클릭) 모두 뷰 생성자 → 이 메서드 한 경로라
        // 두 진입이 자동으로 같은 동작이다.
        PlaceholderText.Text = $"Loading {Path.GetFileName(path)}...";
        PlaceholderText.Visibility = Visibility.Visible;
        LoadedText loaded;
        try
        {
            loaded = await Worker.Run(_ => ReadTextSmart(path));
        }
        catch (OperationCanceledException)
        {
            return; // 뷰가 내려가며 워커가 닫힘
        }
        catch (Exception ex)
        {
            PlaceholderText.Text = "Failed to open: " + ex.Message;
            PlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        if (seq != _openSeq) return; // 그새 다른 파일이 열렸다

        if (loaded.Text.Length > LargeDocumentChars)
        {
            // A177 ⓑ: 장식 오프는 대입보다 먼저 — 대입이 쏘는 TextChanged→Invalidate부터 전부
            // 무동작이 되고, 스크롤 훅(ViewChanged)은 아예 걸리지도 않는다.
            _decor.DisableForLargeDocument();
            // A177 ⓐ: 대입(수 초 UI 점유)은 로딩 표시 프레임이 제출된 뒤로 미룬다. 미루는 동안
            // 이 뷰는 "파일 없음" 상태 그대로다 — _path=null이라 Ctrl+S는 무동작(SaveAsync 첫
            // 가드), 더티=false라 닫기·모듈 전환 가드(ICloseGuard)는 그냥 통과, 에디터는
            // Collapsed라 편집 입력 자체가 불가. 대입 전 상태에서 저장·닫기·편집이 새지 않는다.
            DeferApplyAfterRender(seq, path, loaded);
            return;
        }
        ApplyLoadedText(path, loaded); // 소용량: 종전 그대로 즉시 대입(지연 프레임을 끼우지 않는다)
    }

    /// <summary>
    /// A177 ⓐ: 대용량 Text 대입을 "로딩 표시가 담긴 프레임이 제출된 뒤"로 미룬다.
    /// CompositionTarget.Rendering은 매 프레임 렌더 직전에 온다(구독 자체가 프레임 틱을
    /// 보장한다 — A88 맥박 루프에서 확인된 성질). 1틱째는 로딩 표시가 담길 프레임의 렌더
    /// 직전이므로 건너뛰고, 2틱째에 오면 그 프레임은 이미 제출돼 있다 — 그때 대입하면 수 초가
    /// 걸려도 사용자는 로딩 표시를 보고 있다. 창이 최소화돼 틱이 멈추면 대입도 함께 미뤄질 수
    /// 있으나, 보이지 않는 창은 그려질 필요도 없고 복원되면 틱과 함께 이어진다.
    /// 뷰가 내려가면(창 닫기·모듈 전환) Unloaded가 핸들러를 해제해 대입은 조용히 무산된다.
    /// </summary>
    private void DeferApplyAfterRender(int seq, string path, LoadedText loaded)
    {
        // 방어: 직전 보류분이 남아 있으면 먼저 해제(현 라우팅상 열기는 뷰당 1회라 오지 않는 경로).
        if (_pendingApplyHandler is { } previous)
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= previous;

        var ticks = 0;
        void OnRendering(object? sender, object? e)
        {
            if (++ticks < 2) return; // 1틱째 = 로딩 표시 프레임 렌더 직전 — 그 프레임을 막지 않는다
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            _pendingApplyHandler = null;
            if (seq != _openSeq) return; // 그새 다른 파일이 열렸다(방어 — OpenPath와 같은 시퀀스)
            ApplyLoadedText(path, loaded);
        }
        _pendingApplyHandler = OnRendering;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>
    /// 읽기 결과를 에디터에 반영하는 종착점 — A177 이전 OpenPath의 후반부 그대로다(순서 계약
    /// 무변경: 상태 세팅 → Text 대입 → 기준 텍스트 수립 → 더티 해제 → 표시 전환 → 장식 무효화 →
    /// ContentOpened → 트레이). 소용량은 읽기 완료 즉시, 대용량(A177)은 로딩 표시 프레임이
    /// 제출된 뒤에 이 메서드로 들어온다 — 두 경로의 차이는 진입 시점뿐이다.
    /// </summary>
    private void ApplyLoadedText(string path, LoadedText loaded)
    {
        HidePdf(); // PDF → 텍스트 전환 (A16)
        _path = path;
        _encoding = loaded.Encoding;
        _newLine = loaded.NewLine;
        _truncated = loaded.Truncated;
        _originalBytes = loaded.OriginalBytes;   // A113 ⓑ: 원본 바이트(잘림이면 null)
        _lossyAtLoad = loaded.Loss != RoundTripLoss.None;
        _lossyReason = loaded.Loss;
        _diskWriteTimeUtc = loaded.WriteTimeUtc; // A113 ⓓ: 외부 변경 판정의 기준 스탬프
        _diskLength = loaded.Length;

        // A181: 거터(A142 ③) 자리 예약 — 자릿수+1을 왼쪽 패딩으로 확보한다(UpdateEditorPadding
        // 주석 참고). Text 대입보다 먼저 잡아야 랩 계산이 최종 패딩으로 한 번에 끝난다.
        // 대용량(A177)은 장식 자체가 꺼져 있어 예약하지 않는다(0 = 기본 패딩).
        _gutterDigits = loaded.Text.Length > LargeDocumentChars
            ? 0
            : DigitCount(CountLines(loaded.Text)) + 1;
        UpdateEditorPadding();

        _loadingText = true; // 프로그램적 설정 — dirty 아님
        EditorBox.Text = loaded.Text;
        _loadingText = false;
        _textSnapshot = null; // A142 ①ⓑ: TextChanged 무효화와 중복이어도 무해한 방어 — 옛 파일 잔상 금지
        // A113 ⓒ: 더티 판정 기준은 "TextBox가 실제로 보유한 텍스트"의 정규화본이다 — 로드 문자열
        // 대신 셋 직후 값을 쓰는 이유는, TextBox가 줄바꿈('\r') 외에 무언가를 더 손봐도 열자마자
        // 더티가 되는 오탐이 없게 하기 위함(undo 원복 판정의 기준점과도 일치한다).
        // 이 접근이 스냅샷을 새로 띄워 첫 장식 렌더(A115)까지 같은 복사본을 쓴다(A142 ①ⓑ).
        _baselineText = NormalizeNewlines(EditorText);
        _dirtyTimer?.Stop(); // 이전 파일의 보류 중 판정이 새 파일 상태를 건드리지 않게
        EditorBox.IsReadOnly = loaded.Truncated; // 잘린 채 저장되는 사고 방지
        SetDirty(false);

        EditorBox.Visibility = Visibility.Visible;
        _decor.Invalidate(); // A115: 새 문서·표시 전환이 레이아웃에 반영된 뒤 장식을 다시 그린다
        PlaceholderText.Visibility = Visibility.Collapsed;
        FileNameText.Text = Path.GetFileName(path);
        UpdateZoomText(); // A181: _path가 잡혔다 — 하단 바에 현재 배율 표시(항상, 100% 포함)
        _shownPath = path;
        ContentOpened?.Invoke(path); // 셸 동기화 — A22: 셸이 드라이브 줄을 내린다
        TrayStatusChanged?.Invoke(); // A54→A138: 트레이 = "1/1"(텍스트는 페이지 개념 없음)
    }

    // ---------- PDF (A16) ----------

    private PdfPane? _pdfPane; // 지연 생성 — 텍스트만 쓰는 세션에는 만들지 않는다

    private async void OpenPdf(string path)
    {
        var seq = ++_openSeq; // 텍스트 열기와 같은 경쟁 방지 시퀀스 공유

        if (_pdfPane is null)
        {
            _pdfPane = new PdfPane();
            _pdfPane.PageChanged += (current, total) =>
            {
                // A138: 트레이 = 페이지 위치. 스크롤·팬마다 오는 이벤트라 같은 값이면 쏘지 않는다
                // (1차 방어 — 셸 쪽 ComposeKey 선비교가 2차로 재합성을 걸러 주지만, 디스패치 큐잉
                // 자체를 여기서 끊는 쪽이 싸다). 하단 바 텍스트 갱신보다 먼저 둔다 — 아래
                // 같은 문자열 조기 반환이 트레이 갱신까지 삼키면 안 되기 때문.
                if (_pdfCurrentPage != current || _pdfTotalPages != total)
                {
                    _pdfCurrentPage = current;
                    _pdfTotalPages = total;
                    TrayStatusChanged?.Invoke();
                }
                // A148: 드래그 팬은 매 프레임 ViewChanged → PageChanged를 부른다. 페이지 번호가
                // 그대로면 대입하지 않는다(같은 값이어도 TextBlock 대입은 측정·배치를 유발한다).
                var text = total > 0 ? $"{current} / {total}" : string.Empty;
                if (PageInfoText.Text == text) return;
                PageInfoText.Text = text;
            };
            RootGrid.Children.Insert(0, _pdfPane); // 상태바·플레이스홀더보다 뒤(z 순서)
        }

        // 화면 전환: 에디터 내리고 PDF 패널 표시. PDF는 편집 대상이 아니다 — 저장 상태 초기화
        // (A113 ⓐ~ⓓ 상태도 함께 비운다 — PDF에는 어떤 런타임 체크도 적용되지 않는다).
        _path = null;
        _truncated = false;
        _originalBytes = null;
        _lossyAtLoad = false;
        _lossyReason = RoundTripLoss.None;
        _baselineText = string.Empty;
        _dirtyTimer?.Stop();
        SetDirty(false);
        EditorBox.Visibility = Visibility.Collapsed;
        _decor.Invalidate(); // A115: 에디터가 내려갔다 — 다음 레이아웃에서 장식도 걷힌다
        UpdateZoomText(); // A181: PDF는 별개 줌 체계 — 텍스트 배율 표기를 비운다(_path=null)
        PlaceholderText.Visibility = Visibility.Collapsed;
        _pdfPane.Visibility = Visibility.Visible;
        PageInfoText.Visibility = Visibility.Visible;
        PageInfoText.Text = string.Empty;
        FileNameText.Text = Path.GetFileName(path);

        // A49→A145: Fit 조절기는 이제 항상 보이고(텍스트 모드는 비활성 "1/1") PDF에서 4옵션이
        // 활성화된다. 파일이 바뀌면 버튼 표시도 Contain으로 회귀(A30 규칙, 기억 안 함) —
        // 실제 배율 적용은 PdfPane.LoadAsync가 한다.
        _lastFitOption = PdfFitMode.Contain;
        ShowPdfFitState();

        var ok = await _pdfPane.LoadAsync(path); // 실패 다이얼로그는 패널이 띄운다
        if (seq != _openSeq) return;             // 그새 다른 파일이 열렸다
        if (!ok)
        {
            HidePdf();
            FileNameText.Text = "No file open";
            PlaceholderText.Visibility = Visibility.Visible;
            _shownPath = null;
            TrayStatusChanged?.Invoke(); // A54: 열기 실패 → 유휴("DOC")
            return;
        }

        _shownPath = path;
        ContentOpened?.Invoke(path); // 셸 동기화 — A22: 셸이 드라이브 줄을 내린다
        TrayStatusChanged?.Invoke(); // A54→A138: 트레이 = 현재/전체 페이지(LoadAsync가 (1, 전체)를 이미 쐈다)
    }

    /// <summary>PDF 패널을 내린다(텍스트로 전환·열기 실패 시). 비트맵·문서 참조 해제.</summary>
    private void HidePdf()
    {
        if (_pdfPane is null) return;
        _pdfPane.Clear();
        _pdfPane.Visibility = Visibility.Collapsed;
        PageInfoText.Visibility = Visibility.Collapsed;
        ShowTextFitState(); // A145: 숨기지 않고 비활성 "1/1"로 (구 A49의 Collapsed를 대체)
    }

    // ---------- PDF 키보드 스크롤 (A121) ----------

    /// <summary>
    /// A121: ↑/↓·PageUp/PageDown·Home/End를 <b>PDF 표시 중일 때만</b> PdfPane의 세로 스크롤로
    /// 돌린다. 생성자에서 <c>PreviewKeyDown</c>(터널링)으로 걸리므로 PdfPane 안쪽 ListView·
    /// ScrollViewer의 내장 키 처리보다 먼저 도착한다.
    ///
    /// <b>합격선 = 원 기능 불간섭</b>. 아래 게이트를 전부 통과할 때만 소비하고, 하나라도 걸리면
    /// 아무것도 하지 않고 물러난다(Handled를 세우지 않으므로 종전 경로가 그대로 이어진다):
    /// ⓐ PDF 모드일 것 — 텍스트 에디터·4MB 잘림 뷰·빈 화면에서는 즉시 통과라 TextBox의
    ///    캐럿 이동·PgUp/PgDn·Home/End가 손도 타지 않는다(PdfPane은 텍스트 모드에서 Collapsed).
    /// ⓑ <c>e.OriginalSource</c>가 텍스트 입력 컨트롤이면 통과 — 오버레이 필터 입력·F2 인라인
    ///    이름변경(A94)의 커서 이동이 우선이다.
    /// ⓒ 포커스가 텍스트 입력이거나 탐색기 표면(PassThroughTag — 리스트·트리·썸네일)이면 통과.
    ///    판정은 A34 공용 <c>HotkeySupport.ShouldPassThrough</c> 재사용이다(ⓑ와 겹치지만, ⓑ는
    ///    이벤트 원천 · ⓒ는 실제 포커스라 서로 못 잡는 구멍을 메운다). 셸 오버레이는 애초에
    ///    이 뷰의 조상 경로 밖이라 터널링이 닿지도 않는다 — 이 판정은 이중 방어선이다.
    /// ⓓ 수정키(Ctrl·Shift·Alt·Win)가 하나라도 눌려 있으면 통과 — Ctrl+Home 같은 조합을 뺏지 않는다.
    ///
    /// <b>오토리피트는 무시하지 않는다</b>: <c>e.KeyStatus.WasKeyDown</c>을 보지 않으므로 꾹 누르면
    /// 반복 down마다 스크롤이 이어진다(A121 사양). A86 문자 키 3종 세트나 셸의 F11/F12 전이가
    /// 오토리피트를 걸러내는 것과 반대인데, 그쪽은 "토글·상태 전이"라 연사되면 안 되고 이쪽은
    /// "연속 이동"이 목적이기 때문이다.
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        var pane = _pdfPane;
        if (pane is null || pane.Visibility != Visibility.Visible) return; // ⓐ PDF 모드가 아니다
        if (e.OriginalSource is TextBox or PasswordBox or RichEditBox) return; // ⓑ 텍스트 입력이 원 기능
        if (HotkeySupport.ShouldPassThrough(this)) return; // ⓒ 텍스트 입력·탐색기 표면 포커스
        if (IsModifierDown()) return;                      // ⓓ 조합 키는 우리 것이 아니다
        if (pane.TryHandleNavKey(e.Key)) e.Handled = true; // 우리 키 + 스크롤 대상이 있을 때만 소비
    }

    /// <summary>
    /// A121 게이트 ⓓ: 수정키가 하나라도 눌려 있는지. 조회 방식은 이 파일의 Tab 처리
    /// (OnEditorKeyDown)·셸의 Alt 판정과 같은 InputKeyboardSource 경로다.
    /// Windows 키만 좌/우 별도 조회 — 합산 VirtualKey가 없다(Ctrl·Shift·Alt는 합산 키가 있다).
    /// </summary>
    private static bool IsModifierDown() =>
        IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.Menu)
        || IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows);

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ---------- PDF 맞춤 보기 (A49 — A30 규격) ----------

    /// <summary>
    /// A30 규격: Fit 버튼 본체가 표시·재적용할 마지막 핏 옵션. A83 이후 100%도 플라이아웃
    /// 옵션이라 ActualSize까지 들어온다(1:1 별도 버튼은 A111에서 없어졌다).
    /// 기억하지 않는다 — 파일이 바뀌면 Contain으로 회귀(A30 규칙).
    /// </summary>
    private PdfFitMode _lastFitOption = PdfFitMode.Contain;

    /// <summary>
    /// A145: PDF 모드 진입 — Fit 조절기(본체 + 화살표) 활성. 본체 내용·툴팁은 UpdateFitButton()이
    /// 마지막 옵션으로 맞춘다. 짝은 아래 ShowTextFitState() — 활성/비활성 전환은 이 한 쌍만 한다.
    /// </summary>
    private void ShowPdfFitState()
    {
        FitButton.IsEnabled = true;
        FitOptionsButton.IsEnabled = true;
        ToolTipService.SetToolTip(FitOptionsButton, "Fit options"); // 형제 모듈 XAML 고정값과 동일
        UpdateFitButton();
    }

    /// <summary>
    /// A145: 텍스트 에디터·빈 화면 — 표시만 하는 비활성 "1/1"(텍스트는 확대/축소 대상이 없어
    /// 옵션이 무의미하다 — 부록 B 67, 1차는 표시만). 구 A49의 Collapsed 분기를 대체한다 —
    /// 조절기가 항상 보여 문서 바 폭이 모드 전환에 출렁이지 않는다.
    /// 비활성이라 A·F 키도 HotkeySupport의 IsEnabled 게이트에서 통과된다(타이핑 우선 —
    /// 종전 Visibility 게이트와 같은 효과). 표기 "1/1"은 트레이(A138)의 텍스트 문서 표기와
    /// 같은 값이다(페이지 1/1 — Fit 모드 아이콘이 아니라 고정 상태 표기).
    /// 실기기 확인 포인트: WinUI는 비활성 컨트롤 위에서 툴팁이 안 뜰 수 있다 — 안 뜨면
    /// 표기("1/1")만으로 충분한지 사용자 판단을 받는다.
    /// </summary>
    private void ShowTextFitState()
    {
        FitButton.IsEnabled = false;
        FitOptionsButton.IsEnabled = false;
        FitButton.Content = new TextBlock { Text = "1/1", FontSize = 13 };
        ToolTipService.SetToolTip(FitButton, "Text documents are always 1:1");
        ToolTipService.SetToolTip(FitOptionsButton, "Text documents are always 1:1");
    }

    /// <summary>
    /// A30 규격: Fit 버튼 본체 내용(4옵션 아이콘)과 툴팁을 마지막 옵션에 맞춘다.
    /// A144: 본체가 SplitButton에서 일반 Button(32×32)이 됐다 — 화살표는 별도
    /// DropDownButton(FitOptionsButton, 플라이아웃 전담·A34 키 없음)이라 이 메서드는
    /// 종전대로 본체(FitButton)만 만진다. PDF 모드에서만 불린다(텍스트 모드는 ShowTextFitState).
    /// A143: 100%도 아이콘이 됐다 — 종전 "1:1" 텍스트(FontSize 13) 대신 PathIcon(부록 B 69).
    /// ⚠️ v0.174.1: PathIcon 인스턴스(UIElement)만이 아니라 **Geometry도 공유 금지** — WinUI
    /// Geometry는 부모가 하나뿐이라 공유 인스턴스를 PathIcon.Data에 걸면 XamlParseException
    /// ("Failed to assign to property")으로 앱이 죽는다(실기기 크래시 실사례 — 종전엔 App.xaml
    /// 공유 리소스를 봤다). 호출마다 BuildActualSizeIconGeometry()로 새로 만든다.
    /// </summary>
    private void UpdateFitButton()
    {
        (object content, string tip) = _lastFitOption switch
        {
            PdfFitMode.FitWidth =>
                ((object)new FontIcon { Glyph = "\uE8AB", FontSize = 18 }, "Fit width"),
            PdfFitMode.FitHeight =>
                (new FontIcon { Glyph = "\uE8CB", FontSize = 18 }, "Fit height"),
            PdfFitMode.ActualSize => (new PathIcon
            {
                Data = BuildActualSizeIconGeometry(),
            }, "Actual size"),
            _ => (new FontIcon { Glyph = "\uE9A6", FontSize = 18 },
                "Contain - the whole page fits, never enlarged"),
        };
        FitButton.Content = content;
        ToolTipService.SetToolTip(FitButton, FitTip(tip)); // A34: 표기는 키 상수에서
    }

    /// <summary>
    /// A143/v0.174.1: 100% 아이콘 도형(16x16 좌표계 — PathIcon은 스케일하지 않는다). 도형 6개 =
    /// 왼쪽 1(깃발+기둥/밑변)·콜론 점 2개·오른쪽 1(깃발+기둥/밑변). 호출마다 새 인스턴스를 만든다
    /// (Geometry 공유 금지 — 위 UpdateFitButton 주석). 좌표를 바꾸면 이 파일 XAML의 인라인 Data
    /// 문자열과 형제 두 모듈(이미지·영상)의 같은 두 곳까지 총 6곳을 함께 고칠 것.
    /// </summary>
    private static Geometry BuildActualSizeIconGeometry()
    {
        static PathFigure Fig(double sx, double sy, params (double X, double Y)[] points)
        {
            var figure = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(sx, sy),
                IsClosed = true,
                IsFilled = true,
                Segments = new PathSegmentCollection(),
            };
            foreach ((double x, double y) in points)
                figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x, y) });
            return figure;
        }

        var geometry = new PathGeometry { Figures = new PathFigureCollection() };
        geometry.Figures.Add(Fig(2.3, 5.0, (4.2, 3.0), (5.0, 3.0), (5.0, 11.4), (3.5, 11.4), (3.5, 5.0)));
        geometry.Figures.Add(Fig(1.8, 11.4, (6.7, 11.4), (6.7, 13.0), (1.8, 13.0)));
        geometry.Figures.Add(Fig(7.4, 6.0, (8.8, 6.0), (8.8, 7.4), (7.4, 7.4)));
        geometry.Figures.Add(Fig(7.4, 9.6, (8.8, 9.6), (8.8, 11.0), (7.4, 11.0)));
        geometry.Figures.Add(Fig(10.5, 5.0, (12.4, 3.0), (13.2, 3.0), (13.2, 11.4), (11.7, 11.4), (11.7, 5.0)));
        geometry.Figures.Add(Fig(10.0, 11.4, (14.9, 11.4), (14.9, 13.0), (10.0, 13.0)));
        return geometry;
    }

    /// <summary>
    /// 본체 툴팁 = "지금 표시 중인 옵션 (F) · 100% (A)" — 1:1 버튼이 사라져도 A 키 표기가
    /// 남게 병합한다(A111). 두 표기 모두 키 상수에서 조립한다(A34 표기 규칙).
    /// </summary>
    private static string FitTip(string description) =>
        $"{HotkeySupport.Tip(description, FitKey)} · {HotkeySupport.Tip("100%", ActualSizeKey)}";

    /// <summary>플라이아웃에서 옵션 선택 — 즉시 적용하고 버튼 표시를 그 옵션으로 바꾼다.</summary>
    private void SelectFitOption(PdfFitMode option)
    {
        _lastFitOption = option;
        UpdateFitButton();
        _pdfPane?.ApplyFit(option);
    }

    /// <summary>A30 규격: 본체 클릭 = 버튼에 표시된 마지막 옵션 재적용
    /// (A144: SplitButton 본체 → 일반 Button — 시그니처만 RoutedEventArgs로 바뀌었다).
    /// 텍스트 모드에서는 버튼이 비활성(A145)이라 이 핸들러에 못 온다.</summary>
    private void OnFitClicked(object sender, RoutedEventArgs e) =>
        _pdfPane?.ApplyFit(_lastFitOption);

    /// <summary>플라이아웃 100%·A 키(A34) 공용 경로 — 구 1:1 버튼 자리(A111).</summary>
    private void OnFitActualSizeClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(PdfFitMode.ActualSize);

    private void OnFitContainClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(PdfFitMode.Contain);

    private void OnFitWidthClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(PdfFitMode.FitWidth);

    private void OnFitHeightClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(PdfFitMode.FitHeight);

    // ---------- 인코딩 감지·보존 (A37) ----------

    /// <summary>열 때 감지해 저장 시 그대로 재현하는 인코딩 종류.</summary>
    private enum TextEncodingKind
    {
        Utf8,       // BOM 없는 UTF-8
        Utf8Bom,    // EF BB BF
        Utf16LeBom, // FF FE
        Utf16BeBom, // FE FF
        Cp949,      // 레거시 한글 (BOM 없음, UTF-8 해석 실패 시)
    }

    /// <summary>
    /// A113 ⓑ: 로드 시 1회 판정하는 라운드트립 손실 사유. None이 아니면 "무수정 저장조차 원본
    /// 바이트를 재현하지 못한다"는 뜻이라, 저장 직전에 이 사유로 예고 대화상자를 띄운다.
    /// </summary>
    private enum RoundTripLoss
    {
        None,          // 무수정 저장 = 원본과 바이트 동일
        MixedNewlines, // 줄바꿈이 섞여 있거나 재현 불가 — 저장하면 감지한 한 스타일로 통일된다
        DecodingLoss,  // 디코딩이 대체 문자를 만들어 원본 바이트를 되쓸 수 없다
    }

    private sealed record LoadedText(
        string Text, TextEncodingKind Encoding, string NewLine, bool Truncated,
        byte[]? OriginalBytes, RoundTripLoss Loss, DateTime WriteTimeUtc, long Length);

    /// <summary>
    /// BOM이 있으면 그대로, 없으면 엄격 UTF-8로 시도하고 깨질 때만 CP949로 해석한다
    /// (동영상 자막 SubtitleCharset과 같은 접근 — 모듈 간 참조 금지라 별도 구현).
    /// 감지한 인코딩·줄바꿈 스타일은 저장 시 보존을 위해 함께 반환한다(A37).
    /// </summary>
    private static LoadedText ReadTextSmart(string path)
    {
        using var stream = File.OpenRead(path);
        var truncated = stream.Length > MaxBytes;
        var bytes = new byte[Math.Min(stream.Length, MaxBytes)];
        stream.ReadExactly(bytes);

        string text;
        TextEncodingKind kind;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            kind = TextEncodingKind.Utf8Bom;
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            kind = TextEncodingKind.Utf16LeBom;
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            kind = TextEncodingKind.Utf16BeBom;
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(bytes);
                kind = TextEncodingKind.Utf8;
            }
            catch (DecoderFallbackException)
            {
                kind = TextEncodingKind.Cp949;
                text = Encoding.GetEncoding(949).GetString(bytes); // 레거시 한글(CP949)
            }
        }

        // 줄바꿈 스타일: 첫 줄바꿈 기준(혼합 파일은 첫 스타일로 통일 저장 — 단순함 우선).
        // 줄바꿈이 없는 파일은 Windows 기본 CRLF.
        var lf = text.IndexOf('\n');
        var newline = lf > 0 && text[lf - 1] == '\r' ? "\r\n" : lf >= 0 ? "\n" : "\r\n";

        // A113 ⓑ: 라운드트립 판정 — "이 텍스트를 무수정 저장하면 원본 바이트가 그대로 나오는가"를
        // 저장 경로와 같은 변환(정규화 → 줄바꿈 복원 → EncodeStrict)으로 여기서 1회만 계산해 둔다
        // (저장 시점에는 이 결과만 본다 — 워커라 UI를 막지 않는다). 잘린 파일은 읽기 전용이라
        // 저장 자체가 없으므로 원본 보관·판정 모두 생략한다.
        byte[]? originalBytes = null;
        var loss = RoundTripLoss.None;
        if (!truncated)
        {
            originalBytes = bytes; // 참조 유지 — 이미 전부 읽었다(추가 복사 없음)
            var restored = NormalizeNewlines(text);
            if (newline != "\n") restored = restored.Replace("\n", newline);
            try
            {
                if (!EncodeStrict(restored, kind).SequenceEqual(bytes))
                    loss = restored == text ? RoundTripLoss.DecodingLoss : RoundTripLoss.MixedNewlines;
            }
            catch (EncoderFallbackException)
            {
                loss = RoundTripLoss.DecodingLoss; // 디코드가 만든 대체 문자를 CP949로 되쓸 수 없다
            }
        }

        // A113 ⓓ: 외부 변경 판정의 기준 스탬프. 읽기 스트림이 아직 열려 있어(쓰기 공유 차단)
        // 지금 조회한 값은 방금 읽은 내용과 일치한다.
        var stamp = new FileInfo(path);
        var writeTimeUtc = stamp.LastWriteTimeUtc;
        var length = stamp.Length;

        if (truncated)
            text += $"\n\n--- Showing the first {MaxBytes / 1024 / 1024} MB of this file (read-only) ---";
        return new LoadedText(text, kind, newline, truncated, originalBytes, loss, writeTimeUtc, length);
    }

    /// <summary>저장용 인코드. CP949로 표현 못 하는 문자는 예외로 알린다(무단 '?' 치환 방지).</summary>
    private static byte[] EncodeStrict(string text, TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8Bom =>
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
        TextEncodingKind.Utf16LeBom =>
            [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
        TextEncodingKind.Utf16BeBom =>
            [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
        TextEncodingKind.Cp949 => Encoding.GetEncoding(949,
            EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback).GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text), // BOM 없는 UTF-8
    };

    /// <summary>
    /// 비교·저장의 공통 기준(\n)으로 줄바꿈 정규화(A113). WinUI TextBox는 줄바꿈을 '\r'로
    /// 정규화하므로, 에디터 텍스트·로드 텍스트 어느 쪽이든 이걸 거치면 같은 기준에서 만난다.
    /// A142 ①ⓒ: 결과는 종전 <c>Replace("\r\n","\n").Replace('\r','\n')</c>과 동일하되 단일
    /// 패스로 처리한다 — 4MB급에서 중간 문자열 복사 2회 → 최대 1회('\r'가 없으면 0회).
    /// A113 ⓒ의 비교 계약 자체는 무변경이다(복사 횟수만 줄인다).
    /// </summary>
    private static string NormalizeNewlines(string text)
    {
        if (!text.Contains('\r')) return text; // 로드 텍스트(\n 전용)·개행 없는 파일의 흔한 빠른 경로
        var chars = new char[text.Length];
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                chars[n++] = '\n';
                if (i + 1 < text.Length && text[i + 1] == '\n') i++; // CRLF는 한 줄바꿈
            }
            else
            {
                chars[n++] = c;
            }
        }
        return new string(chars, 0, n);
    }

    // ---------- 편집·저장 (A37) ----------

    /// <summary>ⓒ 디바운스 간격 — 길이가 같은 편집(치환·undo 원복)의 내용 비교를 이만큼 미룬다.</summary>
    private const int DirtyDebounceMs = 250;

    private DispatcherTimer? _dirtyTimer; // A113 ⓒ: UI 스레드 타이머(다른 뷰들과 같은 방식)

    /// <summary>지연 생성 — 길이가 같은 편집이 한 번도 없는 세션에는 만들지 않는다.</summary>
    private DispatcherTimer DirtyTimer => _dirtyTimer ??= CreateDirtyTimer();

    private DispatcherTimer CreateDirtyTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DirtyDebounceMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // 반복 타이머 — 1회 판정용이라 즉시 멈춘다
            if (_loadingText || _path is null || _truncated) return; // 판정 대상이 아니다
            SetDirty(!EditorMatchesBaseline());
        };
        return timer;
    }

    /// <summary>
    /// A113 ⓒ: 더티 = "기준 텍스트(로드·저장 시점)와 실제로 다른가". 길이가 다르면 그 자체가
    /// 증거라 즉시 확정한다 — 대용량(잘림 한계 4MB 직전) 파일에서 키 입력마다 전체 비교를 하지
    /// 않기 위한 빠른 경로. 길이가 같으면(1글자 치환, undo로 원복) 250ms 조용해진 뒤 내용 비교로
    /// 판정한다 — 그래서 undo로 원본과 같아지면 ●가 꺼진다(사양). 에디터 줄바꿈('\r')과
    /// 기준('\n')은 1글자끼리 대응하므로 원시 길이 비교가 유효하다.
    /// </summary>
    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        _textSnapshot = null; // A142 ①ⓑ: 어떤 편집이든 스냅샷부터 무효화 — 조기 반환보다 먼저
        if (_loadingText || _path is null || _truncated) return;
        if (EditorText.Length != _baselineText.Length)
        {
            _dirtyTimer?.Stop(); // 보류 중 판정 불필요 — 결과가 이미 확정이다
            SetDirty(true);
            return;
        }
        DirtyTimer.Stop(); // 반복 타이머 — Stop 후 Start로 확실히 되감는다(전 모듈 관용구)
        DirtyTimer.Start();
    }

    /// <summary>ⓒ 판정 본체: 에디터 내용을 \n 정규화해 기준 텍스트와 비교한다.</summary>
    private bool EditorMatchesBaseline() => NormalizeNewlines(EditorText) == _baselineText;

    /// <summary>ⓒ 즉시 재판정(디바운스 없이) — 저장 성공 재기준화 직후에 쓴다.</summary>
    private void RecomputeDirty()
    {
        _dirtyTimer?.Stop();
        SetDirty(!EditorMatchesBaseline());
    }

    /// <summary>
    /// 보류 중인 ⓒ 디바운스 판정이 있으면 지금 확정한다 — 치환 편집 직후 250ms 안에 저장·닫기
    /// 판단이 이루어질 때 "변경 없음"으로 새지 않게 한다(A113 ① 점검에서 발견한 구멍).
    /// </summary>
    private void SettlePendingDirtyCheck()
    {
        if (_dirtyTimer is { IsEnabled: true }) RecomputeDirty();
    }

    /// <summary>Tab이 포커스 이동 대신 탭 문자를 넣게 한다(에디터 기본기). Shift+Tab은 포커스 이동 유지.</summary>
    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || EditorBox.IsReadOnly) return;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

        e.Handled = true;
        var pos = EditorBox.SelectionStart;
        EditorBox.SelectedText = "\t"; // 선택 영역을 탭으로 치환(없으면 삽입)
        EditorBox.SelectionStart = pos + 1;
        EditorBox.SelectionLength = 0;
    }

    /// <summary>양방향(A113 ⓒ): 켜기만이 아니라 undo 원복·재판정으로 꺼지기도 한다.</summary>
    private void SetDirty(bool dirty)
    {
        ModifiedText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = dirty;
        if (_dirty == dirty) return; // 값 무변경(파일 전환 직후 UI 초기화, ⓒ 재판정의 재확인) — 셸 통지 생략
        _dirty = dirty;
        UnsavedChanged?.Invoke(dirty);
    }

    /// <summary>
    /// 현재 내용을 원본 인코딩·줄바꿈으로 저장한다. true = 저장 완료(또는 저장할 것 없음).
    /// A113 순서: ⓓ 외부 변경 확인 → ⓑ 라운드트립 손실 예고 → 인코딩(CP949 불가 시 UTF-8 전환
    /// 확인) → 쓰기 → ⓐ 재검증(실패 = Retry / Save as... / Cancel). 어느 대화상자에서든 취소는
    /// 저장 전체 취소다(false — 더티 유지, ConfirmCloseAsync 경유면 닫기도 함께 취소된다).
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (_saving) return false; // 저장 흐름이 이미 진행 중(대화상자 포함) — 완료를 주장하지 않는다
        if (_path is null || _truncated) return true; // 잘림·PDF·빈 화면 — 저장 대상이 없다(ⓐ~ⓓ 비적용)
        SettlePendingDirtyCheck(); // 250ms 창 안의 치환 편집이 "저장할 것 없음"으로 새지 않게
        if (!_dirty) return true;

        _saving = true;
        try
        {
            return await SaveCoreAsync();
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task<bool> SaveCoreAsync()
    {
        if (_path is not { } originalPath) return true; // SaveAsync가 걸렀다 — 방어

        // WinUI TextBox는 줄바꿈을 '\r'로 정규화한다 — 기준(\n)으로 맞춘 뒤 원본 스타일로 되돌린다.
        var normalized = NormalizeNewlines(EditorText); // A142 ①ⓑ: 마지막 편집 후 스냅샷 재사용

        var text = _newLine == "\n" ? normalized : normalized.Replace("\n", _newLine);

        // ⓓ 외부 변경 감지: 열 때(또는 직전 저장 때) 기록한 스탬프와 다르면 덮어쓸지 먼저 묻는다.
        if (DiskChangedSinceLoad() && !await ConfirmOverwriteExternalChangeAsync()) return false;

        // ⓑ 라운드트립 손실 예고: 로드 시 1회 판정해 둔 결과 — 원본 바이트를 쥔 경우에만 성립한다.
        if (_lossyAtLoad && _originalBytes is not null && !await ConfirmNormalizeAsync()) return false;

        byte[] bytes;
        try
        {
            bytes = EncodeStrict(text, _encoding);
        }
        catch (EncoderFallbackException)
        {
            if (!await ConfirmUtf8FallbackAsync()) return false;
            _encoding = TextEncodingKind.Utf8; // 이후 저장도 UTF-8
            bytes = EncodeStrict(text, _encoding);
        }

        // 쓰기 + ⓐ 재검증. 실패하면 같은 경로 재시도(Retry) 또는 새 경로(Save as...)로 반복한다.
        var path = originalPath;
        var savedAs = false;
        while (true)
        {
            SaveStamp stamp;
            try
            {
                var target = path; // 워커 클로저가 이번 회차의 경로를 읽도록 고정
                stamp = await Worker.Run(_ => WriteAndVerify(target, bytes));
            }
            catch (OperationCanceledException)
            {
                return false; // 뷰가 내려가는 중 — 저장 완료를 주장하지 않는다
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Save failed", ex.Message);
                return false;
            }

            if (stamp.Verified)
            {
                CommitSave(path, savedAs, bytes, normalized, stamp);
                return true;
            }

            // ⓐ 실패: 디스크가 의심 상태고 버퍼가 정본이다 — 더티를 유지한 채 선택지를 준다.
            switch (await ShowVerifyFailedAsync())
            {
                case ContentDialogResult.Primary: // Retry — 같은 경로에 쓰기+재검증 재시도
                    continue;
                case ContentDialogResult.Secondary: // Save as... — 새 경로로
                    if (await PickSaveAsPathAsync() is not { } picked) return false; // 피커 취소 = 저장 취소
                    path = picked;
                    savedAs = true;
                    continue;
                default: // Cancel — 더티 유지
                    return false;
            }
        }
    }

    /// <summary>ⓐ 쓰기+재검증 결과. Verified=false면 디스크가 의심 상태다(버퍼가 정본 — 더티 유지).</summary>
    private sealed record SaveStamp(bool Verified, DateTime WriteTimeUtc, long Length);

    /// <summary>
    /// 워커에서 쓰고(WriteAllBytes — 반환 시점에 핸들이 닫혀 있다) 곧바로 다시 읽어 쓴 바이트와
    /// 대조한다(A113 ⓐ). ⓓ 스탬프 재기록용 수정 시각·크기도 같은 왕복에서 조회해 돌려준다.
    /// </summary>
    private static SaveStamp WriteAndVerify(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        var verified = File.ReadAllBytes(path).SequenceEqual(bytes);
        var info = new FileInfo(path);
        return new SaveStamp(verified, info.LastWriteTimeUtc, info.Length);
    }

    /// <summary>
    /// 저장 성공(ⓐ 통과) 후 재기준화: 원본 바이트·기준 텍스트·손실 플래그·ⓓ 스탬프를 방금 쓴
    /// 상태로 다시 잡는다 — 이걸 빠뜨리면 다음 저장마다 ⓓ가 "외부 변경"을 오탐한다.
    /// Save as...로 경로가 바뀌었으면 편집 대상·상태바·셸 통지(기존 배선)도 새 경로로 잇는다.
    /// </summary>
    private void CommitSave(string path, bool savedAs, byte[] bytes, string normalizedText, SaveStamp stamp)
    {
        if (!savedAs && path != _path) return; // 그새 다른 파일이 열렸다 — 새 파일 상태를 덮지 않는다

        if (path != _path)
        {
            _path = path;      // Save as... — 이후 편집·저장은 새 파일이 대상
            _shownPath = path; // 트레이 표기(A54)도 새 파일 기준
            FileNameText.Text = Path.GetFileName(path);
            ContentOpened?.Invoke(path); // 셸 동기화 — 기준 경로·드라이브 줄·오버레이(기존 배선)
        }

        _originalBytes = bytes;         // ⓑ: 이제 디스크의 원본 = 방금 쓴 바이트
        _lossyAtLoad = false;
        _lossyReason = RoundTripLoss.None;
        _baselineText = normalizedText; // ⓒ: 기준 텍스트 = 저장한 그 내용
        _diskWriteTimeUtc = stamp.WriteTimeUtc; // ⓓ: 스탬프 재기록
        _diskLength = stamp.Length;

        // ⓒ: 쓰는 동안 새 입력이 있었으면 기준과 다르다 — 무조건 끄지 않고 내용 비교로 재판정한다
        // (종전에는 저장 완료가 무조건 더티 해제였다 — 그 사이 입력이 ● 없이 새는 창이 있었다).
        RecomputeDirty();

        // A137: 저장 성공 1회 통지 — 저장으로 파일 크기가 바뀌면 셸이 작업표시줄 32px 아이콘의
        // 용량 표기를 다시 그린다(타이핑 중 실시간 갱신은 하지 않는다 — 부록 B 69 확정. 여기는
        // A113 재기준화 지점이라 "저장 성공 시 1회"가 정확히 성립하는 자리다). Save as...(경로
        // 변경)도 이 한 번으로 충분하고, 트레이 값(A138 페이지)은 저장으로 안 변하므로 셸의
        // ComposeKey 선비교가 트레이 재합성을 걸러 준다 — 종전의 savedAs 한정 발화를 대체한다.
        TrayStatusChanged?.Invoke();
    }

    /// <summary>
    /// A113 ⓓ: 디스크의 파일이 연 뒤(또는 직전 저장 뒤)에 바뀌었는지 — 수정 시각·크기 스탬프 비교.
    /// 파일이 사라졌거나 조회가 실패해도 "외부에서 무슨 일이 있었다"이므로 변경으로 친다.
    /// </summary>
    private bool DiskChangedSinceLoad()
    {
        if (_path is null) return false;
        try
        {
            var info = new FileInfo(_path);
            return !info.Exists
                || info.LastWriteTimeUtc != _diskWriteTimeUtc
                || info.Length != _diskLength;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>ⓓ: 외부 변경을 안고 덮어쓸지 확인. 파괴적이라 기본 버튼은 Cancel(강행 금지 원칙).</summary>
    private async Task<bool> ConfirmOverwriteExternalChangeAsync()
    {
        if (XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            Title = "File changed on disk",
            Content = $"{Path.GetFileName(_path)} was changed outside this editor after it was opened.\n"
                      + "Overwrite it with the text in this editor?",
            PrimaryButtonText = "Overwrite",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>ⓑ: 저장이 원본을 정규화(줄바꿈 통일 또는 문자 대체)함을 예고하고 진행 여부를 묻는다.</summary>
    private async Task<bool> ConfirmNormalizeAsync()
    {
        if (XamlRoot is null) return false;
        var reason = _lossyReason == RoundTripLoss.MixedNewlines
            ? "The original line endings can't be preserved exactly - saving will write all line breaks as "
              + (_newLine == "\r\n" ? "CRLF" : "LF") + "."
            : "Some bytes could not be decoded exactly when the file was opened - saving will write the "
              + "replacement characters shown in the editor instead of the original bytes.";
        var dialog = new ContentDialog
        {
            Title = "Saving will normalize this file",
            Content = reason,
            PrimaryButtonText = "Save anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>ⓐ 실패 알림: Retry / Save as... / Cancel. XamlRoot가 없으면 None(취소 취급).</summary>
    private async Task<ContentDialogResult> ShowVerifyFailedAsync()
    {
        if (XamlRoot is null) return ContentDialogResult.None;
        var dialog = new ContentDialog
        {
            Title = "Save verification failed",
            Content = "The file on disk does not match what was just written - the disk copy may be "
                      + "incomplete or altered.\nYour text is kept in the editor either way.",
            PrimaryButtonText = "Retry",
            SecondaryButtonText = "Save as...",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync();
    }

    /// <summary>
    /// ⓐ의 "Save as..." — FileSavePicker(ArchiveView 피커와 같은 InitializeWithWindow+GetHwnd 패턴).
    /// 확장자 목록은 현재 파일의 확장자 1개, 제안 파일명은 현재 파일명(확장자는 피커가 붙인다).
    /// null = 사용자 취소(저장 전체 취소).
    /// </summary>
    private async Task<string?> PickSaveAsPathAsync()
    {
        if (_path is null) return null;
        var ext = Path.GetExtension(_path);
        if (string.IsNullOrEmpty(ext)) ext = ".txt"; // 확장자 없는 경로 방어(현행 라우팅상 오지 않는다)
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(_path),
        };
        picker.FileTypeChoices.Add(ext.TrimStart('.').ToUpperInvariant() + " file", new List<string> { ext });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetHwnd());
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>피커 초기화용 창 핸들. Window 객체 없이 XamlRoot 경유로 얻는다(ArchiveView와 동일).</summary>
    private nint GetHwnd()
    {
        var environment = XamlRoot?.ContentIslandEnvironment
            ?? throw new InvalidOperationException("Cannot determine the window handle.");
        return Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
    }

    /// <summary>CP949 원본에 CP949로 못 쓰는 문자가 들어왔을 때: UTF-8 전환 확인.</summary>
    private async Task<bool> ConfirmUtf8FallbackAsync()
    {
        if (XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            Title = "Encoding change required",
            Content = "Some characters can't be saved in the file's original encoding (CP949).\n"
                      + "Save as UTF-8 instead?",
            PrimaryButtonText = "Save as UTF-8",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void OnSaveButtonClick(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private void OnSaveInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = SaveAsync();
    }

    // ---------- 미저장 가드 (A37 — ICloseGuard) ----------

    /// <summary>
    /// 셸(창 닫기·뷰 교체)이 가드 필요 여부를 묻는 값. 보류 중인 ⓒ 디바운스 판정이 있으면 먼저
    /// 확정한다 — 치환 편집 직후 250ms 안의 닫기가 "변경 없음"으로 새지 않게(A113 ① 점검 수리).
    /// </summary>
    public bool HasUnsavedChanges
    {
        get
        {
            SettlePendingDirtyCheck();
            return _dirty;
        }
    }

    /// <summary>저장/버리기/취소 확인. 셸이 뷰 교체·창 닫기 전에 부르고, 뷰 내부 열기도 직접 부른다.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!HasUnsavedChanges) return true; // 보류 중 ⓒ 판정 확정 포함
        if (_saving) return false; // 저장 흐름의 대화상자가 떠 있다 — ContentDialog는 동시 1개(닫기 보류)
        if (XamlRoot is null) return true; // 다이얼로그를 띄울 수 없으면 막지 않는다

        var dialog = new ContentDialog
        {
            Title = "Unsaved changes",
            Content = $"Save changes to {Path.GetFileName(_path)}?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveAsync(), // 저장 실패·취소면 닫기도 취소
            ContentDialogResult.Secondary => true,            // 버리기
            _ => false,                                       // 취소
        };
    }

    // A99: 모듈 열기 버튼·O 키·파일 대화상자(PickAndOpenAsync)는 제거 — 파일 열기는
    // 셸 S4 'Open file'(A90)로 일원화됐다(미저장 가드는 셸이 열기 전에 통과시킨다 — A37).
    // 드래그&드롭은 종전대로 창 수준(MainWindow)에서 확장자 라우팅으로 일괄 처리한다.

    // A151: 전체화면 토글(ToggleFullScreen·⛶ 버튼·F11/Esc 액셀러레이터)은 전부 제거 —
    // 전체화면은 셸의 3단 모드 체계(MainWindow — Enter 순환·Alt+Enter·Esc·모드 버튼)가 담당한다.
    // 편집 중 Enter는 줄바꿈이 우선이라 순환하지 않는다(셸의 텍스트 입력 통과 — A151 ④ⓐ).

    // ---------- 하단 바 버튼 핫키 (A34) ----------

    /// <summary>Fit 키 — 툴팁 표기(UpdateFitButton)와 액셀러레이터가 이 한 값을 함께 쓴다.</summary>
    private const VirtualKey FitKey = VirtualKey.F;

    /// <summary>
    /// 100%(1:1) 키 — A111에서 1:1 버튼이 사라진 뒤로도 A는 그대로 100% 적용이다(A107 확정:
    /// 문자 핫키 전부 유지). 대상만 Fit 버튼의 100% 옵션 적용 액션으로 옮겼다.
    /// </summary>
    private const VirtualKey ActualSizeKey = VirtualKey.A;

    /// <summary>
    /// A34: 하단 바 버튼에 단독 문자 키를 걸고 툴팁 "(키)" 표기까지 같은 호출에서 만든다.
    /// **이 모듈은 에디터(TextBox)가 본문**이라 통과 규칙이 특히 중요하다 — 타이핑 중에는
    /// HotkeySupport가 A·F를 삼키지 않고 글자를 그대로 흘려보낸다(A32/A84 규칙 재사용).
    /// 100%(A)·Fit(F)은 PDF에서만 활성인 Fit 본체(FitButton)에 걸리므로, 텍스트 모드
    /// (A145에서 Collapsed → 비활성 "1/1"로 바뀌었다)에서는 키도 동작하지 않는다 —
    /// HotkeySupport.Register의 IsEnabled 게이트가 종전 Visibility 게이트와 같은 효과를 낸다.
    /// 저장은 Ctrl+S 그대로(A84의 유일한 Ctrl 예외) — XAML 액셀러레이터에 남겨 둔다.
    /// </summary>
    private void SetupHotkeys()
    {
        HotkeySupport.Register(this, FitButton, ActualSizeKey,
            () => SelectFitOption(PdfFitMode.ActualSize));
        HotkeySupport.Register(this, FitButton, FitKey, () => _pdfPane?.ApplyFit(_lastFitOption));
        ShowTextFitState(); // A145: 초기 상태(파일 없음)도 텍스트 상태와 같은 비활성 "1/1"
    }
}
