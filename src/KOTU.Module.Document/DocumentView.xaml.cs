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
/// A189: 무제 문서(New text file)는 <c>_path</c>가 null이어도 편집 대상이다 — 구분 표지는
/// <c>_untitled</c>(필드 주석 참고), 첫 저장이 Save as 피커로 경로를 확정한다.
/// <b>런타임 정합성 체크(A113 ⓐ~ⓓ)</b> — 강행 금지, 항상 사용자에게 선택권을 준다:
/// ⓐ 저장 직후 파일을 다시 읽어 쓴 바이트와 대조(실패 = Retry/Save as.../Cancel),
/// ⓑ 로드 시 라운드트립 판정(무수정 저장이 원본 바이트를 재현 못 하면 저장 전에 예고),
/// ⓒ 더티 = 기준 텍스트와의 실제 내용 비교(길이 우선 + 250ms 디바운스 — undo 원복이면 ●가 꺼진다),
/// ⓓ 저장 직전 디스크 스탬프(수정 시각·크기) 대조로 외부 변경 검출. 전부 잘림·PDF에는 비적용.
/// A211 배치 3(v0.222.0): 인쇄 공급자(<see cref="IPrintPageProvider"/>) — PDF 갈래부터. 보고 있는
/// PDF의 전 페이지를 지연 렌더로 셸 PrintHost에 공급한다(Ctrl+P·하단 바 버튼. 텍스트/마크다운
/// 갈래는 배치 4·5 — 갈래 분기 설계는 아래 "인쇄" 절 주석이 정본).
/// </summary>
public sealed partial class DocumentView : UserControl,
    IContentStateSource, IBottomBarProvider, IDriveStripHost, ICloseGuard, ITrayStatusProvider,
    IUntitledContentSource, IPrintPageProvider
{
    /// <summary>파일을 열면 셸에 알린다(빈 상태 탐색기 내림·오버레이 기준 갱신).</summary>
    public event Action<string>? ContentOpened;

    /// <summary>A189: 무제 문서로 에디터에 진입했다(IUntitledContentSource) — 경로가 없어
    /// ContentOpened를 못 쓴다. 셸은 이걸로 빈 상태 탐색기를 내리고 제목을 무제 표기로 바꾼다.</summary>
    public event Action? UntitledOpened;

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

    /// <summary>
    /// A189: 무제 문서 표지. <c>_path == null</c>은 종전부터 "PDF 뷰 전용/빈 화면"(저장 경로
    /// 자체가 없다 — A113 편집 범위 주석)의 표지로도 쓰여서, "경로는 없지만 편집 가능"인 상태는
    /// 이 플래그가 유일하게 구분한다(상태 enum 대신 bool — <c>_truncated</c>류의 기존 표지 관용구.
    /// 두 축의 조합: _path 있음 = 파일 편집 / _path 없음+_untitled = 무제 편집 /
    /// _path 없음+!_untitled = PDF·빈 화면). 세우는 곳 = StartUntitled 하나,
    /// 내리는 곳 = 첫 저장 성공(CommitSave)·파일/PDF 열기(ApplyLoadedText·OpenPdf).
    /// </summary>
    private bool _untitled;

    /// <summary>A189: 무제 문서 표시명 — 하단 바 파일명·저장 확인 대화상자·피커 제안 파일명이
    /// 같은 값을 쓴다. 셸의 창 제목 "KOTU - Untitled"(MainWindow.OnUntitledOpened)와 표기 동기.</summary>
    private const string UntitledDisplayName = "Untitled";
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
            // A193: 분할 조립 루프도 같은 static 이벤트 — 같은 해제 의무(남기면 뷰 누수 +
            // 닫힌 뷰의 RenderStack 조작).
            StopRenderAppendLoop();
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
    /// (A177 — 그동안 _path는 null)는 빈 문자열. A189: 무제 문서도 텍스트 편집이라 표시한다.
    /// </summary>
    private void UpdateZoomText()
    {
        var text = _path is not null || _untitled ? $"{_zoomPercent}%" : string.Empty;
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
    /// 읽기 결과를 에디터에 반영하는 종착점 — A177 이전 OpenPath의 후반부 그대로다(순서 계약:
    /// 상태 세팅 → Text 대입 → 기준 텍스트 수립 → 더티 해제 → 표시 전환 → 장식 무효화 →
    /// 렌더 축 판정(A190 — 마크다운이면 기본 렌더 뷰로 재전환) → ContentOpened → 트레이).
    /// 소용량은 읽기 완료 즉시, 대용량(A177)은 로딩 표시 프레임이
    /// 제출된 뒤에 이 메서드로 들어온다 — 두 경로의 차이는 진입 시점뿐이다.
    /// </summary>
    private void ApplyLoadedText(string path, LoadedText loaded)
    {
        HidePdf(); // PDF → 텍스트 전환 (A16)
        _path = path;
        _untitled = false; // A189: 실경로 열기 — 무제 표지를 걷는다(방어 — 현행 라우팅상 무제 중 열기는 새 뷰다)
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
        UpdateNewFileButton(); // A189: 콘텐츠가 열렸다 — New text file 비활성

        // A190: 마크다운이면 기본 = 렌더 뷰(사양). 자격 = md 확장자 + 비잘림 + A177 임계 이하
        // (대용량 md는 렌더 생략·에디터만 — A178 성능 원칙. 4MB 잘림은 항상 임계 초과지만 명시
        // 이중 게이트). 빈 파일은 그릴 게 없어 편집으로 시작한다(토글은 활성 — 타이핑 후 미리보기).
        var renderEligible = IsMarkdownPath(path) && !loaded.Truncated
            && loaded.Text.Length <= LargeDocumentChars;
        ResetRenderState(renderEligible);
        if (renderEligible && loaded.Text.Length > 0) EnterRenderMode();

        ContentOpened?.Invoke(path); // 셸 동기화 — A22: 셸이 드라이브 줄을 내린다
        TrayStatusChanged?.Invoke(); // A54→A138: 트레이 = "1/1"(텍스트는 페이지 개념 없음)
    }

    // ---------- 무제 문서 (A189) ----------

    /// <summary>A189: 하단 바 'New text file' 클릭 — 무제 문서로 에디터 진입.</summary>
    private void OnNewFileClick(object sender, RoutedEventArgs e) => StartUntitled();

    /// <summary>
    /// A189: 'New text file' 버튼 활성 판정 — 빈 상태(아무 콘텐츠도 표시 전)에서만 켠다.
    /// 콘텐츠가 있으면 비활성(A145 Fit 조절기의 "항상 보이되 비활성" 관용구) — 눌리면 현재
    /// 문서를 대체해야 해서 뷰 내부 미저장 가드가 필요해지는 경로 자체를 만들지 않는다.
    /// 로딩 중(_shownPath 확정 전)에는 아직 활성인데, 그때의 클릭은 StartUntitled의
    /// _openSeq 증가가 진행 중 읽기·지연 대입(A177)을 자연 무산시킨다(경합 없음).
    /// </summary>
    private void UpdateNewFileButton() =>
        NewFileButton.IsEnabled = _shownPath is null && !_untitled;

    /// <summary>
    /// A189: 무제 문서로 에디터 진입 — ApplyLoadedText와 같은 순서 계약(상태 세팅 → Text 대입 →
    /// 기준 텍스트 수립 → 더티 해제 → 표시 전환 → 장식 무효화 → 셸 통지)을 빈 텍스트로 밟는다.
    /// 더티 기준 텍스트 = 빈 문자열(사양)이라 첫 입력부터 A113 ⓒ 판정이 성립하고, 저장은
    /// 경로가 없어 첫 Ctrl+S가 Save as 피커로 간다(SaveCoreAsync). 새 파일 기본값 =
    /// UTF-8(BOM 없음)·CRLF(줄바꿈 없는 기존 파일의 감지 폴백과 같은 Windows 기본).
    /// 트레이·작업표시줄 32px는 경로 없음 폴백 그대로다(_shownPath=null → 유휴 "DOC" /
    /// 셸 OpenFileIconInfo의 File.Exists 가드 → 유휴 이니셜) — 값 무변경이라 TrayStatusChanged도
    /// 쏘지 않는다(셸 갱신은 UntitledOpened 쪽 RefreshShellIcons가 겸한다). 첫 저장이 경로를
    /// 확정하면 CommitSave의 기존 배선(ContentOpened·TrayStatusChanged)이 전부 되살린다.
    /// </summary>
    private void StartUntitled()
    {
        ++_openSeq; // 진행 중 읽기(OpenPath)·지연 대입(A177)이 이 상태를 덮지 않게
        HidePdf();
        ResetRenderState(false); // A190: 렌더 축 리셋 — 무제는 .txt 계열(무제 md는 범위 밖)
        _path = null;
        _untitled = true;
        _shownPath = null;
        _encoding = TextEncodingKind.Utf8;
        _newLine = "\r\n";
        _truncated = false;
        _originalBytes = null;
        _lossyAtLoad = false;
        _lossyReason = RoundTripLoss.None;
        _diskWriteTimeUtc = default; // ⓓ 스탬프는 첫 저장(CommitSave)이 잡는다
        _diskLength = 0;

        _gutterDigits = DigitCount(1) + 1; // 빈 문서 = 1줄 — ApplyLoadedText와 같은 산식
        UpdateEditorPadding();

        _loadingText = true; // 프로그램적 설정 — dirty 아님(ApplyLoadedText 관용구)
        EditorBox.Text = string.Empty;
        _loadingText = false;
        _textSnapshot = null;
        _baselineText = string.Empty; // A113 ⓒ: 무제의 더티 기준 = 빈 문자열
        _dirtyTimer?.Stop();
        EditorBox.IsReadOnly = false;
        SetDirty(false);

        EditorBox.Visibility = Visibility.Visible;
        _decor.Invalidate(); // A115: 표시 전환이 레이아웃에 반영된 뒤 장식을 다시 그린다
        PlaceholderText.Visibility = Visibility.Collapsed;
        FileNameText.Text = UntitledDisplayName;
        UpdateZoomText(); // A181: 무제도 텍스트 편집 — 배율 표시
        UpdateNewFileButton();
        UntitledOpened?.Invoke(); // 셸 동기화 — 탐색기 내림·드라이브 줄 숨김·제목 "KOTU - Untitled"
        EditorBox.Focus(FocusState.Programmatic); // 곧바로 타이핑 가능하게
    }

    // ---------- 마크다운 렌더 뷰 (A190) ----------
    //
    // 상태 전이표(함정 1 — 문서 모듈 상태 축 정본. 종전 3축: 파일 편집(_path)/무제(_untitled)/
    // PDF·빈 화면(_path=null, !_untitled)에 렌더 축(_renderMode — 파일 편집의 하위 모드)이 얹혔다):
    //
    //   이벤트                        | 결과 상태
    //   ------------------------------+------------------------------------------------------------
    //   열기 .txt/.log/.ini           | 편집(렌더 축 리셋 — _renderEligible=false, 토글 비활성)
    //   열기 .md/.markdown (소용량)   | 렌더(기본 — 사양. 빈 파일은 편집으로 시작, 토글은 활성)
    //   열기 .md (A177 대용량·4MB 잘림)| 편집만(_renderEligible=false — 렌더 생략, A178 성능 원칙)
    //   열기 .pdf                     | PDF 뷰(렌더 축 리셋)
    //   New text file (A189 무제)     | 무제 편집(렌더 축 리셋 — 무제 md는 범위 밖)
    //   토글 클릭 (렌더 중)           | 편집(에디터 표시·포커스 — 보류 중 파싱은 시퀀스로 무산)
    //   토글 클릭 (편집 중)           | 렌더(현재 에디터 버퍼를 그 시점에 재파싱 — 사양: 재렌더는
    //                                 | 토글 시점. 미저장 변경도 렌더에 보인다)
    //   저장 (Ctrl+S·버튼)            | 모드 불변 — 저장은 EditorText 기준이라 렌더 중에도 동작
    //   Save as로 경로 변경(검증 실패)| 모드 불변 — 확장자는 피커가 동일하게 유지, 자격만 재판정
    //   무제 첫 저장 (.txt 고정)      | 편집 유지(자격 재판정 — .txt라 계속 비활성)
    //   닫기·모듈 전환               | ICloseGuard(HasUnsavedChanges) — 모드 무관, 버퍼 기준
    //   Esc·전체화면                  | 셸의 3단 모드 축(A151) — 이 모듈 상태 불변
    //
    // 불변식: _renderMode이면 반드시 _renderEligible이고 _path는 md 파일이다. 렌더 모드에서
    // 에디터는 Collapsed일 뿐 내용은 그대로다(렌더는 읽기 전용 표시일 뿐 — 더티·저장·A113 체계
    // 전부 에디터 버퍼가 정본). 줌(A181)·Fit(A145)은 에디터 모드 기준 그대로다(렌더 모드 줌은
    // 범위 밖 — 등재 후보).
    //
    // A193(분할 조립 축): 렌더 진입은 첫 조각(RenderChunkBlocks)만 즉시 조립하고 나머지는
    // CompositionTarget.Rendering 틱당 한 조각씩 append한다(StartRenderAppendLoop). 루프는
    // _renderMode 동안만 살아 있고, 위 표의 모든 이탈 전이가 루프를 죽인다 —
    // 파일/PDF/무제 열기(ResetRenderState)·편집 토글(ExitRenderMode) = 명시 해제 + _renderSeq 증가,
    // 렌더 재진입(EnterRenderMode) = seq 증가 + 루프 기동 직전 방어 해제,
    // 뷰 언로드 = Unloaded의 명시 해제(static 이벤트 — 함정 1).
    // 구 루프가 한 틱 살아남아도 append 직전 seq 대조가 낡은 블록 부착(잔상)을 막는다(함정 2 —
    // 한 틱 = 한 조각이라 틱 진입 시 1회 대조로 충분하다). 토글 재진입은 전체 재조립(현행 사양 —
    // Clear 후 첫 조각부터 다시, 진행 중이던 구 루프는 seq로 무산).

    /// <summary>렌더 가능 판정(md 파일 + 비잘림 + A177 임계 이하) — 토글 버튼 활성의 단일 출처.</summary>
    private bool _renderEligible;

    /// <summary>true = 렌더 뷰 표시 중(에디터 Collapsed). 세우고 걷는 곳 = EnterRenderMode/
    /// ExitRenderMode/ResetRenderState 셋뿐이다.</summary>
    private bool _renderMode;

    /// <summary>느린 파싱이 모드 전환·파일 전환 뒤에 낡은 결과를 그리지 않게(_openSeq 관용구).
    /// A193: 분할 조립 루프의 중단 판정도 이 값이다 — 매 틱 append 직전에 대조한다.</summary>
    private int _renderSeq;

    /// <summary>
    /// A193 체감 조정 지점: 분할 조립 조각 크기(블록 수) — 첫 즉시 조각과 프레임당 append 조각이
    /// 같은 값을 쓴다(확정 수치 60 — 되돌리기·조정은 이 상수 하나만 고치면 된다.
    /// 수치를 한 곳에만 두는 MarkdownRenderer 상수 배치 관용구).
    /// </summary>
    private const int RenderChunkBlocks = 60;

    /// <summary>
    /// A193: 분할 조립 루프의 프레임 틱 핸들러(null = 루프 없음). CompositionTarget.Rendering은
    /// static 이벤트라(A177 _pendingApplyHandler와 같은 사정) 뷰 수명 안에서 반드시 해제한다 —
    /// 남기면 뷰가 통째로 누수되고 닫힌 뷰의 RenderStack을 계속 조작한다(함정 1).
    /// 해제의 단일 지점 = StopRenderAppendLoop. 호출부 전수 = Unloaded·ResetRenderState·
    /// ExitRenderMode·StartRenderAppendLoop 기동 직전 방어·틱 내부(완료/seq 중단/예외).
    /// </summary>
    private EventHandler<object>? _renderAppendHandler;

    private static bool IsMarkdownPath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A190: 하단 바 토글 클릭 — 편집 ↔ 렌더. 비활성 게이트(XAML IsEnabled)가 1차지만 방어 재확인.</summary>
    private void OnViewToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_renderEligible) return;
        if (_renderMode) ExitRenderMode();
        else EnterRenderMode();
    }

    /// <summary>
    /// A190: 토글 버튼 표시 갱신의 단일 지점 — 활성(_renderEligible) + 글리프·툴팁(누르면 갈 모드:
    /// 편집 중 E890 View / 렌더 중 E70F Edit). UpdateFitButton과 같은 "코드가 내용을 정한다" 관용구.
    /// </summary>
    private void UpdateViewToggle()
    {
        ViewToggleButton.IsEnabled = _renderEligible;
        ViewToggleIcon.Glyph = _renderMode ? "\uE70F" : "\uE890"; // Edit / View
        ToolTipService.SetToolTip(ViewToggleButton, _renderMode ? "Edit" : "Preview (Markdown only)");
    }

    /// <summary>
    /// A190: 렌더 모드 진입 — 표시 전환은 즉시, 내용은 워커 파싱(A42: 파싱 = 워커, 요소 조립 = UI)
    /// 완료 후 채운다(그동안 이전 내용 또는 빈 판이 보인다). A193: 조립은 첫 조각
    /// (RenderChunkBlocks — 첫 화면 분량)만 즉시 하고 나머지는 프레임 틱 루프
    /// (StartRenderAppendLoop)가 조각 단위로 잇는다 — 수백 KB md의 일괄 조립이 UI 스레드를
    /// 수백 ms 점유하던 것의 A178 수리(파싱은 무변경). 소형 문서(첫 조각 이하)는 여기서 전부
    /// 끝나 루프가 아예 돌지 않는다 — 종전 일괄 조립과 동작 동일.
    /// 파싱·조립 어느 쪽이 실패해도 원문 TextBlock 폴백으로 대체한다(함정 3 — 앱 다운 금지,
    /// ApplyPlainTextFallback). 포커스는 뷰 루트로 옮긴다(Collapsed 에디터에 남기지 않는다 —
    /// 셸 키 처리 관례).
    /// </summary>
    private async void EnterRenderMode()
    {
        _renderMode = true;
        UpdateViewToggle();
        EditorBox.Visibility = Visibility.Collapsed;
        _decor.Invalidate(); // A115: 에디터가 내려갔다 — 다음 레이아웃에서 장식도 걷힌다(OpenPdf 관용구)
        RenderPane.Visibility = Visibility.Visible;
        Focus(FocusState.Programmatic);

        var seq = ++_renderSeq;
        var text = EditorText; // A142 ①ⓑ: UI 스레드에서 스냅샷 확보 — 워커는 이 복사본만 읽는다
        IReadOnlyList<MdBlock> blocks;
        try
        {
            blocks = await Worker.Run(_ => MarkdownParser.Parse(text));
        }
        catch (OperationCanceledException)
        {
            return; // 뷰가 내려가며 워커가 닫힘
        }
        catch (Exception)
        {
            blocks = MarkdownParser.Fallback(text); // Parse 자체 폴백의 이중 방어
        }
        if (seq != _renderSeq) return; // 그새 토글·파일 전환이 있었다 — 낡은 결과 폐기

        try
        {
            // A193: 첫 조각만 즉시 조립 — 첫 호출 전 Clear는 호출자 몫(AppendRange 계약).
            RenderStack.Children.Clear();
            var first = Math.Min(RenderChunkBlocks, blocks.Count);
            MarkdownRenderer.AppendRange(RenderStack, blocks, 0, first);
            if (first < blocks.Count) StartRenderAppendLoop(seq, blocks, first, text);
        }
        catch (Exception)
        {
            ApplyPlainTextFallback(text); // 조립 실패 — 원문 그대로(고정폭)가 합격선이다
        }
    }

    /// <summary>
    /// A193: 첫 조각 이후의 나머지 블록을 CompositionTarget.Rendering 틱마다 한 조각
    /// (RenderChunkBlocks)씩 RenderStack 끝에 append한다 — UI 스레드 점유 상한 = 조각 1개 조립
    /// (A177 지연 대입과 같은 프레임 틱 관용구·같은 해제 의무).
    /// 중단 판정 = 매 틱 append 직전의 seq 대조(한 틱 = 한 조각이라 틱 진입 시 1회로 충분 —
    /// 함정 2): ResetRenderState·ExitRenderMode·EnterRenderMode 재진입이 _renderSeq를 올리는
    /// 현행 구조 그대로다 — 구 루프가 새 문서·비워진 판에 낡은 블록을 붙이는 사고를 막는다.
    /// 스크롤 보정은 불요 — append는 판 끝에만 붙어 이미 보이는 내용의 오프셋(뷰포트 상단)이
    /// 움직이지 않는다. 틱 핸들러는 본문 전체가 try/catch다(static 이벤트라 예외가 새면 앱 전역
    /// 크래시 — 함정 3): 조각 조립 예외 = 부분 조립물 전체를 버리고 원문 폴백으로 교체 + 루프
    /// 중단(부분 렌더 잔존 금지 — EnterRenderMode 첫 조각의 폴백 계약과 동일).
    /// </summary>
    private void StartRenderAppendLoop(int seq, IReadOnlyList<MdBlock> blocks, int start, string text)
    {
        StopRenderAppendLoop(); // 방어: 직전 루프가 남아 있으면 먼저 해제(DeferApplyAfterRender 관용구)

        var next = start;
        void OnTick(object? sender, object? e)
        {
            try
            {
                if (seq != _renderSeq)
                {
                    StopRenderAppendLoop(); // 그새 토글·파일 전환·재진입 — 낡은 블록을 붙이지 않는다
                    return;
                }
                var count = Math.Min(RenderChunkBlocks, blocks.Count - next);
                MarkdownRenderer.AppendRange(RenderStack, blocks, next, count);
                next += count;
                if (next >= blocks.Count) StopRenderAppendLoop(); // 완료 — 더 깨울 이유가 없다
            }
            catch (Exception)
            {
                StopRenderAppendLoop();
                ApplyPlainTextFallback(text); // 자체 최후 방어까지 있어 여기서 다시 던지지 않는다
            }
        }
        _renderAppendHandler = OnTick;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnTick;
    }

    /// <summary>A193: 분할 조립 루프 해제의 단일 지점 — 구독 해제 + 표지 소거(루프 없으면 무동작).
    /// 기동은 StartRenderAppendLoop 한 곳뿐이라 구독 중 핸들러 = 이 필드 하나가 불변식이다.</summary>
    private void StopRenderAppendLoop()
    {
        if (_renderAppendHandler is { } handler)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
            _renderAppendHandler = null;
        }
    }

    /// <summary>
    /// A190/A193: 조립 실패 폴백 — 조립물(부분 조립 포함)을 전부 버리고 원문 그대로(고정폭)로
    /// 교체한다. 이것마저 실패하면 빈 판(최후 방어). 어떤 경우에도 밖으로 던지지 않는다 —
    /// 분할 조립 틱(static 이벤트)에서도 불리므로 예외가 새면 앱 전역 크래시다(함정 3).
    /// </summary>
    private void ApplyPlainTextFallback(string text)
    {
        try
        {
            RenderStack.Children.Clear();
            RenderStack.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = BaseEditorFontSize,
                FontFamily = new FontFamily("Consolas"),
            });
        }
        catch (Exception)
        {
            try
            {
                RenderStack.Children.Clear();
            }
            catch (Exception)
            {
                // 최후 방어 — 빈 판 복구조차 실패하면 삼킨다(틱 핸들러 밖으로 새면 앱이 죽는다)
            }
        }
    }

    /// <summary>A190: 편집 모드 복귀 — 렌더 내용은 남겨 둔다(다음 진입 시 통째 교체 — 재렌더는 토글 시점, 사양).</summary>
    private void ExitRenderMode()
    {
        _renderSeq++; // 보류 중 파싱 무산(A193: 분할 조립 루프의 seq 대조도 이걸로 무산된다)
        StopRenderAppendLoop(); // A193: 진행 중 루프는 즉시 명시 해제 — 다음 틱을 기다리지 않는다
        _renderMode = false;
        UpdateViewToggle();
        RenderPane.Visibility = Visibility.Collapsed;
        EditorBox.Visibility = Visibility.Visible;
        _decor.Invalidate(); // A115: 에디터 복귀 — 다음 레이아웃에서 장식 재개
        EditorBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// A190: 렌더 축 리셋 — 파일·PDF·무제 열기의 공통 선행 단계(상태 전이표의 "렌더 축 리셋").
    /// 판(RenderPane)을 내리고 조립물을 비워 이전 문서의 잔상이 새 문서에 비치지 않게 한다.
    /// A193: 진행 중 분할 조립 루프도 여기서 명시 해제한다 — Clear보다 먼저라 비워진 판에
    /// 구 루프가 한 틱 늦게 append하는 잔상 경로가 없다(seq 대조가 2중 방어).
    /// 에디터 Visibility는 만지지 않는다 — 각 열기 경로가 자기 표시 전환을 그대로 수행한다.
    /// </summary>
    private void ResetRenderState(bool eligible)
    {
        _renderSeq++; // 보류 중 파싱 무산(A193: 분할 조립 루프의 seq 대조도 이걸로 무산된다)
        StopRenderAppendLoop(); // A193: 루프 명시 해제 — 아래 Clear 이후 append가 성립할 수 없다
        _renderMode = false;
        _renderEligible = eligible;
        RenderPane.Visibility = Visibility.Collapsed;
        RenderStack.Children.Clear();
        UpdateViewToggle();
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
        _untitled = false; // A189: PDF 뷰 — 무제 표지도 함께 걷는다(경로 없음 = 뷰 전용의 종전 의미)
        _truncated = false;
        _originalBytes = null;
        _lossyAtLoad = false;
        _lossyReason = RoundTripLoss.None;
        _baselineText = string.Empty;
        _dirtyTimer?.Stop();
        SetDirty(false);
        ResetRenderState(false); // A190: 렌더 축 리셋 — PDF 뷰에는 토글이 없다(비활성)
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
            UpdateNewFileButton(); // A189: 빈 상태로 복귀 — New text file 재활성
            TrayStatusChanged?.Invoke(); // A54: 열기 실패 → 유휴("DOC")
            return;
        }

        _shownPath = path;
        UpdateNewFileButton(); // A189: 콘텐츠가 열렸다 — New text file 비활성
        UpdatePrintButton(); // A211 배치 3: PDF 로드 성공 — 인쇄 가능(버튼 활성·셸 Ctrl+P는 같은 판정을 직접 본다)
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
        // A211 배치 3: PDF 갈래 이탈(텍스트/무제 전환·열기 실패·빈 화면 복귀 전부 이 관문을
        // 지난다) — 인쇄 비활성. OpenPdf 성공과 이 둘이 PDF 인쇄 상태 변화의 호출 전수다.
        UpdatePrintButton();
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
    /// A184: 그 PathIcon 도형을 글자 "1:1" 형상에서 꺾쇠 프레임으로 바꿨다
    /// (BuildActualSizeIconGeometry 주석 참조 — 툴팁·키·동작은 무변경).
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
    /// A143/v0.174.1: 100% 아이콘 도형(16x16 좌표계 — PathIcon은 스케일하지 않는다).
    /// A184: 도형 5개 = 바깥 네 모서리 꺾쇠 4개(각 변 4·획 1.5·모서리 여백 2) + 가운데 채움
    /// 사각형 4x4(6,6~10,10). "원본 크기 프레임 그대로"라는 뜻이고 확대/축소 화살표가 없어
    /// Contain류와 구분된다. 종전 A143 도형(글자 "1:1" 형상 — 깃발+기둥 2개와 콜론 점 2개)은
    /// 어색하다는 사용자 보고로 폐기했다. 호출마다 새 인스턴스를 만든다
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
        // 좌상 꺾쇠
        geometry.Figures.Add(Fig(2.0, 2.0, (6.0, 2.0), (6.0, 3.5), (3.5, 3.5), (3.5, 6.0), (2.0, 6.0)));
        // 우상 꺾쇠
        geometry.Figures.Add(Fig(14.0, 2.0, (10.0, 2.0), (10.0, 3.5), (12.5, 3.5), (12.5, 6.0), (14.0, 6.0)));
        // 우하 꺾쇠
        geometry.Figures.Add(Fig(14.0, 14.0, (10.0, 14.0), (10.0, 12.5), (12.5, 12.5), (12.5, 10.0), (14.0, 10.0)));
        // 좌하 꺾쇠
        geometry.Figures.Add(Fig(2.0, 14.0, (6.0, 14.0), (6.0, 12.5), (3.5, 12.5), (3.5, 10.0), (2.0, 10.0)));
        // 가운데 채움 사각형
        geometry.Figures.Add(Fig(6.0, 6.0, (10.0, 6.0), (10.0, 10.0), (6.0, 10.0)));
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
            // A189: 무제(_untitled)는 경로가 없어도 판정 대상이다 — 편집 계열 가드 공통 형태.
            if (_loadingText || (_path is null && !_untitled) || _truncated) return; // 판정 대상이 아니다
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
        if (_loadingText || (_path is null && !_untitled) || _truncated) return; // A189: 무제도 더티 추적
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
        // 잘림·PDF·빈 화면 — 저장 대상이 없다(ⓐ~ⓓ 비적용). A189: 무제는 저장 대상이다 —
        // 경로 확정(Save as 피커)은 SaveCoreAsync 몫.
        if ((_path is null && !_untitled) || _truncated) return true;
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
        // A189: 무제 문서는 저장 대상 경로가 아직 없다 — 먼저 Save as 피커로 경로를 확정한다
        // (A113 ⓐ의 "Save as..." 피커 재사용). 취소 = 저장 전체 취소(더티 유지 — ConfirmCloseAsync
        // 경유면 닫기도 함께 취소). ⓓ 외부 변경·ⓑ 라운드트립 예고는 원본 디스크 파일이 없어
        // 자연히 건너뛴다(_path=null → DiskChangedSinceLoad=false, _originalBytes=null).
        // 피커가 기존 파일을 골랐으면 덮어쓰기 확인은 피커 자신이 했다(기존 Save as 흐름과 동일).
        string originalPath;
        var savedAs = false;
        if (_path is { } existing)
        {
            originalPath = existing;
        }
        else if (_untitled)
        {
            if (await PickSaveAsPathAsync() is not { } picked) return false; // 피커 취소 = 저장 취소
            originalPath = picked;
            savedAs = true; // CommitSave가 경로 확정 연쇄(_path·제목·트레이·셸 통지)를 탄다
        }
        else
        {
            return true; // SaveAsync가 걸렀다 — 방어
        }

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
            _path = path;      // Save as...·무제 첫 저장(A189) — 이후 편집·저장은 새 파일이 대상
            _untitled = false; // A189: 경로가 확정됐다 — 무제 표지를 걷는다(이하 통지는 기존 배선)
            _shownPath = path; // 트레이 표기(A54)도 새 파일 기준
            FileNameText.Text = Path.GetFileName(path);
            UpdateNewFileButton(); // A189: 무제 → 파일 전이 — 버튼은 계속 비활성(콘텐츠 있음)
            // A190: 경로가 바뀌었다 — 렌더 자격만 재판정(모드는 편집 유지 — 상태 전이표).
            // 현행 피커는 무제=.txt 고정·검증 실패 Save as=같은 확장자라 값이 바뀌는 경로는
            // 없지만, 자격의 단일 출처를 지키는 방어다(_truncated는 저장 가능이므로 항상 false).
            _renderEligible = IsMarkdownPath(path) && !_truncated
                && EditorText.Length <= LargeDocumentChars;
            UpdateViewToggle();
            // 창 제목 갱신은 셸 몫 — OnContentOpened가 무제 전이(A189)에 한해 새 경로로 바꾼다
            // (기존 파일 Save as의 제목 미갱신은 A113 알려진 한계 그대로 — 이번 범위 밖).
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
    /// A189: 무제 문서(_path=null)는 확장자 .txt(새 텍스트 파일)·제안 파일명 Untitled.
    /// null = 사용자 취소(저장 전체 취소).
    /// </summary>
    private async Task<string?> PickSaveAsPathAsync()
    {
        var ext = _path is { } current ? Path.GetExtension(current) : ".txt"; // 무제(A189) = .txt
        if (string.IsNullOrEmpty(ext)) ext = ".txt"; // 확장자 없는 경로 방어(현행 라우팅상 오지 않는다)
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _path is { } named
                ? Path.GetFileNameWithoutExtension(named)
                : UntitledDisplayName,
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
            // A189: 무제 문서(_path=null인데 더티 — 무제뿐이다)는 표시명으로 묻는다.
            Content = $"Save changes to {(_path is { } p ? Path.GetFileName(p) : UntitledDisplayName)}?",
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

    // ---------- 인쇄 (A211 배치 3, v0.222.0 — 계약 = IPrintPageProvider, 소비자 = 셸 PrintHost) ----------
    // 사양 단일 원본 = docs/A211-print-research.md §2(PdfPane 접점)·§3-2 + 배치 2(ImageViewerView)
    // 산식 선례. 이 절이 문서 모듈 인쇄의 **모드 분기 축**이다(설계 정본): 한 뷰가 PDF / 텍스트
    // 편집 / 마크다운 렌더 세 표면을 오가므로 계약 구현은 "지금 전면인 표면" 판정에서 갈라진다 —
    // 배치 3은 PDF 갈래(PrintablePdfPane)만 얹고, 텍스트(배치 4)·마크다운(배치 5)은 아래 각
    // 멤버의 "배치 4/5 예약" 주석 자리에 자기 갈래를 분기로 얹는다(멤버 추가가 아니라 분기 추가 —
    // 네 판정(CanPrintNow·GetPrintPageCount·CreatePrintPageAsync·버튼 활성)이 같은 갈래를 봐야 한다).

    /// <summary>
    /// 인쇄 렌더 유효 DPI 상한(A211 배치 3 확정값 300). 근거: 래스터 인쇄 품질의 관행 기준이
    /// 300DPI이고 그 위는 지면 체감 향상이 미미한데 비트맵 메모리는 DPI 제곱으로 는다 —
    /// Letter 세로 300DPI ≈ 2550×3300px ≈ 32MB(BGRA)라 "한 장 렌더·즉시 폐기"(조사 §1-ⓑ
    /// MS 조언 — 셸 AddPages가 참조를 넘기고 버린다)의 페이지당 일시 점유가 안전권이다.
    /// 600·1200DPI 프린터 값을 그대로 따르면 페이지당 129MB~0.5GB가 된다.
    /// </summary>
    private const double MaxPrintRenderDpi = 300;

    /// <summary>인쇄 렌더 유효 DPI 하한 = 화면 밀도 96 — 프린터가 이상 규격(DpiX 0 등)을 줘도
    /// 화면보다 흐려지지는 않게(셸 PrintHost.FallbackSpec의 96과 같은 값).</summary>
    private const double MinPrintRenderDpi = 96;

    /// <summary>
    /// 인쇄 렌더 비트맵 한 변 픽셀 상한. DPI 상한이 못 막는 축을 막는다 — 렌더 픽셀은 용지
    /// 크기에도 비례하므로 대형 용지·세장 페이지(포스터·배너 PDF)는 300DPI로도 폭주한다.
    /// 4096이면 최악(정사각)에도 4096²×4B = 64MB로 닫히고, 표준 용지(Letter/A4)의 300DPI 긴 변
    /// (3300~3508px)은 안 걸려 일반 문서 품질은 그대로다.
    /// </summary>
    private const double MaxPrintRenderPixels = 4096;

    /// <summary>
    /// 모듈 하단 바 인쇄 버튼(PrintButton) → 셸 인쇄 단일 경로(MainWindow.RequestPrint).
    /// 셸이 ShowModule에서 구독하고, All Readable 자식일 때는 AllReadableView가 중계한다(배치 2).
    /// 뷰는 셸을 모른 채 신호만 쏜다(계약 규정 — 배치 2와 동일 배선).
    /// </summary>
    public event Action? PrintRequested;

    /// <summary>
    /// PDF 인쇄 가능 판정의 단일 지점 — "PDF 표면이 전면"(패널 존재+표시, OnRootPreviewKeyDown
    /// 게이트 ⓐ와 같은 기준) + "문서가 실제로 로드됨"(PrintPageCount 양수 — LoadAsync 완료 전·
    /// 실패 후는 제외된다. 로드 진행 중의 값 의미는 PdfPane.PrintPageCount 주석 참고).
    /// </summary>
    private PdfPane? PrintablePdfPane =>
        _pdfPane is { Visibility: Visibility.Visible, PrintPageCount: > 0 } pane ? pane : null;

    /// <summary>
    /// 지금 인쇄할 콘텐츠가 있는가 — <b>배치 3은 PDF만</b>: 텍스트 편집·마크다운 렌더·무제·빈
    /// 화면은 false(Ctrl+P·버튼 둘 다 잠잠 — 부록 B 78 범위의 단계적 확장 중).
    /// 배치 4/5 예약: 텍스트/마크다운 갈래는 이 식에 || 분기로 얹는다 —
    /// <see cref="CreatePrintPageAsync"/>의 갈래 분기와 반드시 짝으로.
    /// </summary>
    public bool CanPrintNow => PrintablePdfPane is not null;

    /// <summary>
    /// OS 인쇄 큐·대화상자에 뜰 작업 이름 = 보고 있는 파일 이름(_shownPath — PDF도 채워진다).
    /// 배치 4 예약: 무제 문서 갈래는 UntitledDisplayName을 돌려줄 것(경로가 없다 — 계약의
    /// "무제 문서는 표시 제목" 규정). 비면 셸이 앱 이름으로 대체한다(계약).
    /// </summary>
    public string PrintJobName => _shownPath is { } shown ? Path.GetFileName(shown) : string.Empty;

    /// <summary>
    /// PDF = 문서 전체 페이지 수(PdfPane.PrintPageCount — PageCount 메타 값 즉답·렌더 0회라
    /// 수백 페이지에서도 Paginate가 무겁지 않다 — 계약의 "무겁게 만들지 말 것").
    /// spec은 PDF 갈래에서는 안 본다 — PDF 1페이지 = 종이 1장 고정이라 용지가 수를 못 바꾼다.
    /// 배치 4/5 예약: 텍스트/마크다운은 측정 기반 페이지네이터의 결과를 여기 갈래로 얹는다
    /// (그때부터 spec(용지·영역)이 페이지 수를 결정한다).
    /// </summary>
    public int GetPrintPageCount(PrintPageSpec spec) => PrintablePdfPane?.PrintPageCount ?? 0;

    /// <summary>
    /// pageNumber(1-base) 인쇄 페이지 1장 조립 — 미리보기(GetPreviewPage)와 본인쇄(AddPages)가
    /// 같은 이 메서드를 타므로 <b>호출마다 전부 새 인스턴스</b>다(v0.174.1 교훈 — 요소 부모 1개,
    /// 계약 규칙. 비트맵도 매회 새로 렌더한다). 요청 페이지만 지연 렌더 — 선렌더·자체 캐시 없음
    /// (수백 페이지 PDF에서 미리보기 n페이지 이동 = 그 페이지 1회 렌더가 전부다).
    /// <para>
    /// 해상도: 렌더 픽셀 폭 = 종이 위 실크기(DIP) × 유효 DPI ÷ 96. 유효 DPI =
    /// spec.DpiX를 [96, 300]으로 접은 값 + 한 변 4096px 상한 — 근거는 상수 3종 주석.
    /// 미리보기와 본인쇄를 구분하지 않는다(보수적 단일 상한): 셸 PrintHost는 두 경로가 같은
    /// BuildPageAsync 하나고 spec도 Paginate에서 굳힌 한 벌이라(PrintHost._spec) 계약상 구분
    /// 신호가 없다 — 300DPI 상한이면 미리보기 과렌더도 페이지당 수십 MB 일시 점유로 닫히고,
    /// 미리보기가 인쇄물과 같은 픽셀이라 품질 확인 수단으로도 정직하다.
    /// </para>
    /// 배치 4/5 예약: 모드 갈래는 첫 판정에서 나눈다 — PDF 갈래가 아니면(그때 가서 텍스트/
    /// 마크다운 갈래로) null. null·예외는 셸이 안내 페이지로 대체한다(계약 — 파이프는 계속 간다).
    /// </summary>
    public async Task<object?> CreatePrintPageAsync(int pageNumber, PrintPageSpec spec)
    {
        if (PrintablePdfPane is not { } pane) return null; // 배치 4/5 예약: 텍스트/마크다운 갈래 분기 지점
        if (pageNumber < 1 || pageNumber > pane.PrintPageCount) return null;
        if (pane.GetPrintPageSize(pageNumber) is not { } size) return null;

        // 인쇄 가능 영역(Imageable) — 용지 기준으로 잡으면 프린터가 물리적으로 못 찍는 가장자리에서
        // 잘린다. 이상 규격(0 이하) 방어까지 배치 2(ImageViewerView.CreatePrintPageAsync) 산식 그대로.
        var areaWidth = spec.ImageableWidth > 0 ? spec.ImageableWidth : spec.PageWidth;
        var areaHeight = spec.ImageableHeight > 0 ? spec.ImageableHeight : spec.PageHeight;
        var areaX = spec.ImageableWidth > 0 ? spec.ImageableX : 0;
        var areaY = spec.ImageableHeight > 0 ? spec.ImageableY : 0;
        if (areaWidth <= 0 || areaHeight <= 0) return null;

        // contain — PDF 페이지(96DPI DIP)와 인쇄 영역(96DPI DIP)이 같은 좌표계라 비율 그대로다.
        // 배율 상한 없는 "영역 안 최대"(부록 B 78 이미지 확정과 같은 의미론) — 화면 Contain의
        // "축소만"(A83)은 화면 전용 결정이라 종이에는 옮기지 않는다(배치 2와 같은 판정 — 뒤집지 말 것).
        var scale = Math.Min(areaWidth / size.Width, areaHeight / size.Height);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) return null;
        var layoutWidth = size.Width * scale;
        var layoutHeight = size.Height * scale;

        // 렌더 픽셀 폭 — 상한 근거는 메서드·상수 주석. 높이 쪽 상한 초과도 폭을 줄여서 막는다
        // (DestinationWidth만 주면 렌더러가 종횡비를 유지하므로 폭 하나로 두 축이 닫힌다).
        var dpi = Math.Clamp((double)spec.DpiX, MinPrintRenderDpi, MaxPrintRenderDpi);
        var pixelWidth = layoutWidth * dpi / 96.0;
        var pixelHeight = layoutHeight * dpi / 96.0;
        var overflow = Math.Max(pixelWidth, pixelHeight) / MaxPrintRenderPixels;
        if (overflow > 1) pixelWidth /= overflow;

        var bitmap = await pane.RenderPrintPageAsync(
            pageNumber, (uint)Math.Max(1, Math.Round(pixelWidth)));
        if (bitmap is null) return null; // 렌더 실패·그새 문서 교체 — 셸이 안내 페이지로 대체

        // 페이지 요소 — Canvas 절대 배치(배치 2 관용구: 자식을 원하는 크기 그대로 놓아 레이아웃
        // 잘림 규칙에 기대지 않는다). 색 명시(흰 종이) — 테마 브러시 금지(계약 규칙).
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            Width = layoutWidth,
            Height = layoutHeight,
        };
        Canvas.SetLeft(image, areaX + ((areaWidth - layoutWidth) / 2));
        Canvas.SetTop(image, areaY + ((areaHeight - layoutHeight) / 2));
        var page = new Canvas
        {
            Width = spec.PageWidth,
            Height = spec.PageHeight,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        };
        page.Children.Add(image);
        return page;
    }

    /// <summary>
    /// 인쇄 버튼 활성 = <see cref="CanPrintNow"/>(부록 B 78 규격 — 배치 3에서는 PDF 로드 성공
    /// 동안만 켜지고 텍스트/마크다운/무제/빈 화면은 비활성. 텍스트 갈래 활성은 배치 4 몫).
    /// 셸 Ctrl+P는 버튼이 아니라 같은 속성을 직접 물으므로(MainWindow.RequestPrint) 버튼 표기와
    /// 키 동작이 어긋날 수 없다(배치 2 UpdatePrintButton과 같은 형). 호출 전수 = OpenPdf 성공 ·
    /// HidePdf(PDF 갈래 상태 변화의 관문 2곳 — HidePdf 쪽 주석 참고). 배치 4/5가 갈래를 얹으면
    /// 그 갈래의 상태 변화 지점에서도 함께 부를 것.
    /// </summary>
    private void UpdatePrintButton() => PrintButton.IsEnabled = CanPrintNow;

    /// <summary>버튼 클릭 = 셸에 인쇄 요청 신호 1발(배선은 셸·All Readable 중계가 — 계약 규정).</summary>
    private void OnPrintButtonClick(object sender, RoutedEventArgs e) => PrintRequested?.Invoke();

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
