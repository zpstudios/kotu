using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinUtil.Core.Contracts;

namespace WinUtil.Module.Hardware;

/// <summary>
/// 하드웨어 스펙 화면. WMI 수집은 HardwareModule.Poller(프로세스 공유 폴링 워커, A42)가
/// 전담하고, 뷰는 구독해서 스냅샷을 UI 스레드로 디스패치 받아 그리기만 한다.
/// 일반 모드는 라벨-값 리스트, 전체화면(F11/⛶)은 섹션 카드 대시보드로 보여준다(v0.42.0).
/// Refresh·Copy·⛶는 하단 바로 이동 — 셸이 TakeBottomBar()로 떼어간다.
/// </summary>
public sealed partial class HardwareView : UserControl, IBottomBarProvider
{
    private IReadOnlyList<HardwareSection> _sections = [];
    private AppWindow? _appWindow;
    private bool _dashboardRendered; // 같은 데이터로 대시보드를 다시 만들지 않기 위한 플래그
    private IDisposable? _subscription;  // 공유 폴러 구독(로드 중에만 유지 — 없으면 폴러 휴면)
    private bool _refreshPending;        // Busy 링 표시 중 — 다음 스냅샷 도착 시 끈다
    private string _dataSignature = ""; // 값이 안 바뀌면 UI 재구성 생략

    public HardwareView(OpenContext context)
    {
        _ = context; // 파일 컨텍스트 없음
        InitializeComponent();
        Loaded += (_, _) =>
        {
            HookPresenterChanged();
            Focus(FocusState.Programmatic); // F11/Esc 액셀러레이터가 바로 듣게
            if (_dataSignature.Length == 0) ShowBusy(); // 첫 데이터가 올 때까지 링 표시(기존 동작 유지)
            _subscription ??= HardwareModule.Poller.Subscribe(OnSnapshot); // 구독 즉시 1회 폴링됨
        };
        Unloaded += (_, _) =>
        {
            _subscription?.Dispose(); // 마지막 뷰가 내려가면 폴러는 휴면
            _subscription = null;
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
    private void OnSnapshot(IReadOnlyList<HardwareSection> sections)
        => DispatcherQueue?.TryEnqueue(() => ApplySnapshot(sections));

    /// <summary>
    /// UI 스레드: Busy 링을 끄고(수동 Refresh·첫 로드), 값이 지난번과 같으면
    /// UI 재구성을 생략한다(200ms마다 트리 재생성 방지). 겹침 방지는 폴러가 보장(단일 루프).
    /// </summary>
    private void ApplySnapshot(IReadOnlyList<HardwareSection> sections)
    {
        if (_refreshPending)
        {
            _refreshPending = false;
            Busy.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
        var signature = Signature(sections);
        if (signature == _dataSignature) return;
        _dataSignature = signature;
        _sections = sections;
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

    /// <summary>전체화면이면 대시보드, 아니면 리스트를 보여준다.</summary>
    private void UpdateViewMode()
    {
        var full = _appWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        ListScroller.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        DashboardScroller.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        if (full) RenderDashboard();
    }

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
        HardwareModule.Poller.Poke(); // 간격을 기다리지 않고 즉시 수집 — 결과는 OnSnapshot으로
    }

    /// <summary>모든 섹션을 텍스트로 클립보드에 복사 (사양 공유용).</summary>
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

        var package = new DataPackage();
        package.SetText(sb.ToString().TrimEnd());
        Clipboard.SetContent(package);
    }
}
