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
/// 하드웨어 스펙 화면. WMI 수집·센서 수집은 HardwareModule.Poller(프로세스 공유 폴링 워커,
/// A42)가 전담하고, 뷰는 구독해서 스냅샷을 UI 스레드로 디스패치 받아 그리기만 한다.
/// 일반 모드는 라벨-값 리스트, 전체화면(F11/⛶)은 섹션 카드 대시보드로 보여준다(v0.42.0).
/// 센서 그래프 카드(A17)는 하단 바 한 줄 안에 산다(v0.64.2 사용자 지시) — 전체화면에서만
/// 셸 하단 바가 숨는 동안 SensorStrip으로 옮겨 표시. Copy·⛶·센서를 담은
/// 하단 바는 셸이 TakeBottomBar()로 떼어간다. 수동 Refresh 버튼은 A75에서 제거
/// (주기 폴링이 이미 최신 상태를 유지하므로 불필요 — 사용자 확정).
/// A61(v0.111.0): 핀(A39)을 켜면 셸에 접기를 요청해 하단 바만 남는 상시 표시 바가 된다
/// (IWindowCollapseSource). A62: 그 바의 글씨·선 굵기·카드 폭을 S/M/L로 키운다.
/// </summary>
public sealed partial class HardwareView : UserControl, IBottomBarProvider, IWindowCollapseSource
{
    private IReadOnlyList<HardwareSection> _sections = [];
    private AppWindow? _appWindow;
    private bool _dashboardRendered; // 같은 데이터로 대시보드를 다시 만들지 않기 위한 플래그
    private IDisposable? _subscription;  // 공유 폴러 구독(로드 중에만 유지 — 없으면 폴러 휴면)
    private bool _firstLoadPending;      // 첫 로드 Busy 링 표시 중 — 첫 스냅샷 도착 시 끈다(A75)
    private string _dataSignature = ""; // 값이 안 바뀌면 UI 재구성 생략
    private readonly List<SensorCard> _cards = []; // 센서 그래프 카드 10개 (A17)
    private SensorFrame _lastFrame = SensorFrame.Empty; // Copy all에 센서 값 포함용

    public HardwareView(OpenContext context)
    {
        _ = context; // 파일 컨텍스트 없음
        InitializeComponent();
        BuildSensorCards();
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
            TraySensors.Changed -= UpdateTrayPins; // Loaded 중복 발화 대비 — 이중 구독 방지
            TraySensors.Changed += UpdateTrayPins; // 다른 창에서 토글해도 이 창 카드에 반영
            HardwareModule.BarScaleChanged -= ApplyBarScale; // 같은 이유의 이중 구독 방지 (A62)
            HardwareModule.BarScaleChanged += ApplyBarScale; // 다른 창에서 바꿔도 이 창 바에 반영
            // A88: 맥박 렌더 루프를 붙인다(프레임마다 다시 그려 스파이크가 흐르게).
            // static 이벤트라 같은 이중 구독 방지 패턴을 그대로 쓴다.
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPulseFrame;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnPulseFrame;
            UpdateTrayPins();
        };
        Unloaded += (_, _) =>
        {
            _subscription?.Dispose(); // 마지막 뷰가 내려가면 폴러는 휴면(트레이 구독이 없다면)
            _subscription = null;
            TraySensors.Changed -= UpdateTrayPins;
            HardwareModule.BarScaleChanged -= ApplyBarScale; // A62
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

    /// <summary>하단 바(Refresh·Copy·⛶)를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(v0.42.0).</summary>
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
    /// UI 스레드: 첫 로드 Busy 링을 끄고, 센서 카드는 매 프레임 갱신,
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
        // 폴러는 스펙 섹션을 2초 캐시로 재사용한다 — 같은 참조면 서명 계산조차 불필요
        if (ReferenceEquals(_sections, snapshot.Sections)) return;
        var signature = Signature(snapshot.Sections);
        _sections = snapshot.Sections; // 값이 같아도 참조는 갱신 — 다음 폴링부터 위 빠른 경로를 타게
        if (signature == _dataSignature) return;
        _dataSignature = signature;
        ApplySections();
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

    /// <summary>새 수집 결과를 현재 보이는 뷰(리스트/대시보드)에 반영한다.</summary>
    private void ApplySections()
    {
        _dashboardRendered = false;
        Render();
        if (DashboardScroller.Visibility == Visibility.Visible) RenderDashboard();
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

    // ---------- 일반 모드: 라벨-값 리스트 ----------

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

            foreach (var item in section.Items)
                Root.Children.Add(MakeItemRow(item, labelWidth: 170));
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

    // ---------- 전체화면 모드: 카드 대시보드 (v0.42.0) ----------

    /// <summary>섹션별 액센트 색 — 카드 헤더 스트라이프에 쓴다(양쪽 테마에서 보이는 채도).</summary>
    private static Windows.UI.Color AccentFor(string title) => title switch
    {
        "CPU" => Windows.UI.Color.FromArgb(255, 0xE9, 0x60, 0x3D),
        "GPU" => Windows.UI.Color.FromArgb(255, 0x7A, 0x5A, 0xF8),
        "RAM" => Windows.UI.Color.FromArgb(255, 0x2E, 0x9E, 0x6B),
        "Motherboard" => Windows.UI.Color.FromArgb(255, 0xC5, 0x8A, 0x00),
        "Storage" => Windows.UI.Color.FromArgb(255, 0x3A, 0x7B, 0xD5),
        "Network" => Windows.UI.Color.FromArgb(255, 0x1F, 0xA8, 0xA0), // A20 — 오디오 모듈 청록 계열
        _ => Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x8E), // System 등
    };

    /// <summary>
    /// 전체화면 대시보드: 머신 이름 헤더 + 섹션 카드 3열 그리드.
    /// 카드 = 액센트 스트라이프 + 제목, 첫 항목을 히어로(큰 글씨)로, 나머지는 라벨-값 행.
    /// </summary>
    private void RenderDashboard()
    {
        if (_dashboardRendered) return;
        _dashboardRendered = true;

        DashboardRoot.Children.Clear();

        // 헤더: 컴퓨터 이름 크게 + OS 한 줄
        DashboardRoot.Children.Add(new TextBlock
        {
            Text = Environment.MachineName,
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(4, 0, 0, 0),
        });
        DashboardRoot.Children.Add(new TextBlock
        {
            Text = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            FontSize = 13,
            Opacity = 0.55,
            Margin = new Thickness(4, -10, 0, 4),
        });

        // 카드 3열 그리드 (섹션 6개 = 2행)
        const int columns = 3;
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = (_sections.Count + columns - 1) / columns;
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < _sections.Count; i++)
        {
            var card = MakeSectionCard(_sections[i]);
            Grid.SetColumn(card, i % columns);
            Grid.SetRow(card, i / columns);
            grid.Children.Add(card);
        }
        DashboardRoot.Children.Add(grid);
    }

    private static Border MakeSectionCard(HardwareSection section)
    {
        var accent = new SolidColorBrush(AccentFor(section.Title));
        var panel = new StackPanel { Spacing = 4 };

        // 헤더: 액센트 스트라이프 + 섹션 제목
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new Border
        {
            Width = 4,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = section.Title.ToUpperInvariant(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 100,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(header);

        // 히어로: 첫 항목을 큰 글씨로 (라벨은 작은 캡션)
        if (section.Items.Count > 0)
        {
            var hero = section.Items[0];
            panel.Children.Add(new TextBlock
            {
                Text = hero.Value,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(0, 6, 0, 0),
            });
            panel.Children.Add(new TextBlock
            {
                Text = hero.Label,
                FontSize = 11,
                Opacity = 0.5,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        // 나머지 항목: 라벨-값 행
        foreach (var item in section.Items.Skip(1))
            panel.Children.Add(MakeItemRow(item, labelWidth: 130));

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = panel,
        };
    }

    // ---------- 하단 바 표시 크기 S/M/L (A62, v0.111.0) ----------

    // M(1.0) 기준 치수. 실제 값은 ApplyBarScale이 HardwareModule.BarScale을 곱해 정한다.
    private const double BaseCardHeight = 36;      // v0.64.2 컴팩트 카드 높이
    // 카드 높이 상한. A97(v0.116.0)에서 40 → 36 — 하단 바 버튼이 36이 됐고 카드가 그보다
    // 높으면 한 줄의 위아래 선이 어긋나 보인다. 44px 바(A40 불변) 안에도 그대로 들어간다.
    // ※ 결과: 기준 높이(36)와 상한이 같아져 **L 단계에서도 카드 높이는 36**이다 —
    //   A62의 L은 이제 글씨·선 굵기만 키운다(높이로 커지는 여지는 바 두께 44가 원래부터 막고 있었다).
    private const double MaxCardHeight = 36;
    private const double BaseTitleFontSize = 11;   // 카드 초단축 제목
    private const double BaseValueFontSize = 13;   // 카드 값
    private const double BaseSmallFontSize = 10;   // 트레이 핀 아이콘(A18) + 축 라벨(A74)
    private const double BaseStrokeThickness = 1.5; // 스파크라인·맥박 선 굵기
    private const double BaseBarIconFontSize = 18;  // 하단 바 아이콘(A27 규격 버튼 안)

    /// <summary>
    /// 크기 버튼 클릭 = S → M → L → S 순환(A62). 설정은 프로세스 공유라 열려 있는 다른
    /// 정보 창도 HardwareModule.BarScaleChanged로 같은 단계를 따라온다.
    /// </summary>
    private void OnBarScaleClick(object sender, RoutedEventArgs e) => HardwareModule.CycleBarScale();

    /// <summary>
    /// 현재 단계를 하단 바 요소에 반영한다(A62). 바 두께 44는 불변(A40)이므로 **바 안 요소**의
    /// 글씨 크기·선 굵기·카드 높이(최대 36 — A97)·카드 최소 폭만 바뀐다.
    /// 폭 임계값(카드 수 축소·맥박 숨김·축 라벨 숨김)도 같이 스케일해야 글씨가 커졌을 때
    /// 더 이른 폭에서 축약된다 — 그래서 마지막에 배치·밀도·스파크라인을 다시 계산한다.
    /// 버튼 아이콘 크기도 단계를 따라 커져 툴팁 없이도 지금 단계가 보인다.
    /// 전역 UI 배율(A41 UiScale)은 건드리지 않는다 — 별개의 배수(A62 확정).
    /// </summary>
    private void ApplyBarScale()
    {
        var scale = HardwareModule.BarScale;
        var height = Math.Min(MaxCardHeight, BaseCardHeight * scale);
        foreach (var card in _cards)
        {
            card.Root.Height = height;
            card.TitleText.FontSize = BaseTitleFontSize * scale;
            card.ValueText.FontSize = BaseValueFontSize * scale;
            card.Pin.FontSize = BaseSmallFontSize * scale;
            card.YAxisText.FontSize = BaseSmallFontSize * scale;
            card.XAxisText.FontSize = BaseSmallFontSize * scale;
            card.Line.StrokeThickness = BaseStrokeThickness * scale;
        }
        PulseHost.Height = height; // 맥박 그래프도 카드와 같은 높이 유지 (v0.64.2 규격)
        PulseLine.StrokeThickness = BaseStrokeThickness * scale;
        BarScaleIcon.FontSize = BaseBarIconFontSize * scale;
        // A34: 표기는 키 상수에서 조립한다(단계 표시가 바뀌어도 키 표기는 어긋나지 않는다).
        ToolTipService.SetToolTip(BarScaleButton, HotkeySupport.Tip(
            $"Bottom bar size: {HardwareModule.BarScaleSteps[HardwareModule.BarScaleIndex].Label}",
            BarScaleKey));

        _sensorColumns = 0; // 카드 최소 폭이 바뀌었다 — 같은 폭이어도 다시 계산하게 한다
        LayoutSensorCards(SensorGrid.ActualWidth);
        UpdateBarDensity(BarGrid.ActualWidth);
        RerenderSparklines(); // 축 라벨 표시 임계값(A74)·선 굵기를 다음 스냅샷 전에 반영
    }

    // ---------- 센서 그래프 스트립 (A17) ----------

    /// <summary>그래프가 보여주는 최대 시간 범위(ms) — A17의 "최근 60초".</summary>
    private const double GraphWindowMaxMs = 60_000;

    /// <summary>
    /// 그래프 시간 창(A17: 최근 60초)을 카드 폭에 맞춰 그린다. 단 이력 링
    /// (<see cref="SensorService.HistoryCapacity"/>개)이 담는 시간이 그보다 짧으면 그만큼만 —
    /// A73의 최단 주기 50ms에서는 600표본 = 30초뿐이라 60초 창을 쓰면 좌측 절반이 빈 채로 남는다.
    /// A74의 x축 표기도 이 값을 그대로 쓴다("표본 개수 × 주기"가 곧 창 길이).
    /// </summary>
    private static TimeSpan GraphWindow => TimeSpan.FromMilliseconds(
        Math.Min(GraphWindowMaxMs, (double)SensorService.HistoryCapacity * HardwareModule.RefreshMs));

    /// <summary>
    /// 축 라벨(A74)을 표시하는 최소 그래프 폭(M 기준). 이보다 좁으면 라벨 두 개가 셀을 다 덮어
    /// 그래프가 안 읽힌다 — A40의 "좁으면 축약" 관례와 같은 방식으로 숨긴다.
    /// A62: 글씨가 커지면 같은 폭에서 더 많이 가리므로 임계값에도 배수를 곱한다(AxisMinWidthNow).
    /// </summary>
    private const double AxisMinWidth = 90;

    /// <summary>현재 단계(A62)를 반영한 축 라벨 표시 임계 폭.</summary>
    private static double AxisMinWidthNow => AxisMinWidth * HardwareModule.BarScale;

    /// <summary>
    /// 카드 10개 배치(순서는 사용자 확정). 채널 정의(제목·색·선택자·포맷·스케일)는
    /// SensorChannels 단일 소스(A18에서 트레이와 공용화) — 색은 대시보드 섹션 액센트 계열:
    /// CPU 주황 / GPU 보라 / RAM 초록 / 팬 황금 / SSD 파랑.
    /// 스케일: 온도·부하는 0~100 고정, 전력·클럭·팬은 자동(하한 있는 관찰 최댓값).
    /// 카드는 하단 바 한 줄에 들어가는 36px 컴팩트형(v0.64.2 사용자 지시) — 그래프가 카드
    /// 전체를 채우고 제목·값이 그 위에 얹힌다. 배치는 항상 1줄(A40: 하단 바 두께 고정 44) —
    /// 폭이 모자라면 뒤 순서 카드부터 생략한다(LayoutSensorCards 참고).
    /// A74: 각 카드 모서리에 y 최대값·x 시간 범위 라벨이 얹힌다(폭이 좁으면 숨김).
    /// </summary>
    private void BuildSensorCards()
    {
        foreach (var channel in SensorChannels.All)
            AddCard(channel);
        SensorGrid.SizeChanged += (_, e) => LayoutSensorCards(e.NewSize.Width);
        // A40: 하단 바 전체 폭 기준으로 장식 요소(맥박 그래프)를 먼저 내린다 — 카드 폭 확보
        BarGrid.SizeChanged += (_, e) => UpdateBarDensity(e.NewSize.Width);
        LayoutSensorCards(0); // 실측 폭을 알기 전엔 10칸 1줄로 시작
    }

    /// <summary>
    /// 카드가 이보다 좁아지면 안 된다(좁으면 표시 카드 수를 줄인다). 하한 근거: 좌우 패딩 12 +
    /// 최장 값 "4500 MHz"(13px SemiBold ≈ 62px) — 제목은 스타 칸이라 먼저 말줄임되므로
    /// 값만 안 잘리면 된다(v0.64.3 사용자 피드백: 104는 너무 일찍 줄바꿈됨).
    /// A62: 글씨가 커지면 값도 그만큼 넓어지므로 이 하한에 배수를 곱한다(MinCardWidthNow).
    /// </summary>
    private const double MinCardWidth = 76;

    /// <summary>현재 단계(A62)를 반영한 카드 최소 폭.</summary>
    private static double MinCardWidthNow => MinCardWidth * HardwareModule.BarScale;

    /// <summary>
    /// 하단 바(BarGrid)에서 센서 카드 칸(star)을 뺀 **고정 요소들의 폭 합**. A62 배수와 무관하다 —
    /// 버튼 규격은 A27(→A97 개정)이 못 박아 두었고 배율은 카드 쪽에만 곱하기 때문.
    /// A97(v0.116.0)에서 1칸 버튼 40→36 · 간격 10→6이 되어 **재산정**했다.
    /// 이전 값 458은 v0.94.0(A40) 산정치 1240에서 역산한 근사치라 실제 합(424)보다 34 컸다 —
    /// 이번에는 XAML 요소를 하나씩 세어 정확한 합으로 바꾼다:
    ///   Copy 36 + Busy(ProgressRing) 20 + 맥박 90 + 주기(2칸) 84 + 크기 36 + 핀 36 + ⛶ 36 = 338
    ///   + ColumnSpacing 6 × 7칸 사이 = 42  →  **380**
    /// ⚠️ BarGrid.ActualWidth 기준이므로 셸의 ModuleBarHost Margin(48/12)은 여기 포함되지 않는다.
    /// HardwareView.xaml의 BarGrid 구성이 바뀌면 이 합도 함께 고칠 것.
    /// </summary>
    private const double BarFixedWidth = 380;

    /// <summary>카드 10개가 최소 폭으로 다 들어가는 데 필요한 폭 — 단계(A62)에 따라 함께 커진다.</summary>
    private double CardsMinTotalWidth
        => MinCardWidthNow * _cards.Count + CardSpacing * Math.Max(0, _cards.Count - 1);

    /// <summary>
    /// A40: 하단 바 폭이 좁으면 장식성 요소부터 내린다 — 맥박 그래프(A29)는 카드 10개가
    /// 전부 들어갈 폭이 안 되면 숨긴다(카드 = 정보, 맥박 = 장식이므로 카드가 우선).
    /// 임계값 = 카드 전체 최소 폭 + 고정 요소·간격(M 단계에서 832 + 380 = **1212** —
    /// A97/v0.116.0의 버튼 36·간격 6 재산정 반영. 이전 v0.111.0 값은 1290이었다).
    /// A62에서 글씨가 커지면 임계값도 같이 올라가 맥박이 더 이른 폭에서 사라진다 — 카드 우선 원칙은 그대로다.
    /// PulseHost 표시 여부는 BarGrid(부모가 정하는 폭) 기준이라 피드백 루프가 없다.
    /// </summary>
    private void UpdateBarDensity(double width)
        => PulseHost.Visibility = width >= CardsMinTotalWidth + BarFixedWidth
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>SensorGrid의 ColumnSpacing/RowSpacing과 같은 값 — 폭 계산에 쓴다.</summary>
    private const double CardSpacing = 8;

    /// <summary>현재 표시 중인 카드 수(= 칸 수). 0 = 아직 배치 전.</summary>
    private int _sensorColumns;

    /// <summary>
    /// 폭에 맞는 카드 수를 골라 항상 1줄로 배치한다. A40(하단 바 두께 고정)으로
    /// v0.64.2의 5칸 2줄·4칸 3줄 접힘은 폐기 — 바가 세로로 자라면 안 되므로, 폭이
    /// 모자라면 뒤 순서 카드부터 숨긴다(SensorChannels.All 순서 = 사용자 확정 = 우선순위).
    /// 숨긴 카드는 폭이 돌아오면(전체화면 SensorStrip 포함) 다시 나타난다.
    /// 카드 수가 그대로면 아무것도 안 한다.
    /// </summary>
    private void LayoutSensorCards(double width)
    {
        var visible = _cards.Count; // 실측 폭을 알기 전엔 전부(10칸 1줄)
        if (width > 0)
            visible = Math.Clamp(
                (int)((width + CardSpacing) / (MinCardWidthNow + CardSpacing)), 1, _cards.Count);
        if (visible == _sensorColumns) return;
        _sensorColumns = visible;

        SensorGrid.ColumnDefinitions.Clear();
        SensorGrid.RowDefinitions.Clear(); // 항상 1줄 — 행 정의 없이 암시적 0행만 쓴다
        for (var c = 0; c < visible; c++)
            SensorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < _cards.Count; i++)
        {
            _cards[i].Root.Visibility = i < visible ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(_cards[i].Root, i < visible ? i : 0);
            Grid.SetRow(_cards[i].Root, 0);
            // ⚠️ FrameworkElement.Parent는 라이브 트리 부착 전(생성자 안)에는 Children.Add
            // 뒤에도 null이라 중복 추가 가드로 못 쓴다 — v0.111.0(A62)의 ApplyBarScale이
            // 생성자에서 이 메서드를 두 번째로 태우면서 같은 카드가 두 번 Add되어
            // COMException 0x800F1000("설치된 구성 요소가 감지되지 않았습니다")으로 죽었다
            // (v0.113.2에서 수정). 컬렉션 멤버십을 직접 확인한다 — 카드 10개라 비용은 무시 가능.
            if (!SensorGrid.Children.Contains(_cards[i].Root))
                SensorGrid.Children.Add(_cards[i].Root);
        }
    }

    private void AddCard(SensorChannel channel)
    {
        var accent = channel.Accent;
        var stroke = new SolidColorBrush(accent);
        var fill = new SolidColorBrush(Windows.UI.Color.FromArgb(56, accent.R, accent.G, accent.B));

        // 글씨 크기·선 굵기·카드 높이는 M 단계 기준값으로 만들고, 직후 ApplyBarScale(A62)이
        // 현재 단계 배수를 곱해 덮어쓴다 — 단계가 바뀔 때마다 카드를 다시 만들지 않기 위해서다.
        var titleText = new TextBlock
        {
            Text = channel.ShortTitle, // 초단축 제목(v0.64.3) — 전체 이름은 툴팁에
            FontSize = BaseTitleFontSize,
            Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 트레이 선택 핀(A18): 이 채널이 트레이에 표시 중이면 보인다.
        var pinIcon = new FontIcon
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
        Grid.SetColumn(pinIcon, 1);
        Grid.SetColumn(valueText, 2);
        header.Children.Add(titleText);
        header.Children.Add(pinIcon);
        header.Children.Add(valueText);

        header.VerticalAlignment = VerticalAlignment.Center;

        var line = new Polyline { Stroke = stroke, StrokeThickness = BaseStrokeThickness };
        var area = new Polygon { Fill = fill };

        // 축 스케일 라벨(A74): 눈금선·축선은 그리지 않는다(정사각에 가까운 작은 셀이 지저분해진다).
        // 좌상단 = y 최대값 + 단위("100°C"), 우하단 = x 시간 범위("60s"). y 하한 0은 자명해 생략.
        // 값·표시 여부는 RenderSparkline이 매 프레임 채운다 — 여기선 빈 채로 만들어 둔다.
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

        var graphHost = new Grid(); // 카드 전체가 그래프 — 텍스트는 그 위에 겹친다(v0.64.2 컴팩트형)
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
            Height = BaseCardHeight, // 하단 바(A40 고정 44) 한 줄에 들어가는 높이 — A62 상한은 36(A97)
            Opacity = 0.45, // 값이 들어오면 1로
            Child = panel,
        };

        // 카드 클릭 = 트레이 표시 토글(A18, 사용자 확정 UX). 이미 2개면 오래된 선택이 밀려난다.
        root.Tapped += (_, _) => TraySensors.Toggle(channel.Id);
        ToolTipService.SetToolTip(root, $"{channel.Title} — click to show in tray (up to 2)");

        _cards.Add(new SensorCard
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
            // 축 상한 시작점(A74): 고정 스케일 채널(온도·%)은 100, 나머지는 채널별 하한.
            AxisMax = channel.FixedMax > 0 ? channel.FixedMax : channel.AutoFloor,
        });
    }

    /// <summary>트레이 선택이 바뀌면(이 창 클릭이든 다른 창이든) 카드 핀 표시를 맞춘다(A18).</summary>
    private void UpdateTrayPins()
    {
        foreach (var card in _cards)
            card.Pin.Visibility = TraySensors.IsSelected(card.Channel.Id)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>매 스냅샷: 승격 안내 표시 여부 + 카드 값·스파크라인 갱신.</summary>
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
        foreach (var card in _cards)
            UpdateCard(card, frame, history);
    }

    private static void UpdateCard(SensorCard card, SensorFrame frame, SensorFrame[] history)
    {
        var value = frame.Timestamp == DateTime.MinValue ? null : card.Channel.Select(frame);
        card.ValueText.Text = value is { } v ? card.Channel.FormatFull(v) : "—";
        if (value is not null) card.HasEverHadValue = true;
        card.Root.Opacity = card.HasEverHadValue ? 1.0 : 0.45;
        RenderSparkline(card, history);
    }

    /// <summary>
    /// 이력을 카드 폭에 맞춰 꺾은선 + 면으로 그린다. x는 시간 비례(주기가 바뀌어도 올바름 —
    /// A29 대비), y는 채널별 축 상한(A74: %는 0~100 고정 / 온도는 100 시작 + 초과 시 확장 /
    /// 그 외는 1·2·5 눈금 올림, 셋 다 세션 내 단조 증가) 기준.
    /// 그리고 나서 모서리 축 라벨을 갱신한다(A74).
    /// 레이아웃 전(폭 0)엔 그리지 않는다 — 다음 프레임에 그려진다.
    /// </summary>
    private static void RenderSparkline(SensorCard card, SensorFrame[] history)
    {
        var w = card.GraphHost.ActualWidth;
        var h = card.GraphHost.ActualHeight;
        if (w <= 2 || h <= 2 || history.Length == 0)
        {
            ClearSparkline(card);
            return;
        }

        var window = GraphWindow; // 한 렌더 안에서는 같은 값 — 주기 변경과 겹쳐도 x가 어긋나지 않게
        var now = history[^1].Timestamp;
        var start = now - window;

        // 축 상한 갱신(A74): 창 안의 관측값이 현재 상한을 넘을 때만 올린다. 한 번 올라간 상한은
        // 세션 중 내려오지 않는다 — 값이 튈 때마다 그래프 전체가 출렁이는 것을 막기 위해서다.
        foreach (var f in history)
        {
            if (f.Timestamp < start) continue;
            if (card.Channel.Select(f) is { } v && v > card.AxisMax)
                card.AxisMax = AxisCeiling(card.Channel, v);
        }
        var max = card.AxisMax;
        if (max <= 0) max = 1;

        var linePoints = new PointCollection();
        var areaPoints = new PointCollection();
        double firstX = -1, lastX = -1;
        // 표본 상한 ~150개: 그리기 비용 억제 (200ms 주기 60초 창 = 300표본 → 2개에 1개)
        var step = Math.Max(1, history.Length / 150);
        for (var i = 0; i < history.Length; i += step)
        {
            var f = history[i];
            if (f.Timestamp < start) continue;
            if (card.Channel.Select(f) is not { } v) continue; // null 구간은 건너뛴다(선이 이어짐)

            var x = (f.Timestamp - start).TotalSeconds / window.TotalSeconds * w;
            var y = h - Math.Clamp(v / max, 0f, 1f) * h;
            linePoints.Add(new Windows.Foundation.Point(x, y));
            areaPoints.Add(new Windows.Foundation.Point(x, y));
            if (firstX < 0) firstX = x;
            lastX = x;
        }

        if (linePoints.Count < 2)
        {
            ClearSparkline(card);
            return;
        }
        // 면은 선 아래를 바닥까지 닫는다
        areaPoints.Add(new Windows.Foundation.Point(lastX, h));
        areaPoints.Add(new Windows.Foundation.Point(firstX, h));
        card.Line.Points = linePoints;
        card.Area.Points = areaPoints;

        // 축 라벨(A74): 좌상단 y 최대값 + 단위, 우하단 x 시간 범위. 카드가 좁으면(그래프 폭
        // 90 미만) 숨긴다 — 값이 한 번도 없던 채널도 숨긴다(축만 떠 있으면 오히려 오해를 준다).
        var showAxis = w >= AxisMinWidthNow && card.HasEverHadValue;
        if (showAxis)
        {
            card.YAxisText.Text = $"{max:0}{card.Channel.AxisUnit}";
            card.XAxisText.Text = FormatSpan(window);
        }
        card.YAxisText.Visibility = showAxis ? Visibility.Visible : Visibility.Collapsed;
        card.XAxisText.Visibility = showAxis ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>그릴 게 없을 때: 선·면과 축 라벨(A74)을 함께 비운다.</summary>
    private static void ClearSparkline(SensorCard card)
    {
        card.Line.Points = null;
        card.Area.Points = null;
        card.YAxisText.Visibility = Visibility.Collapsed;
        card.XAxisText.Visibility = Visibility.Collapsed;
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

    /// <summary>x축 시간 범위 표기(A74) — 60초를 넘으면 분으로. 예: "30s"·"60s"·"3m".</summary>
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

    /// <summary>센서 카드 하나의 구성 요소·스케일 상태. 채널 정의는 SensorChannels 공용(A18).</summary>
    private sealed class SensorCard
    {
        public required Border Root;
        public required TextBlock TitleText; // 초단축 제목 — A62 배수 적용 대상
        public required TextBlock ValueText;
        public required FontIcon Pin;  // 트레이 표시 중 핀 (A18)
        public required Grid GraphHost;
        public required Polyline Line;
        public required Polygon Area;
        public required TextBlock YAxisText; // A74 좌상단: y 최대값 + 단위
        public required TextBlock XAxisText; // A74 우하단: x 시간 범위
        public required SensorChannel Channel;
        public float AxisMax;          // 축 상한(A74): 채널 규칙대로 시작해 관측 초과 시에만 커진다(단조 증가)
        public bool HasEverHadValue;   // 한 번도 값이 없던 채널은 흐리게 + 축 라벨도 숨김
    }

    // ---------- 전체화면 전환 (v0.42.0) ----------

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
    /// 전체화면이면 대시보드, 아니면 리스트를 보여준다.
    /// 센서 카드(v0.64.2): 평소엔 하단 바 안에 살지만 전체화면은 셸이 하단 바를 통째로
    /// 숨기므로 그동안만 뷰의 SensorStrip으로 옮겨 대시보드 하단에 계속 보이게 한다.
    /// </summary>
    private void UpdateViewMode()
    {
        _fullScreen = _appWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        ListScroller.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        DashboardScroller.Visibility = _fullScreen ? Visibility.Visible : Visibility.Collapsed;
        PlaceSensorGrid(inBar: !_fullScreen);
        UpdateStripVisibility();
        if (_fullScreen) RenderDashboard();
        else ApplyAlwaysOnTop(); // 전체화면 복귀 시 새 OverlappedPresenter에 토글 상태 재적용 (A39)
        // A61: 전체화면에서 나오면 핀이 여전히 켜져 있는 한 다시 접힌다(파생 상태 재계산).
        ApplyCollapse();
    }

    private bool _fullScreen;

    /// <summary>SensorGrid를 하단 바(BarGrid 가운데 칸)와 SensorStrip 사이에서 옮긴다.</summary>
    private void PlaceSensorGrid(bool inBar)
    {
        Panel target = inBar ? BarGrid : StripPanel;
        if (ReferenceEquals(SensorGrid.Parent, target)) return;
        (SensorGrid.Parent as Panel)?.Children.Remove(SensorGrid);
        target.Children.Add(SensorGrid); // Grid.Column=2는 요소에 붙어 있어 바로 복귀해도 유효
    }

    /// <summary>SensorStrip은 내용(비관리자 안내 또는 전체화면 센서 카드)이 있을 때만 보인다.</summary>
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
                ToolTipService.SetToolTip(item, "Very frequent polling — higher CPU load");
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
    /// 주기 변경 직후(A74): 그래프 창 길이가 주기에 묶여 있으므로(GraphWindow) 다음 스냅샷을
    /// 기다리지 않고 다시 그린다 — 5000ms를 고르면 최대 5초 동안 옛 x축 표기가 남기 때문.
    /// A51의 RerenderPulse와 같은 계열.
    /// </summary>
    private void RerenderSparklines()
    {
        var history = SensorService.History();
        foreach (var card in _cards)
            RenderSparkline(card, history);
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

    /// <summary>모든 섹션 + 현재 센서 값을 텍스트로 클립보드에 복사 (사양 공유용). 버튼과 C 키(A34) 공용.</summary>
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
            foreach (var card in _cards)
                sb.AppendLine($"{card.Channel.Title}: {(card.Channel.Select(_lastFrame) is { } v ? card.Channel.FormatFull(v) : "-")}");
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
    /// 이 바는 A60·A71·A72에서 개편 예정이라 구성은 그대로 두고 키만 얹었다.
    /// </summary>
    private void SetupHotkeys()
    {
        HotkeySupport.Bind(this, CopyButton, VirtualKey.C,
            "Copy all hardware info and sensor values", CopyAllToClipboard);
        HotkeySupport.Bind(this, IntervalButton, VirtualKey.I,
            "Sensor refresh interval", () => IntervalButton.Flyout?.ShowAt(IntervalButton));
        HotkeySupport.Bind(this, TopToggle, VirtualKey.P,
            "Always on top (collapses to the bar)", () => TopToggle.IsChecked = TopToggle.IsChecked != true);
        HotkeySupport.Register(this, BarScaleButton, BarScaleKey, HardwareModule.CycleBarScale);
    }
}
