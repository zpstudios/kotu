using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using KOTU.Core.Contracts;
using KOTU.Input;

namespace KOTU.Module.Hardware;

/// <summary>
/// 하드웨어 정보 화면. WMI 수집·센서 수집은 HardwareModule.Poller(프로세스 공유 폴링 워커,
/// A42)가 전담하고, 뷰는 구독해서 스냅샷을 UI 스레드로 디스패치 받아 그리기만 한다.
/// A60 3차(v0.138.0): 화면 = 3구획 그래프 중심 — 좌(선택 그래프 확대 ≤2, 10초 창) /
/// 센터(전 채널 타일 그리드, 30초 창 — 클릭 = 선택 토글·드래그 = 순서 변경) /
/// 우(스펙 텍스트 리스트). 전체화면(F11)도 같은 3구획이다 — v0.42.0 섹션 카드 대시보드는 폐기.
/// 하단 바 가운데엔 선택 긴 그래프 2개(5분 창)가 산다 — A17 카드 10개 대체. Copy·⛶·그래프를
/// 담은 하단 바는 셸이 TakeBottomBar()로 떼어간다. 전체화면 동안(셸 하단 바 숨김)은
/// SensorGrid가 SensorStrip으로 옮겨져 긴 그래프가 계속 보인다(v0.64.2 메커니즘 승계).
/// A61(v0.111.0): 핀(A39)을 켜면 셸에 접기를 요청해 하단 바만 남는 상시 표시 바가 된다
/// (IWindowCollapseSource) — 접힌 바에 긴 그래프 2개가 남는 것이 A72 흡수의 핵심 가치.
/// A62: 그 바의 글씨·선 굵기·그래프 크기를 S/M/L로 키운다(바 안 요소 전용 배수).
/// A70(v0.131.0): 센서 선택(A18)·채널 순서(A60 3차)·바 크기(A62)는 창(인스턴스)별 독립 —
/// 이 뷰가 HardwareInstanceState를 소유하고, 저장은 전역 1벌(마지막 커밋 우선)로만 남는다.
/// A101(v0.137.0): 창별 트레이 아이콘(A54)이 **이 창의 선택값**을 직접 표시한다 —
/// ITrayStatusProvider 구현. 센터 타일 클릭 토글 하나로 핀 배지·좌 대형·하단 긴 그래프·트레이가
/// 전부 같은 선택(HardwareInstanceState.Selection)을 따른다(선택 단일화).
/// ISidebarAwareView(A60 3차 신설): 셸이 "좌·우 사이드바 둘 다 열림"을 밀어주면 센터 열 수를
/// 4/8로 바꾼다(A93 썸네일 패턴).
/// </summary>
public sealed partial class HardwareView : UserControl, IBottomBarProvider, IWindowCollapseSource,
    ITrayStatusProvider, ISidebarAwareView
{
    private IReadOnlyList<HardwareSection> _sections = [];
    private AppWindow? _appWindow;
    private IDisposable? _subscription;  // 공유 폴러 구독(로드 중에만 유지 — 없으면 폴러 휴면)
    private bool _firstLoadPending;      // 첫 로드 Busy 링 표시 중 — 첫 스냅샷 도착 시 끈다(A75)
    private string _dataSignature = ""; // 값이 안 바뀌면 UI 재구성 생략
    private SensorFrame _lastFrame = SensorFrame.Empty; // Copy all·트레이 표기에 센서 값 포함용

    // A60 3차: 그래프 표면 3벌 — 센터 타일(전 채널 10개, 상주) / 좌 대형(선택 ≤2) / 하단 긴(선택 ≤2).
    // 좌·하단은 선택이 바뀔 때마다 새로 만든다(RebuildSelectionGraphs) — 표면 요소에는 이벤트
    // 구독이 없어(타일만 Tapped·드래그) 버려도 수명 문제가 없다.
    private readonly List<SensorGraph> _tiles = [];
    private readonly List<SensorGraph> _bigGraphs = [];
    private readonly List<SensorGraph> _longGraphs = [];

    /// <summary>채널별 축 상태(A74) 공유 저장소 — 세 표면이 같은 눈금을 쓰게 한다.</summary>
    private readonly Dictionary<string, ChannelScale> _scales = new();

    // A70: 창(인스턴스)별 상태 — 센서 선택(A18)·채널 순서(A60 3차)·하단 바 크기(A62).
    // 전역 현재값(마지막 커밋 1벌)의 복사로 시작하고, 이 창의 조작은 즉시 전역 1벌로 커밋된다.
    private readonly HardwareInstanceState _state = HardwareInstanceState.CreateForView();

    public HardwareView(OpenContext context)
    {
        _ = context; // 파일 컨텍스트 없음
        InitializeComponent();
        BuildCenterTiles();        // 센터 그리드 타일 10개 (A60 3차 — 구 하단 카드의 후신)
        RebuildSelectionGraphs();  // 좌 대형·하단 긴 그래프 = 현재 선택(저장 복원값, ≤2)
        BuildIntervalFlyout(); // 리프레시 주기 선택 (A29)
        SetupHotkeys();        // A34: 하단 바 버튼 핫키 + 툴팁 표기
        ApplyBarScale();       // 하단 바 표시 크기 복원값 반영 (A62 — 바 크기 툴팁도 여기서)
        Loaded += (_, _) =>
        {
            HookPresenterChanged();
            Focus(FocusState.Programmatic); // F11/Esc 액셀러레이터가 바로 듣게
            if (_dataSignature.Length == 0) ShowBusy(); // 첫 데이터가 올 때까지 링 표시(A75에서 첫 로드 용도만 유지)
            // 뷰 구독(스펙+센서, A18에서 API 분리) — 구독 즉시 1회 폴링됨
            _subscription ??= HardwareModule.SubscribeSnapshots(OnSnapshot);
            // A70: 창 간 동기화 구독(TraySensors.Changed → 핀, BarScaleChanged → 배율)은 제거 —
            // 선택·바 크기는 인스턴스 독립이 사양이라 다른 창을 따라가면 안 된다.
            // 이 창의 핀 갱신은 토글 직후 UpdateTrayPins 직접 호출(이벤트 수명 문제 원천 차단).
            // A88: 맥박 렌더 루프를 붙인다(프레임마다 다시 그려 스파이크가 흐르게).
            // static 이벤트라 같은 이중 구독 방지 패턴을 그대로 쓴다.
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPulseFrame;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnPulseFrame;
            UpdateTrayPins();
        };
        Unloaded += (_, _) =>
        {
            _subscription?.Dispose(); // 마지막 뷰가 내려가면 폴러는 휴면(A101부터 구독자는 뷰뿐)
            _subscription = null;
            // A88: 반드시 해제 — CompositionTarget.Rendering은 static 이벤트라 남겨 두면
            // 이 뷰(와 붙어 있는 창 전체)가 통째로 누수되고, UI 스레드도 매 프레임 계속 깨운다.
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPulseFrame;
            // A39: 토글 버튼은 인포 모듈에만 있으므로, 뷰가 내려가면(모듈 전환 등)
            // 끌 방법이 없는 상태가 남지 않게 항상 위 고정을 해제한다.
            if (_appWindow?.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = false;
            // A61: 같은 이유로 접힘도 함께 푼다 — 접힌 채 다른 모듈로 넘어가면
            // 펼 수단(핀 버튼)이 없는 납작한 창이 남는다.
            SendCollapse(false);
            if (_appWindow is { } w) w.Changed -= OnAppWindowChanged;
            _appWindow = null;
        };
    }

    /// <summary>하단 바(Copy·긴 그래프·⛶ 등)를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(v0.42.0).</summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(ControlBar);
        return ControlBar;
    }

    // ---------- 갱신 (A42: 수집은 공유 폴러, 뷰는 스냅샷 구독) ----------

    /// <summary>폴러 스냅샷 도착 — 워커 스레드에서 불리므로 UI 스레드로 넘겨 반영한다.</summary>
    private void OnSnapshot(HardwareSnapshot snapshot)
        => DispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));

    /// <summary>
    /// UI 스레드: 첫 로드 Busy 링을 끄고, 그래프 3벌은 매 스냅샷 갱신,
    /// 스펙 리스트는 값이 지난번과 같으면 재구성을 생략한다(200ms마다 트리 재생성 방지).
    /// 겹침 방지는 폴러가 보장(단일 루프).
    /// </summary>
    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        if (_firstLoadPending)
        {
            _firstLoadPending = false;
            Busy.IsActive = false;
        }
        RecordPulse(); // 맥박 그래프(A29) — 스냅샷이 실제 도착한 타이밍 기록
        UpdateSensors(snapshot.Sensors);
        NotifyTrayStatus(); // A101: 반드시 UI 스레드(여기) — OnSnapshot(워커)에서 쏘면 안 된다
        // 폴러는 스펙 섹션을 2초 캐시로 재사용한다 — 같은 참조면 서명 계산조차 불필요
        if (ReferenceEquals(_sections, snapshot.Sections)) return;
        var signature = Signature(snapshot.Sections);
        _sections = snapshot.Sections; // 값이 같아도 참조는 갱신 — 다음 폴링부터 위 빠른 경로를 타게
        if (signature == _dataSignature) return;
        _dataSignature = signature;
        Render();
    }

    /// <summary>
    /// Busy 링은 첫 로드(첫 스냅샷 도착 전)에서만 돌린다 — 매 폴링마다 깜빡이면 안 된다.
    /// 센서 드라이버 로드·첫 WMI 수집이 1초 이상 걸릴 수 있어 빈 화면 동안의 표시는 유지(A75).
    /// </summary>
    private void ShowBusy()
    {
        _firstLoadPending = true;
        Busy.IsActive = true;
    }

    /// <summary>변경 감지용 서명 — 전 섹션의 라벨=값을 이어붙인다.</summary>
    private static string Signature(IReadOnlyList<HardwareSection> sections)
    {
        var sb = new StringBuilder();
        foreach (var section in sections)
        {
            sb.Append(section.Title).Append('\x1F');
            foreach (var item in section.Items)
                sb.Append(item.Label).Append('=').Append(item.Value).Append('\x1E');
        }
        return sb.ToString();
    }

    // ---------- 우측 구획: 스펙 라벨-값 리스트 (A60 3차 — 구 일반 모드 리스트가 이동) ----------

    private void Render()
    {
        Root.Children.Clear();
        foreach (var section in _sections)
        {
            Root.Children.Add(new TextBlock
            {
                Text = section.Title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 6),
            });

            // 라벨 폭 120: 우측 구획이 전폭의 25%라(최소 창 720에서 약 170px) 구 값 170이면
            // 값 칸이 사라진다 — 라벨은 말줄임되므로 값이 안 잘리는 쪽을 우선했다(A60 3차).
            foreach (var item in section.Items)
                Root.Children.Add(MakeItemRow(item, labelWidth: 120));
        }
    }

    /// <summary>라벨(고정폭·흐리게) + 값(줄바꿈·선택 가능) 한 줄.</summary>
    private static Grid MakeItemRow(HardwareItem item, double labelWidth)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = item.Label,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 12, 2),
        };
        var value = new TextBlock
        {
            Text = item.Value,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }

    // ---------- 하단 바 표시 크기 S/M/L (A62, v0.111.0) ----------

    // M(1.0) 기준 치수. 실제 값은 ApplyBarScale이 이 창의 배수(_state.BarScale, A70)를 곱해 정한다.
    // A60 3차: 적용 대상이 카드 10개에서 하단 긴 그래프 2개로 바뀌었다 — 상수 체계는 그대로다.
    private const double BaseCardHeight = 36;      // v0.64.2 컴팩트 카드 높이(M 단계 기준값 — 상한과 별개)
    // 그래프 높이 상한. A97(v0.116.0)에서 40 → 36, A106(v0.132.0)에서 36 → 32 — 하단 바 버튼이
    // 32가 됐고 그래프가 그보다 높으면 한 줄의 위아래 선이 어긋나 보인다(A97의 근거 그대로).
    // 44px 바(A40 불변) 안에도 그대로 들어간다.
    // ※ 결과: 상한(32)이 기준 높이(36)보다 작아져 **M·L 두 단계 모두 그래프 높이는 32**로 잘린다
    //   (S만 36 × 0.85 = 30.6으로 상한 아래). A62의 L은 이제 글씨·선 굵기만 키운다 —
    //   높이로 커지는 여지는 바 두께 44가 원래부터 막고 있었다(폭 152도 고정 — A60 3차).
    private const double MaxCardHeight = 32;
    private const double BaseTitleFontSize = 11;   // 그래프 초단축 제목
    private const double BaseValueFontSize = 13;   // 그래프 값
    private const double BaseSmallFontSize = 10;   // 타일 핀 아이콘(A18) + 축 라벨(A74)
    private const double BaseStrokeThickness = 1.5; // 스파크라인·맥박 선 굵기
    private const double BaseBarIconFontSize = 18;  // 하단 바 아이콘(A27 규격 버튼 안)

    /// <summary>
    /// 크기 버튼 클릭 = S → M → L → S 순환(A62). A70: **이 창만** 바뀐다 — 열려 있는 다른
    /// 정보 창은 따라오지 않고, 저장은 전역 1벌(마지막 커밋 우선)로 남는다.
    /// </summary>
    private void OnBarScaleClick(object sender, RoutedEventArgs e) => CycleBarScaleLocal();

    /// <summary>버튼 클릭과 B 키(A34)의 공용 진입로 — 인스턴스 단계 순환 + 이 창에 즉시 반영(A70).</summary>
    private void CycleBarScaleLocal()
    {
        _state.CycleBarScale();
        ApplyBarScale();
    }

    /// <summary>
    /// 현재 단계를 하단 바 요소에 반영한다(A62). 바 두께 44는 불변(A40)이므로 **바 안 요소**의
    /// 글씨 크기·선 굵기·그래프 높이(최대 32 — A97 → A106)만 바뀐다(긴 그래프 폭 152는 고정).
    /// 축 라벨 임계(A74)가 배수를 타므로 마지막에 스파크라인을 다시 그린다.
    /// 버튼 아이콘 크기도 단계를 따라 커져 툴팁 없이도 지금 단계가 보인다.
    /// 전역 UI 배율(A41 UiScale)은 건드리지 않는다 — 별개의 배수(A62 확정).
    /// 센터 타일·좌 대형 그래프는 하단 바 밖이라 배수 비적용 — A62의 목적이
    /// "A61 상시 표시 바의 가독성"이라 바 안 요소 전용이 맞다.
    /// </summary>
    private void ApplyBarScale()
    {
        var scale = _state.BarScale;
        foreach (var graph in _longGraphs)
            ApplyScaleToLongGraph(graph, scale);
        PulseHost.Height = Math.Min(MaxCardHeight, BaseCardHeight * scale); // 맥박도 같은 높이 유지 (v0.64.2 규격)
        PulseLine.StrokeThickness = BaseStrokeThickness * scale;
        BarScaleIcon.FontSize = BaseBarIconFontSize * scale;
        // A34: 표기는 키 상수에서 조립한다(단계 표시가 바뀌어도 키 표기는 어긋나지 않는다).
        ToolTipService.SetToolTip(BarScaleButton, HotkeySupport.Tip(
            $"Bottom bar size: {HardwareInstanceState.BarScaleSteps[_state.BarScaleIndex].Label}",
            BarScaleKey));

        RerenderSparklines(); // 축 라벨 표시 임계값(A74)·선 굵기를 다음 스냅샷 전에 반영
    }

    /// <summary>
    /// 하단 긴 그래프 1개에 현재 단계(A62)를 반영한다 — 생성 직후(RebuildSelectionGraphs)와
    /// 단계 순환(ApplyBarScale)의 공용 경로. 배수 적용 대상 = 글씨·선 굵기·높이(상한 32 클램프,
    /// A106 — A40 바 두께 44 불변). **폭 152는 고정**(배수 비적용): 구 카드의 폭 배수는 "최소 폭
    /// × 개수 축소" 알고리즘의 하한이었고, 고정 2개인 긴 그래프가 폭까지 커지면 L 단계 ×
    /// 최소 창 720에서 star 칸(약 358px)을 넘친다 — 제목은 말줄임, 값은 152 안에 들어간다.
    /// </summary>
    private void ApplyScaleToLongGraph(SensorGraph graph, double scale)
    {
        graph.Root.Height = Math.Min(MaxCardHeight, BaseCardHeight * scale);
        graph.Root.Width = LongCardWidth;
        graph.TitleText.FontSize = BaseTitleFontSize * scale;
        graph.ValueText.FontSize = BaseValueFontSize * scale;
        graph.YAxisText.FontSize = BaseSmallFontSize * scale;
        graph.XAxisText.FontSize = BaseSmallFontSize * scale;
        graph.Line.StrokeThickness = BaseStrokeThickness * scale;
    }

    // ---------- 그래프 시간 창 (A60 3차 — A71 흡수: 3단 차등) ----------

    /// <summary>좌 대형 그래프 시간 창 상한(ms) — 사양 10초.</summary>
    private const double BigWindowMaxMs = 10_000;

    /// <summary>센터 타일 시간 창 상한(ms) — 사양 30초.</summary>
    private const double TileWindowMaxMs = 30_000;

    /// <summary>하단 긴 그래프 시간 창 상한(ms) — 사양 5분(기본 주기 500ms에서 링 600개 = 정확히 5분).</summary>
    private const double LongWindowMaxMs = 300_000;

    /// <summary>
    /// 표면별 실제 시간 창 = min(사양 창, 이력 링(<see cref="SensorService.HistoryCapacity"/>) × 주기)
    /// — A74 ③ 원칙 그대로: 최단 주기 50ms에서는 링이 30초뿐이라 하단 5분 창도 30초로 줄고,
    /// x축 표기(A74)도 이 계산값을 그대로 쓴다(하드코딩 금지).
    /// </summary>
    private static TimeSpan WindowFor(double maxMs) => TimeSpan.FromMilliseconds(
        Math.Min(maxMs, (double)SensorService.HistoryCapacity * HardwareModule.RefreshMs));

    /// <summary>
    /// 축 라벨(A74)을 표시하는 최소 그래프 폭(M 기준). 이보다 좁으면 라벨 두 개가 셀을 다 덮어
    /// 그래프가 안 읽힌다 — A40의 "좁으면 축약" 관례와 같은 방식으로 숨긴다.
    /// A62: 하단 바 표면은 글씨가 커지면 같은 폭에서 더 많이 가리므로 임계값에도 배수를 곱한다
    /// (AxisMinWidthNow). 센터 타일·좌 대형은 바 밖(배수 비적용)이라 이 기준값을 그대로 쓴다.
    /// </summary>
    private const double AxisMinWidth = 90;

    /// <summary>현재 단계(A62)를 반영한 하단 바 표면의 축 라벨 임계 폭 — 단계가 창별이라(A70) 인스턴스 멤버다.</summary>
    private double AxisMinWidthNow => AxisMinWidth * _state.BarScale;

    // ---------- 센터 그래프 그리드 (A60 3차 — 전 채널 타일·선택·드래그 순서) ----------

    /// <summary>
    /// 하단 긴 그래프 폭 = 구 카드 최소 폭 76의 2배(사용자 확정 "기존 카드의 2배 정도").
    /// A62 단계와 무관하게 고정 — 이유는 ApplyScaleToLongGraph 주석 참조.
    /// </summary>
    private const double LongCardWidth = 152;

    /// <summary>SensorGrid의 ColumnSpacing과 같은 값 — 맥박 숨김 임계 폭 계산에 쓴다.</summary>
    private const double CardSpacing = 8;

    /// <summary>
    /// 하단 바(BarGrid)에서 긴 그래프 칸(star)을 뺀 **고정 요소들의 폭 합**. A62 배수와 무관하다 —
    /// 버튼 규격은 A27(→A97·A106 개정)이 못 박아 두었고 배율은 그래프 쪽에만 곱하기 때문.
    /// A97(v0.116.0)에서 1칸 버튼 40→36 · 간격 10→6, A106(v0.132.0)에서 1칸 버튼 36→32가 되어
    /// 그때마다 **재산정**했다. 값을 이월 계산하지 않는다 — A97 이전 값 458은 v0.94.0(A40)
    /// 산정치 1240에서 역산한 근사치라 실제 합보다 34 컸던 전력이 있다.
    /// A106 재계수(HardwareView.xaml의 BarGrid 8칸 중 star 칸인 SensorGrid c2만 제외):
    ///   Copy c0 32 + Busy(ProgressRing) c1 20 + 맥박 c3 90 + 주기(2칸) c4 84
    ///   + 크기 c5 32 + 핀 c6 32 + ⛶ c7 32 = 322
    ///   + ColumnSpacing 6 × 7칸 사이 = 42  →  **364**  (A97 값은 338 + 42 = 380이었다)
    /// ⚠️ BarGrid.ActualWidth 기준이므로 셸의 ModuleBarHost Margin(A106에서 82/12)은 여기 포함되지 않는다.
    /// HardwareView.xaml의 BarGrid 구성이 바뀌면 이 합도 함께 고칠 것.
    /// </summary>
    private const double BarFixedWidth = 364;

    /// <summary>하단 긴 그래프(≤2)가 차지하는 폭 합 — 폭 고정(배수 비적용)이라 개수에만 비례한다.</summary>
    private double LongGraphsWidth
        => LongCardWidth * _longGraphs.Count + CardSpacing * Math.Max(0, _longGraphs.Count - 1);

    /// <summary>
    /// A40: 하단 바 폭이 좁으면 장식성 요소부터 내린다 — 맥박 그래프(A29)는 긴 그래프(≤2, A60 3차)가
    /// 전부 들어갈 폭이 안 되면 숨긴다(그래프 = 정보, 맥박 = 장식이므로 그래프가 우선).
    /// 임계값 = 긴 그래프 폭 합(2개 = 312, 배수 무관) + 고정 요소 합(BarFixedWidth 364) = 676 —
    /// 최소 창 720(BarGrid 약 626)에서는 맥박이 내려가고, 맥박 폭 96이 비면 긴 그래프 2개가 들어간다.
    /// 구 카드 10개의 "뒤 순서부터 숨김" 수 축소 로직은 소멸 — 긴 그래프는 2개 고정이라 접을 것이 없다.
    /// PulseHost 표시 여부는 BarGrid(부모가 정하는 폭) 기준이라 피드백 루프가 없다(기존 그대로).
    /// </summary>
    private void UpdateBarDensity(double width)
        => PulseHost.Visibility = width >= LongGraphsWidth + BarFixedWidth
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>센터 그리드 열 수 — 셸 사이드바 신호(ISidebarAwareView)가 4/8을 정한다.
    /// 기본 8 = 도크 하나라도 닫힘(전폭) 기준 — ThumbnailExplorer(A93)와 같은 초기값.</summary>
    private int _centerColumns = 8;

    /// <summary>
    /// 셸 푸시(A60 3차 신설 계약): 좌·우 사이드바가 둘 다 열려 메인이 절반 폭이면 4열, 아니면 8열
    /// (A93 썸네일과 같은 판정). 호출원은 셸 ApplyOverlayStates(사이드바 상태 변경의 단일 종착점).
    /// 값이 그대로면 재배치하지 않는다.
    /// ※ 현행 셸은 정보 모듈에 사이드바를 안 띄우므로(파일 컨텍스트 게이트) 지금은 항상 8열이다 —
    /// 셸 정책이 바뀌면 이 배선으로 4열이 저절로 산다.
    /// </summary>
    public void SetSidebarsState(bool bothOpen)
    {
        var columns = bothOpen ? 4 : 8;
        if (columns == _centerColumns) return;
        _centerColumns = columns;
        LayoutCenterTiles();
    }

    /// <summary>
    /// 센터 타일 10개를 만든다(A60 3차). 채널 정의(제목·색·선택자·포맷·스케일)는
    /// SensorChannels 단일 소스(A18에서 트레이와 공용화). 순서는 인스턴스 상태(_state.Order —
    /// 드래그로 바뀐 저장 순서)가 정하고 배치는 LayoutCenterTiles가 한다.
    /// 하단 바 밀도(맥박 숨김)는 BarGrid 폭 변화를 따라간다(A40).
    /// </summary>
    private void BuildCenterTiles()
    {
        foreach (var channel in SensorChannels.All)
            _tiles.Add(MakeTileGraph(channel));
        BarGrid.SizeChanged += (_, e) => UpdateBarDensity(e.NewSize.Width);
        LayoutCenterTiles();
    }

    /// <summary>
    /// 센터 타일을 현재 순서(_state.Order)·열 수(4/8)대로 star 칸에 배치한다.
    /// 행도 star라 그리드가 구획을 꽉 채운다(A60 3차: 최소 창 720×540 기준 꽉 참, DPI 무관).
    /// Children을 비우고 다시 얹으므로 중복 Add가 원천적으로 없다 — FrameworkElement.Parent를
    /// 가드로 쓰지 않는다(§3.4: 라이브 트리 부착 전엔 Add 뒤에도 null이라 못 쓴다 — v0.113.2 교훈).
    /// </summary>
    private void LayoutCenterTiles()
    {
        var order = _state.Order;
        var columns = _centerColumns;
        var rows = Math.Max(1, (_tiles.Count + columns - 1) / columns);

        CenterGrid.Children.Clear();
        CenterGrid.ColumnDefinitions.Clear();
        CenterGrid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++)
            CenterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows; r++)
            CenterGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var slot = 0;
        foreach (var id in order)
        {
            if (TileById(id) is not { } tile) continue; // 순서는 정규화로 전 채널 보장 — 방어만
            Grid.SetColumn(tile.Root, slot % columns);
            Grid.SetRow(tile.Root, slot / columns);
            CenterGrid.Children.Add(tile.Root);
            slot++;
        }
    }

    /// <summary>채널 ID로 센터 타일 찾기 — 타일은 전 채널 1:1이라 실패는 방어 경로뿐.</summary>
    private SensorGraph? TileById(string id)
    {
        foreach (var tile in _tiles)
            if (tile.Channel.Id == id) return tile;
        return null;
    }

    /// <summary>
    /// 센터 타일 1개: 공용 그래프 표면 + 클릭 토글 + 드래그 재정렬.
    /// 클릭 = 선택 토글(A18 규칙 승계: 최대 2, 가득이면 오래된 것 밀림) — 좌 대형·하단 긴
    /// 그래프·핀 배지·트레이(A101)가 전부 이 선택 하나를 따른다(선택 단일화).
    /// </summary>
    private SensorGraph MakeTileGraph(SensorChannel channel)
    {
        var graph = MakeGraph(channel, TileWindowMaxMs, inBar: false, withPin: true);
        // 클릭 토글과 드래그는 제스처가 겹치지 않는다 — 드래그는 이동 임계를 넘어야 시작되고,
        // 실제로 시작된 포인터 시퀀스에서는 Tapped가 발화하지 않는다(탭 판정 탈락).
        graph.Root.Tapped += (_, _) =>
        {
            _state.Toggle(channel.Id);
            OnSelectionChanged();
        };
        ToolTipService.SetToolTip(graph.Root,
            $"{channel.Title} - click to select (up to 2), drag to reorder");
        AttachTileDrag(graph, channel);
        return graph;
    }

    // ---------- 센터 타일 드래그 순서 변경 (A60 3차 — A94의 CanDrag+DragStarting 선례 준용) ----------

    /// <summary>드래그 중인 타일의 채널 ID — 내부 재정렬 표식. DragStarting에서 채우고 Drop에서 지운다.</summary>
    private string? _dragChannelId;

    /// <summary>
    /// 타일에 드래그 재정렬을 건다(A60 3차). 방식은 저장소 선례(A94: 컨테이너 CanDrag +
    /// DragStarting, 페이로드는 DataPackage — 여기선 SetText로 채널 ID)를 준용한 최소 구현:
    /// 타일을 끌어 다른 타일에 놓으면 그 자리로 끼워 넣는다(MoveTo — 커밋은 드롭 확정 시 1회).
    /// 내부 재정렬만 받는다 — OS 파일 드래그 등 외부 소스는 표식·Text 페이로드 검사로 거르고
    /// 그대로 지나가게 둔다(창 수준 "열기" 폴백 몫).
    /// ※ 드래그가 밖에서 취소되면 표식이 다음 DragStarting까지 남는다 — 그 사이 외부 텍스트
    /// 드래그가 타일에 떨어지는 극단 케이스는 재정렬 1회로 무해하다(파일 조작 아님).
    /// </summary>
    private void AttachTileDrag(SensorGraph graph, SensorChannel channel)
    {
        graph.Root.CanDrag = true;
        graph.Root.DragStarting += (_, args) =>
        {
            _dragChannelId = channel.Id;
            args.Data.SetText(channel.Id);
            args.Data.RequestedOperation = DataPackageOperation.Move;
        };
        graph.Root.AllowDrop = true;
        graph.Root.DragOver += (_, e) =>
        {
            if (_dragChannelId is null || !e.DataView.Contains(StandardDataFormats.Text)) return;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.Handled = true;
        };
        graph.Root.Drop += (_, e) =>
        {
            if (_dragChannelId is not { } dragged) return;
            _dragChannelId = null; // 1회성 — 취소·완료 어느 쪽이든 다음 드래그가 다시 채운다
            e.Handled = true;
            if (dragged == channel.Id) return; // 제자리 드롭 — 커밋 없음
            _state.MoveTo(dragged, channel.Id); // 저장 커밋은 여기(드롭당 1회) — 드래그 중 난사 없음
            LayoutCenterTiles();
        };
    }

    // ---------- 선택 그래프 (A60 3차: 좌 대형 ≤2 + 하단 긴 ≤2 — A17 카드 대체) ----------

    /// <summary>
    /// 타일 클릭 토글 직후의 공용 후처리(A60 3차) — 선택 단일화의 실행부:
    /// 핀 배지(구 카드 핀의 후신)·좌 대형 그래프·하단 긴 그래프·창별 트레이(A101)가
    /// 전부 HardwareInstanceState.Selection 하나를 따라 갱신된다.
    /// </summary>
    private void OnSelectionChanged()
    {
        UpdateTrayPins();
        RebuildSelectionGraphs();
        NotifyTrayStatus();
    }

    /// <summary>
    /// 선택(≤2, 선택 순서 = 트레이 줄 순서와 동일)이 바뀔 때 좌 대형·하단 긴 그래프를 다시
    /// 만든다. 표면은 매번 새로 만들고 옛것은 컨테이너 Clear로 버린다(표면에 이벤트 구독 없음 —
    /// 수명 문제 없음). 축 상태(ChannelScale)는 채널별 공유라 재선택해도 눈금이 이어진다(A74).
    /// 만들고 나서 이력으로 즉시 1회 그린다 — 다음 스냅샷(최대 5초, A73)을 기다리지 않는다.
    /// </summary>
    private void RebuildSelectionGraphs()
    {
        // 좌 대형(A72 흡수): 줄당 1개, 행 star — 선택 0개면 안내 1줄만 남긴다.
        BigGraphPanel.Children.Clear();
        BigGraphPanel.RowDefinitions.Clear();
        _bigGraphs.Clear();
        foreach (var id in _state.Selection)
        {
            if (SensorChannels.ById(id) is not { } channel) continue; // 미지 ID 방어(구 관례)
            var graph = MakeGraph(channel, BigWindowMaxMs, inBar: false, withPin: false);
            ToolTipService.SetToolTip(graph.Root, channel.Title);
            BigGraphPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(graph.Root, _bigGraphs.Count);
            BigGraphPanel.Children.Add(graph.Root);
            _bigGraphs.Add(graph);
        }
        BigGraphHint.Visibility = _bigGraphs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 하단 긴 그래프(A71·A72 흡수의 하단 축): 폭 152 고정, SensorGrid(가운데 정렬) 안.
        // 선택 0개면 바 중앙은 조용히 빈다(안내 없음 — 확정 사양).
        SensorGrid.Children.Clear();
        SensorGrid.ColumnDefinitions.Clear();
        _longGraphs.Clear();
        foreach (var id in _state.Selection)
        {
            if (SensorChannels.ById(id) is not { } channel) continue;
            var graph = MakeGraph(channel, LongWindowMaxMs, inBar: true, withPin: false);
            ToolTipService.SetToolTip(graph.Root, channel.Title);
            ApplyScaleToLongGraph(graph, _state.BarScale); // 생성 직후 현재 단계 반영(A62)
            SensorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(graph.Root, _longGraphs.Count);
            SensorGrid.Children.Add(graph.Root);
            _longGraphs.Add(graph);
        }
        UpdateBarDensity(BarGrid.ActualWidth); // 그래프 개수(0~2)에 따라 맥박 임계가 달라진다
        // 새 표면은 레이아웃 전이라 ActualWidth가 0이다 — 동기로 한 번 실체화해야 아래의 즉시
        // 렌더가 헛돌지 않는다(ThumbnailExplorer.ShowEntries의 UpdateLayout 선례. 5000ms 주기에서
        // 다음 스냅샷까지 최대 5초를 빈 그래프로 기다리지 않기 위해서다). 뷰가 트리 밖(생성자)이면
        // 무동작이고, 그때는 첫 스냅샷 도착이 그린다.
        BigGraphPanel.UpdateLayout();
        SensorGrid.UpdateLayout();
        RerenderSparklines();
    }

    /// <summary>
    /// 그래프 표면 1개를 만든다 — 구 카드 생성 코드의 골격을 타일·좌 대형·하단 긴 그래프가
    /// 공용한다(A60 3차). 그래프가 표면 전체를 채우고 제목·값이 그 위에 얹힌다(v0.64.2 컴팩트형).
    /// 글씨·선 굵기는 M 기준값 — 하단 긴 그래프만 직후 ApplyScaleToLongGraph가 배수를 덮어쓴다(A62).
    /// 타일·좌 대형은 크기를 지정하지 않는다 — star 칸이 늘려 채운다(꽉 참·DPI 무관).
    /// </summary>
    private SensorGraph MakeGraph(SensorChannel channel, double windowMaxMs, bool inBar, bool withPin)
    {
        var accent = channel.Accent;
        var stroke = new SolidColorBrush(accent);
        var fill = new SolidColorBrush(Windows.UI.Color.FromArgb(56, accent.R, accent.G, accent.B));

        var titleText = new TextBlock
        {
            Text = channel.ShortTitle, // 초단축 제목(v0.64.3) — 전체 이름은 툴팁에
            FontSize = BaseTitleFontSize,
            Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 선택 핀 배지(A18 → A60 3차에서 카드 → 센터 타일로 이동): 이 채널이 선택 중이면 보인다.
        FontIcon? pinIcon = null;
        if (withPin)
            pinIcon = new FontIcon
            {
                Glyph = "\uE718",
                FontSize = BaseSmallFontSize,
                Foreground = stroke,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };
        var valueText = new TextBlock
        {
            Text = "—",
            FontSize = BaseValueFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(valueText, 2);
        header.Children.Add(titleText);
        if (pinIcon is not null)
        {
            Grid.SetColumn(pinIcon, 1);
            header.Children.Add(pinIcon);
        }
        header.Children.Add(valueText);
        header.VerticalAlignment = VerticalAlignment.Top; // 그래프 위 상단 겹침 — 큰 표면에서도 읽힌다

        var line = new Polyline { Stroke = stroke, StrokeThickness = BaseStrokeThickness };
        var area = new Polygon { Fill = fill };

        // 축 스케일 라벨(A74): 눈금선·축선은 그리지 않는다(작은 셀이 지저분해진다).
        // 좌상단 = y 최대값 + 단위("100°C"), 우하단 = x 시간 범위("30s"). y 하한 0은 자명해 생략.
        // 값·표시 여부는 RenderSparkline이 채운다 — 여기선 빈 채로 만들어 둔다.
        var yAxisText = new TextBlock
        {
            FontSize = BaseSmallFontSize,
            Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        var xAxisText = new TextBlock
        {
            FontSize = BaseSmallFontSize,
            Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
        };

        var graphHost = new Grid(); // 표면 전체가 그래프 — 텍스트는 그 위에 겹친다(v0.64.2 컴팩트형)
        graphHost.Children.Add(area);
        graphHost.Children.Add(line);
        graphHost.Children.Add(yAxisText); // 선 위에 얹어 겹쳐도 읽히게 (Opacity 0.55)
        graphHost.Children.Add(xAxisText);

        var panel = new Grid();
        panel.Children.Add(graphHost);
        panel.Children.Add(header);

        var root = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 2, 6, 2),
            Opacity = 0.45, // 값이 들어오면 1로 (채널별 공유 상태 HasEverHadValue)
            Child = panel,
        };
        // 하단 바 표면의 y축 라벨은 상단 겹침 헤더와 겹칠 수 있어, 좁은 바 높이(32)에서는
        // 헤더를 수직 중앙으로 되돌린다 — 구 카드와 같은 배치.
        if (inBar) header.VerticalAlignment = VerticalAlignment.Center;

        return new SensorGraph
        {
            Root = root,
            TitleText = titleText,
            ValueText = valueText,
            Pin = pinIcon,
            GraphHost = graphHost,
            Line = line,
            Area = area,
            YAxisText = yAxisText,
            XAxisText = xAxisText,
            Channel = channel,
            Scale = ScaleFor(channel),
            WindowMaxMs = windowMaxMs,
            InBar = inBar,
        };
    }

    /// <summary>채널별 공유 축 상태를 얻는다(없으면 A74 시작 규칙으로 생성:
    /// 고정 스케일 채널(온도·%)은 100, 나머지는 채널별 하한).</summary>
    private ChannelScale ScaleFor(SensorChannel channel)
    {
        if (!_scales.TryGetValue(channel.Id, out var scale))
        {
            scale = new ChannelScale
            {
                AxisMax = channel.FixedMax > 0 ? channel.FixedMax : channel.AutoFloor,
            };
            _scales[channel.Id] = scale;
        }
        return scale;
    }

    /// <summary>이 창의 선택(A70: 인스턴스 상태)대로 센터 타일의 핀 배지를 맞춘다(A18 →
    /// A60 3차에서 카드 핀의 의미가 타일로 이동). 창 간 이벤트 구독은 없다 —
    /// Loaded 1회 + 토글 직후 직접 호출이 전부다.</summary>
    private void UpdateTrayPins()
    {
        foreach (var tile in _tiles)
            if (tile.Pin is { } pin)
                pin.Visibility = _state.IsSelected(tile.Channel.Id)
                    ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 트레이 아이콘 내용 (A101: 창별 아이콘이 이 창의 선택값 표시) ----------

    /// <summary>
    /// 선택 0개일 때의 유휴 표기 — 셸의 미구현 화면 폴백 표(MainWindow.IdleTrayLabel의
    /// "hardware" 행)와 같은 문자열이어야 한다: 어느 경로로 그려져도 결과가 같게(이중 규칙 방지).
    /// </summary>
    private const string IdleLabel = "INF";

    /// <summary>마지막으로 통지한 표기 키(구 SensorTray의 ComposeKey 방식) — 같으면 통지 생략.</summary>
    private string _trayKey = "";

    public event Action? TrayStatusChanged;

    /// <summary>
    /// 이 창의 선택(A70 인스턴스 상태, 최대 2)을 초압축 표기(FormatCompact) 두 줄로 내준다(A101).
    /// UI 스레드에서 호출된다(계약) — _lastFrame(UI 스레드 대입)과 _state.Selection(불변 스냅샷)만
    /// 읽으므로 경합이 없다. 구 SensorTray 관례 승계: 값을 못 구한 줄은 "—"(Open이 null을 채운다).
    /// 선택 1개면 아래 줄이 "—"다 — 계약상 열림은 2줄 고정(단줄 열림 없음)이고, A60 3차에서도
    /// 확장이 필요 없었다(선택 축은 그대로 두고 화면 축만 새로 얹었다). 같은 이유로 줄별 채널
    /// 색·툴팁도 싣지 않는다 — TrayStatus에 색·툴팁 자리가 없어 글자는 모듈 액센트 1색(셸 합성).
    /// </summary>
    public TrayStatus GetTrayStatus()
    {
        string? line1 = null, line2 = null;
        var count = 0;
        foreach (var id in _state.Selection)
        {
            if (SensorChannels.ById(id) is not { } channel) continue; // 미지 ID 방어(구 관례)
            var value = _lastFrame.Timestamp == DateTime.MinValue ? null : channel.Select(_lastFrame);
            var text = value is { } v ? channel.FormatCompact(v) : null;
            if (count == 0) line1 = text ?? TrayStatus.Unknown;
            else line2 = text ?? TrayStatus.Unknown;
            if (++count == HardwareInstanceState.MaxSelected) break;
        }
        return count == 0 ? TrayStatus.Idle(IdleLabel) : TrayStatus.Open(line1, line2);
    }

    /// <summary>
    /// 표기 키를 다시 계산해 바뀌었을 때만 셸에 알린다(A101). 키에 채널 ID를 포함해
    /// "표기는 같은데 채널이 바뀐" 토글도 잡는다(구 SensorTray.ComposeKey와 같은 구성).
    /// 두 갱신원(스냅샷 도착·선택 토글) 모두 **UI 스레드의 이 깔때기 하나**로 모은다 —
    /// 계약상 이벤트는 스레드 무보장이지만 GetTrayStatus는 UI 스레드 호출이라, 워커 단계
    /// (OnSnapshot 디스패치 전)에서 쏘면 셸이 읽는 시점과 값이 어긋날 수 있기 때문.
    /// 뷰 Unloaded 뒤 잔여 디스패치가 쏴도 셸 구독 핸들러의 현재 뷰 가드가 걸러 준다(A54 배선).
    /// </summary>
    private void NotifyTrayStatus()
    {
        var parts = new List<string>();
        foreach (var id in _state.Selection)
        {
            if (SensorChannels.ById(id) is not { } channel) continue;
            var value = _lastFrame.Timestamp == DateTime.MinValue ? null : channel.Select(_lastFrame);
            parts.Add($"{id}={(value is { } v ? channel.FormatCompact(v) : TrayStatus.Unknown)}");
        }
        var key = string.Join('|', parts);
        if (key == _trayKey) return;
        _trayKey = key;
        TrayStatusChanged?.Invoke();
    }

    /// <summary>매 스냅샷: 승격 안내 표시 여부 + 그래프 3벌(타일·좌 대형·하단 긴)의 값·스파크라인 갱신.</summary>
    private void UpdateSensors(SensorFrame frame)
    {
        _lastFrame = frame;

        // 비관리자여서 커널 드라이버 의존 채널이 비어 있으면 안내 행을 보여준다.
        // (관리자인데도 비면 하드웨어가 그 값을 안 주는 것 — 버튼을 내밀지 않는다)
        var needsAdmin = !SensorService.IsElevated
            && frame.Timestamp != DateTime.MinValue
            && (frame.CpuTemp is null || frame.CpuPower is null
                || frame.FanRpm is null || frame.SsdTemp is null);
        AdminRow.Visibility = needsAdmin ? Visibility.Visible : Visibility.Collapsed;
        UpdateStripVisibility();

        var history = SensorService.History();
        foreach (var graph in _tiles) UpdateGraph(graph, frame, history);
        foreach (var graph in _bigGraphs) UpdateGraph(graph, frame, history);
        foreach (var graph in _longGraphs) UpdateGraph(graph, frame, history);
    }

    // A70: RenderSparkline이 인스턴스 배수(AxisMinWidthNow)를 읽게 되면서 이 호출 연쇄
    // (UpdateSensors → UpdateGraph → RenderSparkline)는 인스턴스 메서드다.
    private void UpdateGraph(SensorGraph graph, SensorFrame frame, SensorFrame[] history)
    {
        var value = frame.Timestamp == DateTime.MinValue ? null : graph.Channel.Select(frame);
        graph.ValueText.Text = value is { } v ? graph.Channel.FormatFull(v) : "—";
        if (value is not null) graph.Scale.HasEverHadValue = true;
        graph.Root.Opacity = graph.Scale.HasEverHadValue ? 1.0 : 0.45;
        RenderSparkline(graph, history);
    }

    /// <summary>
    /// 이력을 표면 폭에 맞춰 꺾은선 + 면으로 그린다. x는 시간 비례(주기가 바뀌어도 올바름 —
    /// A29 대비), y는 채널별 축 상한(A74: %는 0~100 고정 / 온도는 100 시작 + 초과 시 확장 /
    /// 그 외는 1·2·5 눈금 올림, 셋 다 세션 내 단조 증가) 기준. A60 3차: 상한은 채널별 공유
    /// (ChannelScale)라 세 표면(타일 30초·좌 10초·하단 5분)의 축 눈금이 늘 같다 — 긴 창 표면이
    /// 본 과거 최대가 짧은 창 표면에도 적용되는 것은 단조 증가 규칙의 자연스러운 결과다.
    /// 시간 창은 표면별 상한과 링이 담는 시간 중 짧은 쪽(WindowFor — A74 ③).
    /// 그리고 나서 모서리 축 라벨을 갱신한다(A74). 레이아웃 전(폭 0)엔 그리지 않는다.
    /// </summary>
    private void RenderSparkline(SensorGraph graph, SensorFrame[] history)
    {
        var w = graph.GraphHost.ActualWidth;
        var h = graph.GraphHost.ActualHeight;
        if (w <= 2 || h <= 2 || history.Length == 0)
        {
            ClearSparkline(graph);
            return;
        }

        var window = WindowFor(graph.WindowMaxMs); // 한 렌더 안에서는 같은 값 — 주기 변경과 겹쳐도 x가 어긋나지 않게
        var now = history[^1].Timestamp;
        var start = now - window;

        // 창 안 첫 표본 인덱스 — 이력은 시간 오름차순이라 뒤에서부터 경계를 찾는다.
        // 표본 상한(150)도 "창 안 표본 수" 기준으로 세야 좁은 창(좌 10초)이 전체 링 기준
        // 걸러내기 때문에 점이 모자라는 일이 없다(A60 3차에서 창이 표면별로 갈라진 결과).
        var begin = history.Length;
        while (begin > 0 && history[begin - 1].Timestamp >= start) begin--;

        // 축 상한 갱신(A74): 창 안의 관측값이 현재 상한을 넘을 때만 올린다. 한 번 올라간 상한은
        // 세션 중 내려오지 않는다 — 값이 튈 때마다 그래프 전체가 출렁이는 것을 막기 위해서다.
        var scale = graph.Scale;
        for (var i = begin; i < history.Length; i++)
            if (graph.Channel.Select(history[i]) is { } observed && observed > scale.AxisMax)
                scale.AxisMax = AxisCeiling(graph.Channel, observed);
        var max = scale.AxisMax;
        if (max <= 0) max = 1;

        var linePoints = new PointCollection();
        var areaPoints = new PointCollection();
        double firstX = -1, lastX = -1;
        // 표본 상한 ~150개: 그리기 비용 억제 (예: 500ms 주기 5분 창 = 600표본 → 4개에 1개)
        var step = Math.Max(1, (history.Length - begin) / 150);
        for (var i = begin; i < history.Length; i += step)
        {
            var f = history[i];
            if (graph.Channel.Select(f) is not { } v) continue; // null 구간은 건너뛴다(선이 이어짐)

            var x = (f.Timestamp - start).TotalSeconds / window.TotalSeconds * w;
            var y = h - Math.Clamp(v / max, 0f, 1f) * h;
            linePoints.Add(new Windows.Foundation.Point(x, y));
            areaPoints.Add(new Windows.Foundation.Point(x, y));
            if (firstX < 0) firstX = x;
            lastX = x;
        }

        if (linePoints.Count < 2)
        {
            ClearSparkline(graph);
            return;
        }
        // 면은 선 아래를 바닥까지 닫는다
        areaPoints.Add(new Windows.Foundation.Point(lastX, h));
        areaPoints.Add(new Windows.Foundation.Point(firstX, h));
        graph.Line.Points = linePoints;
        graph.Area.Points = areaPoints;

        // 축 라벨(A74): 좌상단 y 최대값 + 단위, 우하단 x 시간 범위. 표면이 좁으면(그래프 폭
        // 임계 미만 — 하단 바 표면만 A62 배수 적용) 숨긴다. 값이 한 번도 없던 채널도 숨긴다
        // (축만 떠 있으면 오히려 오해를 준다).
        var showAxis = w >= (graph.InBar ? AxisMinWidthNow : AxisMinWidth) && scale.HasEverHadValue;
        if (showAxis)
        {
            graph.YAxisText.Text = $"{max:0}{graph.Channel.AxisUnit}";
            graph.XAxisText.Text = FormatSpan(window);
        }
        graph.YAxisText.Visibility = showAxis ? Visibility.Visible : Visibility.Collapsed;
        graph.XAxisText.Visibility = showAxis ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>그릴 게 없을 때: 선·면과 축 라벨(A74)을 함께 비운다.</summary>
    private static void ClearSparkline(SensorGraph graph)
    {
        graph.Line.Points = null;
        graph.Area.Points = null;
        graph.YAxisText.Visibility = Visibility.Collapsed;
        graph.XAxisText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 채널별 축 상한 규칙(A74).
    /// · 백분율(%)은 0~100 고정 — 100%가 정의상 최대라 넘겨도 확장하지 않는다(클램프로 그린다).
    /// · 온도(FixedMax가 있는 나머지 = ℃)는 0~100 고정이되 초과 관측 시에만 10 단위로 확장 —
    ///   여기에 1·2·5 눈금을 쓰면 105℃ 한 번에 축이 200℃가 되어 그래프가 바닥에 깔린다.
    /// · 그 외(W·RPM·MHz)는 관측 최대를 1·2·5 × 10ⁿ 눈금으로 올림(NiceCeiling).
    /// </summary>
    private static float AxisCeiling(SensorChannel channel, float value)
    {
        if (channel.AxisUnit == "%") return 100;
        if (channel.FixedMax > 0) return (float)(Math.Ceiling(value / 10.0) * 10.0);
        return NiceCeiling(value);
    }

    /// <summary>
    /// 값을 1·2·5 × 10ⁿ 눈금으로 올린다(A74). 예: 47→50, 63→100, 250→500, 1500→2000, 4550→5000.
    /// 차트 축의 관례적인 눈금이라 사람이 한눈에 읽고, 값이 조금 튀어도 상한이 자주 바뀌지 않는다.
    /// </summary>
    private static float NiceCeiling(float value)
    {
        if (value <= 0) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / power; // 1 이상 10 미만
        var step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return (float)(step * power);
    }

    /// <summary>x축 시간 범위 표기(A74) — 60초를 넘으면 분으로. 예: "10s"·"30s"·"5m".</summary>
    private static string FormatSpan(TimeSpan span)
        => span.TotalSeconds > 60 ? $"{span.TotalMinutes:0.#}m" : $"{span.TotalSeconds:0}s";

    /// <summary>
    /// 관리자 재시작(A17): 단일 인스턴스 키를 먼저 반납해야 새(관리자) 프로세스가
    /// 이 인스턴스로 리다이렉트되어 죽는 걸 막을 수 있다. UAC를 취소하면 키를 되찾고 계속.
    /// </summary>
    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().UnregisterKey();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch
        {
            // UAC 취소 — 유일한 인스턴스이므로 키를 되찾는다 (Program.InstanceKey와 동일해야 함)
            Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("KOTU-Main");
            return;
        }
        SensorService.Shutdown(); // 드라이버 핸들을 먼저 정리하고 내려간다
        Application.Current.Exit();
    }

    /// <summary>그래프 표면 하나의 구성 요소 — 센터 타일·좌 대형·하단 긴 그래프 공용(A60 3차).
    /// 채널 정의는 SensorChannels 공용(A18), 축 상태는 채널별 공유(ChannelScale).</summary>
    private sealed class SensorGraph
    {
        public required Border Root;
        public required TextBlock TitleText; // 초단축 제목 — 하단 표면만 A62 배수 적용 대상
        public required TextBlock ValueText;
        public FontIcon? Pin;          // 선택 핀 배지 — 센터 타일만(A18 → A60 3차 이동)
        public required Grid GraphHost;
        public required Polyline Line;
        public required Polygon Area;
        public required TextBlock YAxisText; // A74 좌상단: y 최대값 + 단위
        public required TextBlock XAxisText; // A74 우하단: x 시간 범위
        public required SensorChannel Channel;
        public required ChannelScale Scale;  // 채널별 공유 축 상태(A74 단조 증가)
        public required double WindowMaxMs;  // 표면별 시간 창 상한(좌 10초/센터 30초/하단 5분)
        public required bool InBar;          // 하단 바 소속 — A62 배수·축 라벨 임계의 적용 대상
    }

    /// <summary>채널별 축 상태(A74) — 세 표면이 공유해 "세션 내 단조 증가"가 채널 단위로 성립하고,
    /// 선택 그래프를 껐다 켜도(표면 재생성) 눈금·흐림 상태가 이어진다.</summary>
    private sealed class ChannelScale
    {
        public float AxisMax;          // 축 상한(A74): 채널 규칙대로 시작해 관측 초과 시에만 커진다(단조 증가)
        public bool HasEverHadValue;   // 한 번도 값이 없던 채널은 흐리게 + 축 라벨도 숨김
    }

    // ---------- 전체화면 전환 (v0.42.0 → A60 3차: 대시보드 폐기, 3구획 유지) ----------

    /// <summary>프레젠터 변화 감지 — 어떤 경로(F11/⛶/Esc)로 바뀌어도 뷰 모드를 맞춘다.</summary>
    private void HookPresenterChanged()
    {
        if (_appWindow is not null) return;
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;

        _appWindow = AppWindow.GetFromWindowId(environment.AppWindowId);
        _appWindow.Changed += OnAppWindowChanged;
        UpdateViewMode();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange) return;
        DispatcherQueue?.TryEnqueue(UpdateViewMode);
    }

    /// <summary>
    /// 전체화면 왕복 뒤처리. A60 3차: 화면은 전체화면에서도 같은 3구획이라 뷰 교체가 없다 —
    /// v0.42.0 섹션 카드 대시보드는 폐기(우측 텍스트가 상시 보여 중복).
    /// 하단 긴 그래프(v0.64.2 메커니즘 승계): 평소엔 하단 바 안에 살지만 전체화면은 셸이 하단 바를
    /// 통째로 숨기므로 그동안만 뷰의 SensorStrip으로 옮겨 계속 보이게 한다.
    /// </summary>
    private void UpdateViewMode()
    {
        _fullScreen = _appWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        PlaceSensorGrid(inBar: !_fullScreen);
        UpdateStripVisibility();
        if (!_fullScreen) ApplyAlwaysOnTop(); // 전체화면 복귀 시 새 OverlappedPresenter에 토글 상태 재적용 (A39)
        // A61: 전체화면에서 나오면 핀이 여전히 켜져 있는 한 다시 접힌다(파생 상태 재계산).
        ApplyCollapse();
    }

    private bool _fullScreen;

    /// <summary>
    /// SensorGrid(선택 긴 그래프 ≤2)를 하단 바(BarGrid 가운데 칸)와 SensorStrip 사이에서 옮긴다.
    /// reparent 규칙(§3.4): 옛 부모의 Children에서 먼저 제거하고, 멤버십 판정은 Children.Contains —
    /// FrameworkElement.Parent는 가드로 쓰지 않는다(라이브 트리 부착 전엔 null — v0.113.2 교훈).
    /// x:Name 필드(SensorGrid)는 부모에서 떼어도 살아 있다(§3.4) — 코드 참조는 그대로 유효하다.
    /// </summary>
    private void PlaceSensorGrid(bool inBar)
    {
        Panel target = inBar ? BarGrid : StripPanel;
        if (target.Children.Contains(SensorGrid)) return;
        Panel other = inBar ? StripPanel : BarGrid;
        other.Children.Remove(SensorGrid); // 없으면 무동작 — 첫 호출(초기 배치는 XAML)에도 안전
        target.Children.Add(SensorGrid); // Grid.Column=2는 요소에 붙어 있어 바로 복귀해도 유효
    }

    /// <summary>SensorStrip은 내용(비관리자 안내 또는 전체화면 동안의 긴 그래프)이 있을 때만 보인다.</summary>
    private void UpdateStripVisibility()
        => SensorStrip.Visibility = _fullScreen || AdminRow.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

    private void ToggleFullScreen()
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;

        var appWindow = AppWindow.GetFromWindowId(environment.AppWindowId);
        var entering = appWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
        // A61: 접힌 채로 전체화면에 들어가지 않는다 — 먼저 펼쳐서 창을 원래 크기로 돌려놓고
        // 전환한다(전체화면에서 빠져나올 때 복원되는 크기가 접힌 높이가 되지 않게).
        // 나올 때는 UpdateViewMode가 파생 상태를 다시 계산해 (핀이 켜져 있으면) 다시 접는다.
        if (entering) SendCollapse(false);
        appWindow.SetPresenter(entering
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);
    }

    private void OnFullScreenButtonClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    // ---------- 리프레시 주기 선택 + 맥박(EKG) 그래프 (A29) ----------

    /// <summary>
    /// 맥박 그래프 시간 창 = 리프레시 주기 × 2 (A51). 어느 주기에서든 스파이크 1~2개만
    /// 보인다 — 목적이 "설정한 레이트대로 갱신 중" 표시뿐이라 그걸로 충분(5초 고정 창에
    /// 30~40틱이 몰리던 v0.84.0 동작을 대체). 주기 기준 계산이므로 A73의 50~5000ms
    /// (창 100ms~10초) 전 구간에서 그대로 성립한다 — 클램프를 넣지 않는 이유가 이것이다.
    /// </summary>
    private static TimeSpan PulseWindow
        => TimeSpan.FromMilliseconds(HardwareModule.RefreshMs * 2);

    /// <summary>창 안의 스냅샷 도착 시각들 — UI 스레드에서만 접근.</summary>
    private readonly List<DateTime> _pulseTicks = [];

    /// <summary>
    /// 주기 선택 플라이아웃(A73: 50/200/500/1000/2000/5000ms) 구성 + 현재 값 표기(A29).
    /// 항목 텍스트는 숫자 그대로 두고(숫자가 정보다), 최단값 50ms에만 부하 경고를 툴팁으로 붙인다 —
    /// 실측 부하는 기기마다 달라 상한을 강제하지 않는다는 것이 A73의 결정.
    /// </summary>
    private void BuildIntervalFlyout()
    {
        var flyout = new MenuFlyout();
        foreach (var ms in HardwareModule.RefreshChoices)
        {
            var choice = ms; // 클로저 캡처 고정
            var item = new MenuFlyoutItem { Text = $"{choice} ms" };
            if (choice == HardwareModule.RefreshChoices[0]) // 목록 최단값 = 50ms
                ToolTipService.SetToolTip(item, "Very frequent polling - higher CPU load");
            item.Click += (_, _) =>
            {
                HardwareModule.SetRefreshMs(choice); // 폴러 즉시 반영 + 설정 저장
                IntervalText.Text = $"{choice} ms";
                RerenderPulse();       // 맥박 창 길이(주기 × 2, A51)도 즉시 반영
                RerenderSparklines();  // 그래프 창 길이·x축 표기(A74)도 즉시 반영
            };
            flyout.Items.Add(item);
        }
        IntervalButton.Flyout = flyout;
        IntervalText.Text = $"{HardwareModule.RefreshMs} ms"; // 설정 복원값 표기
    }

    /// <summary>
    /// 주기 변경 직후(A74): 그래프 창 길이가 주기에 묶여 있으므로(WindowFor) 다음 스냅샷을
    /// 기다리지 않고 다시 그린다 — 5000ms를 고르면 최대 5초 동안 옛 x축 표기가 남기 때문.
    /// A51의 RerenderPulse와 같은 계열. A60 3차: 세 표면(타일·좌 대형·하단 긴) 전부를 돈다 —
    /// 선택 변경(RebuildSelectionGraphs)·크기 변경(ApplyBarScale)의 즉시 반영도 이 경로를 쓴다.
    /// </summary>
    private void RerenderSparklines()
    {
        var history = SensorService.History();
        foreach (var graph in _tiles) RenderSparkline(graph, history);
        foreach (var graph in _bigGraphs) RenderSparkline(graph, history);
        foreach (var graph in _longGraphs) RenderSparkline(graph, history);
    }

    /// <summary>
    /// A88: 렌더 루프(프레임마다 호출). 데이터(도착 기록)와 그리기를 분리한 결과 —
    /// <see cref="RenderPulse"/>의 x 좌표가 "현재 시각" 기준이라, 같은 <c>_pulseTicks</c>를
    /// 매 프레임 다시 그리기만 해도 스파이크가 우→좌로 흘러간다(수술실 심전도 모니터).
    /// 안 보이면(A40의 폭 축약으로 맥박이 내려간 상태) 즉시 빠져나가 CPU를 쓰지 않는다.
    /// 창이 최소화·숨김이면 그 스레드에 그릴 창이 없어 프레임이 멈추므로 호출도 멎는다 —
    /// 단 같은 UI 스레드에 보이는 창이 따로 있으면 프레임은 계속 오고, 그때 이 핸들러가
    /// 안 보이는 창 몫까지 도는 것은 점 10개 미만이라 비용이 무시할 수준이라 그냥 둔다.
    /// ※ 기록 정리(<c>RemoveAll</c>)는 여기서 하지 않는다 — RecordPulse/RerenderPulse의 몫이고,
    /// 창 밖 기록은 RenderPulse의 <c>tick &lt; start</c> 검사가 이미 건너뛴다.
    /// 두 번째 인자는 반드시 object여야 한다(EventHandler&lt;object&gt;) — RenderingEventArgs로 받으면 안 된다.
    /// </summary>
    private void OnPulseFrame(object? sender, object? e)
    {
        if (PulseHost.Visibility != Visibility.Visible) return;
        RenderPulse(DateTime.UtcNow);
    }

    /// <summary>스냅샷 도착 시각을 기록하고 창 밖 기록을 버린 뒤 그래프를 다시 그린다.</summary>
    private void RecordPulse()
    {
        var now = DateTime.UtcNow;
        _pulseTicks.Add(now);
        var cutoff = now - PulseWindow;
        _pulseTicks.RemoveAll(t => t < cutoff);
        RenderPulse(now);
    }

    /// <summary>주기 변경 직후(A51): 새 창 길이 기준으로 기록을 정리하고 즉시 다시 그린다.</summary>
    private void RerenderPulse()
    {
        var now = DateTime.UtcNow;
        var cutoff = now - PulseWindow;
        _pulseTicks.RemoveAll(t => t < cutoff);
        RenderPulse(now);
    }

    /// <summary>
    /// 병원 심박 모니터풍: 평평한 기준선 위에 도착 시각마다 QRS풍 스파이크(위로 크게 →
    /// 아래로 살짝 → 복귀). 창이 주기 × 2라(A51) 어느 주기에서든 스파이크 1~2개가
    /// 주기에 맞춰 흐른다 — 박동이 흐르는 속도가 곧 리프레시 레이트다.
    /// A88(v0.114.0): 호출자가 <see cref="OnPulseFrame"/>(디스플레이 주사율)이라 **매 프레임** 그린다 —
    /// 좌표가 인자 <paramref name="now"/> 기준이므로 계산식은 그대로 두고 호출 빈도만 올렸다.
    /// 점은 스파이크 1~2개분(10개 미만)이라 프레임당 비용은 무시할 수준.
    /// ※ 스파이크 폭(±3·±1)은 **픽셀 상수**지 ms 상수가 아니다 — 창 길이가 100ms(50ms 주기)든
    /// 10초(5000ms 주기)든 90px 안에서 같은 모양·같은 6px 폭으로 그려지므로 A73의 양 끝에서도
    /// 뭉개지지 않는다. 여기를 시간 단위로 바꾸면 짧은 창에서 스파이크가 창을 삼킨다.
    /// </summary>
    private void RenderPulse(DateTime now)
    {
        var w = PulseHost.ActualWidth;
        var h = PulseHost.ActualHeight;
        if (w <= 2 || h <= 2) return; // 레이아웃 전 — 다음 스냅샷에서 그려진다

        var baseline = h * 0.68;
        var window = PulseWindow; // 주기 × 2 (A51) — 한 렌더 안에서는 같은 값 사용
        var start = now - window;
        var points = new Microsoft.UI.Xaml.Media.PointCollection
        {
            new Windows.Foundation.Point(0, baseline),
        };
        foreach (var tick in _pulseTicks)
        {
            if (tick < start) continue; // 주기 축소 직후 창 밖에 남은 기록은 건너뛴다
            var x = (tick - start).TotalSeconds / window.TotalSeconds * w;
            points.Add(new Windows.Foundation.Point(Math.Max(0, x - 3), baseline));
            points.Add(new Windows.Foundation.Point(x - 1, h * 0.12));                          // R파(위로 크게)
            points.Add(new Windows.Foundation.Point(x + 1, Math.Min(h - 1, baseline + h * 0.22))); // S파(아래로 살짝)
            points.Add(new Windows.Foundation.Point(Math.Min(w, x + 3), baseline));
        }
        points.Add(new Windows.Foundation.Point(w, baseline));
        PulseLine.Points = points;
    }

    // ---------- Always on top (A39 — 사용자 확정: 인포 모듈 전용) + 접힘 (A61) ----------

    private void OnTopToggleChanged(object sender, RoutedEventArgs e)
    {
        ApplyAlwaysOnTop();
        ApplyCollapse(); // A61: 핀이 접힘의 단일 소스 — 별도 토글을 두지 않는다
    }

    /// <summary>
    /// 토글 상태를 창 프레젠터에 반영한다. 전체화면(FullScreenPresenter) 동안은 대상이
    /// 없으므로 건너뛰고, 창 모드로 돌아올 때 UpdateViewMode가 다시 불러 복원한다
    /// (SetPresenter가 OverlappedPresenter를 새로 만들어 IsAlwaysOnTop이 초기화되기 때문).
    /// </summary>
    private void ApplyAlwaysOnTop()
    {
        if (_appWindow is null) HookPresenterChanged(); // Loaded 전 클릭 대비
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = TopToggle.IsChecked == true;
    }

    /// <summary>셸에 보내는 접기/펼치기 요청(A61 — IWindowCollapseSource). 실행은 셸이 한다.</summary>
    public event Action<bool>? CollapseRequested;

    /// <summary>셸에 마지막으로 보낸 값 — 같은 값을 반복해 보내지 않는다(셸 쪽도 멱등).</summary>
    private bool _collapseSent;

    /// <summary>
    /// 접힘은 **"핀 ON && 전체화면 아님"으로 계산되는 파생 상태**다(A61 확정) —
    /// 별도 플래그를 들고 다니지 않으므로 전체화면 왕복·핀 토글 어느 순서로도 어긋나지 않는다.
    /// </summary>
    private bool ShouldCollapse => TopToggle.IsChecked == true && !_fullScreen;

    /// <summary>파생 상태를 다시 계산해 바뀌었으면 셸에 알린다(핀 토글·프레젠터 변화에서 호출).</summary>
    private void ApplyCollapse() => SendCollapse(ShouldCollapse);

    private void SendCollapse(bool collapse)
    {
        if (collapse == _collapseSent) return;
        _collapseSent = collapse;
        CollapseRequested?.Invoke(collapse);
    }

    private void OnFullScreenInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullScreen();
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_appWindow?.Presenter.Kind != AppWindowPresenterKind.FullScreen) return;
        args.Handled = true;
        _appWindow.SetPresenter(AppWindowPresenterKind.Default);
    }

    // ---------- 조작 ----------

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyAllToClipboard();

    /// <summary>모든 섹션 + 현재 센서 값을 텍스트로 클립보드에 복사 (사양 공유용). 버튼과 C 키(A34) 공용.
    /// 센서 나열 순서 = 화면 표시 순서(_state.Order — A60 3차의 드래그 순서를 그대로 따른다).</summary>
    private void CopyAllToClipboard()
    {
        var sb = new StringBuilder();
        foreach (var section in _sections)
        {
            sb.AppendLine($"[{section.Title}]");
            foreach (var item in section.Items)
                sb.AppendLine($"{item.Label}: {item.Value}");
            sb.AppendLine();
        }

        if (_lastFrame.Timestamp != DateTime.MinValue)
        {
            sb.AppendLine("[Sensors]");
            foreach (var id in _state.Order)
            {
                if (SensorChannels.ById(id) is not { } channel) continue;
                sb.AppendLine($"{channel.Title}: {(channel.Select(_lastFrame) is { } v ? channel.FormatFull(v) : "-")}");
            }
        }

        var package = new DataPackage();
        package.SetText(sb.ToString().TrimEnd());
        Clipboard.SetContent(package);
    }

    // ---------- 하단 바 버튼 핫키 (A34) ----------

    /// <summary>바 크기 순환 키 — 툴팁 표기(ApplyBarScale)와 액셀러레이터가 이 한 값을 함께 쓴다.</summary>
    private const VirtualKey BarScaleKey = VirtualKey.B;

    /// <summary>
    /// A34: 하단 바 버튼에 단독 문자 키를 걸고 툴팁 "(키)" 표기까지 같은 호출에서 만든다.
    /// I(주기)는 누르면 선택 플라이아웃이 열리고, P(핀)는 누를 때마다 토글된다.
    /// A(1:1)·F(Fit)가 없는 모듈이라 A는 비워 두고 핀은 P(Pin)로 — 다른 모듈의 A와 뜻이 겹치지 않게 했다.
    /// 바 구성은 A60 3차(v0.138.0)에서 카드 10개 → 긴 그래프 2개로 개편됐다 — 키 배선은 그대로다.
    /// </summary>
    private void SetupHotkeys()
    {
        HotkeySupport.Bind(this, CopyButton, VirtualKey.C,
            "Copy all hardware info and sensor values", CopyAllToClipboard);
        HotkeySupport.Bind(this, IntervalButton, VirtualKey.I,
            "Sensor refresh interval", () => IntervalButton.Flyout?.ShowAt(IntervalButton));
        HotkeySupport.Bind(this, TopToggle, VirtualKey.P,
            "Always on top (collapses to the bar)", () => TopToggle.IsChecked = TopToggle.IsChecked != true);
        HotkeySupport.Register(this, BarScaleButton, BarScaleKey, CycleBarScaleLocal);
    }
}
