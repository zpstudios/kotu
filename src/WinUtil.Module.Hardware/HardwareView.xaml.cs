using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using WinUtil.Core.Contracts;

namespace WinUtil.Module.Hardware;

/// <summary>
/// 하드웨어 스펙 화면. WMI 수집·센서 수집은 HardwareModule.Poller(프로세스 공유 폴링 워커,
/// A42)가 전담하고, 뷰는 구독해서 스냅샷을 UI 스레드로 디스패치 받아 그리기만 한다.
/// 일반 모드는 라벨-값 리스트, 전체화면(F11/⛶)은 섹션 카드 대시보드로 보여준다(v0.42.0).
/// 센서 그래프 카드(A17)는 하단 바 한 줄 안에 산다(v0.64.2 사용자 지시) — 전체화면에서만
/// 셸 하단 바가 숨는 동안 SensorStrip으로 옮겨 표시. Refresh·Copy·⛶·센서를 담은
/// 하단 바는 셸이 TakeBottomBar()로 떼어간다.
/// </summary>
public sealed partial class HardwareView : UserControl, IBottomBarProvider
{
    private IReadOnlyList<HardwareSection> _sections = [];
    private AppWindow? _appWindow;
    private bool _dashboardRendered; // 같은 데이터로 대시보드를 다시 만들지 않기 위한 플래그
    private IDisposable? _subscription;  // 공유 폴러 구독(로드 중에만 유지 — 없으면 폴러 휴면)
    private bool _refreshPending;        // Busy 링 표시 중 — 다음 스냅샷 도착 시 끈다
    private string _dataSignature = ""; // 값이 안 바뀌면 UI 재구성 생략
    private readonly List<SensorCard> _cards = []; // 센서 그래프 카드 10개 (A17)
    private SensorFrame _lastFrame = SensorFrame.Empty; // Copy all에 센서 값 포함용

    public HardwareView(OpenContext context)
    {
        _ = context; // 파일 컨텍스트 없음
        InitializeComponent();
        BuildSensorCards();
        BuildIntervalFlyout(); // 리프레시 주기 선택 (A29)
        Loaded += (_, _) =>
        {
            HookPresenterChanged();
            Focus(FocusState.Programmatic); // F11/Esc 액셀러레이터가 바로 듣게
            if (_dataSignature.Length == 0) ShowBusy(); // 첫 데이터가 올 때까지 링 표시(기존 동작 유지)
            // 뷰 구독(스펙+센서, A18에서 API 분리) — 구독 즉시 1회 폴링됨
            _subscription ??= HardwareModule.SubscribeSnapshots(OnSnapshot);
            TraySensors.Changed -= UpdateTrayPins; // Loaded 중복 발화 대비 — 이중 구독 방지
            TraySensors.Changed += UpdateTrayPins; // 다른 창에서 토글해도 이 창 카드에 반영
            UpdateTrayPins();
        };
        Unloaded += (_, _) =>
        {
            _subscription?.Dispose(); // 마지막 뷰가 내려가면 폴러는 휴면(트레이 구독이 없다면)
            _subscription = null;
            TraySensors.Changed -= UpdateTrayPins;
            // A39: 토글 버튼은 인포 모듈에만 있으므로, 뷰가 내려가면(모듈 전환 등)
            // 끌 방법이 없는 상태가 남지 않게 항상 위 고정을 해제한다.
            if (_appWindow?.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = false;
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
    /// UI 스레드: Busy 링을 끄고(수동 Refresh·첫 로드), 센서 카드는 매 프레임 갱신,
    /// 스펙 리스트는 값이 지난번과 같으면 재구성을 생략한다(200ms마다 트리 재생성 방지).
    /// 겹침 방지는 폴러가 보장(단일 루프).
    /// </summary>
    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        if (_refreshPending)
        {
            _refreshPending = false;
            Busy.IsActive = false;
            RefreshButton.IsEnabled = true;
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

    /// <summary>Busy 링은 수동 Refresh·첫 로드에서만 돌린다 — 200ms마다 깜빡이면 안 된다.</summary>
    private void ShowBusy()
    {
        _refreshPending = true;
        Busy.IsActive = true;
        RefreshButton.IsEnabled = false;
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

    // ---------- 센서 그래프 스트립 (A17) ----------

    /// <summary>그래프 시간 창 — 최근 60초를 카드 폭에 맞춰 그린다.</summary>
    private static readonly TimeSpan GraphWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 카드 10개 배치(순서는 사용자 확정). 채널 정의(제목·색·선택자·포맷·스케일)는
    /// SensorChannels 단일 소스(A18에서 트레이와 공용화) — 색은 대시보드 섹션 액센트 계열:
    /// CPU 주황 / GPU 보라 / RAM 초록 / 팬 황금 / SSD 파랑.
    /// 스케일: 온도·부하는 0~100 고정, 전력·클럭·팬은 자동(하한 있는 관찰 최댓값).
    /// 카드는 하단 바 한 줄에 들어가는 36px 컴팩트형(v0.64.2 사용자 지시) — 그래프가 카드
    /// 전체를 채우고 제목·값이 그 위에 얹힌다. 배치는 기본 1줄(10칸), 창 폭이 좁아
    /// 카드가 MinCardWidth 밑으로 내려갈 때만 5칸(2줄)→4칸(3줄)로 늘어난다.
    /// </summary>
    private void BuildSensorCards()
    {
        foreach (var channel in SensorChannels.All)
            AddCard(channel);
        SensorGrid.SizeChanged += (_, e) => LayoutSensorCards(e.NewSize.Width);
        LayoutSensorCards(0); // 실측 폭을 알기 전엔 1줄로 시작
    }

    /// <summary>
    /// 이보다 카드가 좁아지면 줄 수를 늘린다. 하한 근거: 좌우 패딩 12 + 최장 값
    /// "4500 MHz"(13px SemiBold ≈ 62px) — 제목은 스타 칸이라 먼저 말줄임되므로
    /// 값만 안 잘리면 된다(v0.64.3 사용자 피드백: 104는 너무 일찍 줄바꿈됨).
    /// </summary>
    private const double MinCardWidth = 76;

    /// <summary>SensorGrid의 ColumnSpacing/RowSpacing과 같은 값 — 폭 계산에 쓴다.</summary>
    private const double CardSpacing = 8;

    private int _sensorColumns;

    /// <summary>폭에 맞는 칸 수(10→5→4)를 골라 카드를 재배치한다. 칸 수가 그대로면 아무것도 안 한다.</summary>
    private void LayoutSensorCards(double width)
    {
        var columns = 4; // 최저 단계 = 4칸 3줄 (그 밑으로는 안 내려간다)
        foreach (var c in new[] { 10, 5 })
        {
            if (width <= 0 || (width - (c - 1) * CardSpacing) / c >= MinCardWidth)
            {
                columns = c;
                break;
            }
        }
        if (columns == _sensorColumns) return;
        _sensorColumns = columns;

        SensorGrid.ColumnDefinitions.Clear();
        SensorGrid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++)
            SensorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = (_cards.Count + columns - 1) / columns;
        for (var r = 0; r < rows; r++)
            SensorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _cards.Count; i++)
        {
            Grid.SetColumn(_cards[i].Root, i % columns);
            Grid.SetRow(_cards[i].Root, i / columns);
            if (_cards[i].Root.Parent is null)
                SensorGrid.Children.Add(_cards[i].Root);
        }
    }

    private void AddCard(SensorChannel channel)
    {
        var accent = channel.Accent;
        var stroke = new SolidColorBrush(accent);
        var fill = new SolidColorBrush(Windows.UI.Color.FromArgb(56, accent.R, accent.G, accent.B));

        var titleText = new TextBlock
        {
            Text = channel.ShortTitle, // 초단축 제목(v0.64.3) — 전체 이름은 툴팁에
            FontSize = 11,
            Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 트레이 선택 핀(A18): 이 채널이 트레이에 표시 중이면 보인다.
        var pinIcon = new FontIcon
        {
            Glyph = "\uE718",
            FontSize = 10,
            Foreground = stroke,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var valueText = new TextBlock
        {
            Text = "—",
            FontSize = 13,
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

        var line = new Polyline { Stroke = stroke, StrokeThickness = 1.5 };
        var area = new Polygon { Fill = fill };
        var graphHost = new Grid(); // 카드 전체가 그래프 — 텍스트는 그 위에 겹친다(v0.64.2 컴팩트형)
        graphHost.Children.Add(area);
        graphHost.Children.Add(line);

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
            Height = 36, // 하단 바(44px 최소) 한 줄에 들어가는 높이
            Opacity = 0.45, // 값이 들어오면 1로
            Child = panel,
        };

        // 카드 클릭 = 트레이 표시 토글(A18, 사용자 확정 UX). 이미 2개면 오래된 선택이 밀려난다.
        root.Tapped += (_, _) => TraySensors.Toggle(channel.Id);
        ToolTipService.SetToolTip(root, $"{channel.Title} — click to show in tray (up to 2)");

        _cards.Add(new SensorCard
        {
            Root = root,
            ValueText = valueText,
            Pin = pinIcon,
            GraphHost = graphHost,
            Line = line,
            Area = area,
            Channel = channel,
            AutoMax = channel.AutoFloor,
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
    /// 최근 60초 이력을 카드 폭에 맞춰 꺾은선 + 면으로 그린다. x는 시간 비례(주기가
    /// 바뀌어도 올바름 — A29 대비), y는 고정(온도·부하) 또는 자동(관찰 최대의 1.1배,
    /// 세션 내 단조 증가) 스케일. 레이아웃 전(폭 0)엔 그리지 않는다 — 다음 프레임(≤200ms)에 그려진다.
    /// </summary>
    private static void RenderSparkline(SensorCard card, SensorFrame[] history)
    {
        var w = card.GraphHost.ActualWidth;
        var h = card.GraphHost.ActualHeight;
        if (w <= 2 || h <= 2 || history.Length == 0)
        {
            card.Line.Points = null;
            card.Area.Points = null;
            return;
        }

        var now = history[^1].Timestamp;
        var start = now - GraphWindow;

        // 자동 스케일 상한 갱신 (창 안의 최댓값 기준)
        if (card.Channel.FixedMax <= 0)
        {
            foreach (var f in history)
            {
                if (f.Timestamp < start) continue;
                if (card.Channel.Select(f) is { } v && v * 1.1f > card.AutoMax) card.AutoMax = v * 1.1f;
            }
        }
        var max = card.Channel.FixedMax > 0 ? card.Channel.FixedMax : card.AutoMax;
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

            var x = (f.Timestamp - start).TotalSeconds / GraphWindow.TotalSeconds * w;
            var y = h - Math.Clamp(v / max, 0f, 1f) * h;
            linePoints.Add(new Windows.Foundation.Point(x, y));
            areaPoints.Add(new Windows.Foundation.Point(x, y));
            if (firstX < 0) firstX = x;
            lastX = x;
        }

        if (linePoints.Count < 2)
        {
            card.Line.Points = null;
            card.Area.Points = null;
            return;
        }
        // 면은 선 아래를 바닥까지 닫는다
        areaPoints.Add(new Windows.Foundation.Point(lastX, h));
        areaPoints.Add(new Windows.Foundation.Point(firstX, h));
        card.Line.Points = linePoints;
        card.Area.Points = areaPoints;
    }

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
        public required TextBlock ValueText;
        public required FontIcon Pin;  // 트레이 표시 중 핀 (A18)
        public required Grid GraphHost;
        public required Polyline Line;
        public required Polygon Area;
        public required SensorChannel Channel;
        public float AutoMax;          // 자동 스케일: 채널 하한으로 시작해 관찰 최대 ×1.1로 커진다
        public bool HasEverHadValue;   // 한 번도 값이 없던 채널은 흐리게
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
    }

    private bool _fullScreen;

    /// <summary>SensorGrid를 하단 바(BarGrid 가운데 칸)와 SensorStrip 사이에서 옮긴다.</summary>
    private void PlaceSensorGrid(bool inBar)
    {
        Panel target = inBar ? BarGrid : StripPanel;
        if (ReferenceEquals(SensorGrid.Parent, target)) return;
        (SensorGrid.Parent as Panel)?.Children.Remove(SensorGrid);
        target.Children.Add(SensorGrid); // Grid.Column=3은 요소에 붙어 있어 바로 복귀해도 유효
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
        appWindow.SetPresenter(appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
    }

    private void OnFullScreenButtonClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    // ---------- 리프레시 주기 선택 + 맥박(EKG) 그래프 (A29) ----------

    /// <summary>맥박 그래프 시간 창 — 1000ms 주기에서도 박동 5개가 보인다.</summary>
    private static readonly TimeSpan PulseWindow = TimeSpan.FromSeconds(5);

    /// <summary>창 안의 스냅샷 도착 시각들 — UI 스레드에서만 접근.</summary>
    private readonly List<DateTime> _pulseTicks = [];

    /// <summary>주기 선택 플라이아웃(100/300/1000ms) 구성 + 현재 값 표기(A29).</summary>
    private void BuildIntervalFlyout()
    {
        var flyout = new MenuFlyout();
        foreach (var ms in HardwareModule.RefreshChoices)
        {
            var choice = ms; // 클로저 캡처 고정
            var item = new MenuFlyoutItem { Text = $"{choice} ms" };
            item.Click += (_, _) =>
            {
                HardwareModule.SetRefreshMs(choice); // 폴러 즉시 반영 + 설정 저장
                IntervalText.Text = $"{choice} ms";
            };
            flyout.Items.Add(item);
        }
        IntervalButton.Flyout = flyout;
        IntervalText.Text = $"{HardwareModule.RefreshMs} ms"; // 설정 복원값 표기
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

    /// <summary>
    /// 병원 심박 모니터풍: 평평한 기준선 위에 도착 시각마다 QRS풍 스파이크(위로 크게 →
    /// 아래로 살짝 → 복귀). 주기가 바뀌면 스파이크 간격이 그대로 벌어지고 좁아진다 —
    /// "리프레시 타이밍이 튀는" 모습 자체가 정보다. 폴링 주기마다만 다시 그린다(비용 미미).
    /// </summary>
    private void RenderPulse(DateTime now)
    {
        var w = PulseHost.ActualWidth;
        var h = PulseHost.ActualHeight;
        if (w <= 2 || h <= 2) return; // 레이아웃 전 — 다음 스냅샷에서 그려진다

        var baseline = h * 0.68;
        var start = now - PulseWindow;
        var points = new Microsoft.UI.Xaml.Media.PointCollection
        {
            new Windows.Foundation.Point(0, baseline),
        };
        foreach (var tick in _pulseTicks)
        {
            var x = (tick - start).TotalSeconds / PulseWindow.TotalSeconds * w;
            points.Add(new Windows.Foundation.Point(Math.Max(0, x - 3), baseline));
            points.Add(new Windows.Foundation.Point(x - 1, h * 0.12));                          // R파(위로 크게)
            points.Add(new Windows.Foundation.Point(x + 1, Math.Min(h - 1, baseline + h * 0.22))); // S파(아래로 살짝)
            points.Add(new Windows.Foundation.Point(Math.Min(w, x + 3), baseline));
        }
        points.Add(new Windows.Foundation.Point(w, baseline));
        PulseLine.Points = points;
    }

    // ---------- Always on top (A39 — 사용자 확정: 인포 모듈 전용) ----------

    private void OnTopToggleChanged(object sender, RoutedEventArgs e) => ApplyAlwaysOnTop();

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

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ShowBusy();
        HardwareModule.RefreshNow(); // WMI 스펙 강제 재수집 + 즉시 폴링 — 결과는 OnSnapshot으로
    }

    /// <summary>모든 섹션 + 현재 센서 값을 텍스트로 클립보드에 복사 (사양 공유용).</summary>
    private void OnCopyClick(object sender, RoutedEventArgs e)
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
}
