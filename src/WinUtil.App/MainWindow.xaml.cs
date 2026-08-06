using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinUtil.Core.Cli;
using WinUtil.Core.Contracts;
using WinUtil.Core.Routing;
using WinUtil.Core.Settings;

namespace WinUtil.App;

public sealed partial class MainWindow : Window
{
    private static readonly string IconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

    private static readonly string SponsorLogoPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sponsor-msi.png");

    private readonly FileTypeRouter _router;
    private readonly WindowManager _manager;
    private readonly TrayIcon _tray;
    private readonly ISettingsService _settings;
    private double _uiScaleFactor = 1.0; // 시스템 DPI 대비 상대 배율 (1.0 = 오버라이드 없음)
    private bool _xamlRootHooked;

    // ---- 내장 탐색기 + Alt/Ctrl 오버레이 상태 (v0.25.0, docs/explorer-plan.md) ----
    private IModule? _currentModule;      // 지금 보여주는 모듈 (탐색기 필터·Alt 목록에 사용)
    private string? _currentFilePath;     // 현재 콘텐츠 파일 (null = 빈 상태 → 탐색기 표시)
    private ExplorerPane? _emptyExplorer; // 빈 상태 중앙 탐색기 (지연 생성)
    private ExplorerPane? _altList;       // Alt 홀드 우측 리스트 (지연 생성)
    private bool _altHeld;
    private bool _ctrlHeld;
    private bool _infoPinned;             // Ctrl 2연타로 고정된 정보 오버레이
    private bool _altPinned;              // Alt 2연타로 고정된 우측 리스트 (v0.32.0)
    private DateTime _lastCtrlDown = DateTime.MinValue;
    private DateTime _lastAltDown = DateTime.MinValue;
    private int _infoSeq;                 // 정보 로드 경쟁 방지
    private string? _infoPath;            // 정보 캐시 (파일별 1회 로드)
    private string? _infoText;

    /// <summary>지금 보여주는 모듈 ID. 빈 셸·설정·미지원 파일 안내면 null. 창 재사용 판단에 쓴다.</summary>
    public string? CurrentModuleId { get; private set; }

    /// <summary>아직 아무 콘텐츠도 안 연 빈 셸인지. 창 재사용 판단에 쓴다.</summary>
    public bool IsUntouched { get; private set; } = true;

    public MainWindow(WindowManager manager)
    {
        InitializeComponent();
        Title = "ZP";
        _manager = manager;
        _router = App.Services.GetRequiredService<FileTypeRouter>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        BuildStartMenu();

        // UI 스케일 오버라이드(v0.24.0): 설정값 적용 + 설정 변경·창 크기·모니터 DPI 변화에 추종.
        // RasterizationScale은 XamlRoot 준비 후에만 유효하므로 Loaded에서 시작한다.
        RootLayout.Loaded += (_, _) =>
        {
            if (!_xamlRootHooked && RootLayout.XamlRoot is { } xr)
            {
                _xamlRootHooked = true;
                xr.Changed += (_, _) => ApplyUiScale(); // 모니터 간 이동 등으로 시스템 DPI가 바뀔 때
            }
            ApplyUiScale();
        };
        ScaleHost.SizeChanged += (_, _) => LayoutUiScale();
        UiScale.Changed += ApplyUiScale;
        Closed += (_, _) => UiScale.Changed -= ApplyUiScale;

        // Alt/Ctrl 홀드 감지(v0.25.0): 포커스가 모듈 뷰 안에 있어도 받도록 창 루트에서
        // handledEventsToo로 구독한다. 창 비활성화로 KeyUp을 놓치면 홀드 상태를 초기화.
        RootLayout.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
        RootLayout.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnRootKeyUp), handledEventsToo: true);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) ResetKeyOverlays();
        };

        // 타이틀바·작업표시줄 아이콘 (unpackaged는 exe 아이콘만으로는 타이틀바가 비어 보인다)
        if (File.Exists(IconPath))
        {
            AppWindow.SetIcon(IconPath);
            WindowIcon.Apply(this, IconPath); // 작업표시줄 기본 문서 아이콘 문제 보정 (실기기)
        }

        // 창 헤더만 브랜드 색(#15072E) — 본문은 시스템 테마 기본값
        TitleBarTheming.Apply(AppWindow.TitleBar);

        // 전체화면(동영상 Enter/F11)에서는 하단 바를 통째로 숨긴다 —
        // 재생줄이 하단 바로 통합되면서(v0.21.0) 전체화면 겹침도 여기서 함께 해결
        AppWindow.Changed += (sender, args) =>
        {
            if (!args.DidPresenterChange) return;
            var full = sender.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
            BottomBar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            BottomBarRow.Height = full ? new GridLength(0) : new GridLength(44);
        };

        // 창별 트레이 미니 아이콘: 좌클릭=활성화, 우클릭=메뉴, 툴팁=창 제목
        _tray = new TrayIcon(File.Exists(IconPath) ? IconPath : null);
        _tray.ActivateRequested += BringToFront;
        _tray.CloseRequested += Close;
        _tray.ExitAllRequested += _manager.CloseAll;
        Closed += (_, _) => _tray.Dispose();
    }

    // ---------- UI 스케일 오버라이드 (v0.24.0) ----------

    /// <summary>
    /// 설정의 배율(%)을 시스템 DPI 대비 상대 배율로 환산한다.
    /// 예: 설정 100% + 시스템 150% 모니터 → 2/3배. 설정 0(시스템 기본)이면 1.0.
    /// </summary>
    private void ApplyUiScale()
    {
        // 설정 화면(다른 창)에서 바꿔도 이 창에 적용되도록 이벤트로 불린다 — UI 스레드 보장.
        if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(ApplyUiScale);
            return;
        }

        var percent = _settings.Get(UiScale.SettingKey, 0);
        var system = RootLayout.XamlRoot?.RasterizationScale ?? 1.0;
        _uiScaleFactor = percent <= 0 ? 1.0 : Math.Clamp(percent / 100.0 / system, 0.25, 4.0);
        LayoutUiScale();
    }

    /// <summary>
    /// RootLayout에 ScaleTransform(배율)과 역수 크기를 적용한다.
    /// 레이아웃은 1/배율 크기로 계산되고 렌더링에서 배율만큼 확대되므로 창을 꽉 채운다.
    /// </summary>
    private void LayoutUiScale()
    {
        if (Math.Abs(_uiScaleFactor - 1.0) < 0.001)
        {
            RootLayout.RenderTransform = null;
            RootLayout.ClearValue(FrameworkElement.WidthProperty);
            RootLayout.ClearValue(FrameworkElement.HeightProperty);
            RootLayout.HorizontalAlignment = HorizontalAlignment.Stretch;
            RootLayout.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        RootLayout.HorizontalAlignment = HorizontalAlignment.Left;
        RootLayout.VerticalAlignment = VerticalAlignment.Top;
        RootLayout.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        RootLayout.RenderTransform = new ScaleTransform { ScaleX = _uiScaleFactor, ScaleY = _uiScaleFactor };
        if (ScaleHost.ActualWidth > 0)
        {
            RootLayout.Width = ScaleHost.ActualWidth / _uiScaleFactor;
            RootLayout.Height = ScaleHost.ActualHeight / _uiScaleFactor;
        }
    }

    /// <summary>창 제목과 트레이 툴팁을 함께 갱신한다.</summary>
    private void SetTitle(string title)
    {
        Title = title;
        _tray.SetTooltip(title);
    }

    // ---------- 시작 메뉴 (하단 바에서 위로 떠오르는 플라이아웃) ----------

    /// <summary>
    /// 시작 메뉴 구성. 패널은 위→아래 순서로 채우므로, 사용자가 정한 "아래부터" 순서
    /// (사진-영상-문서, 여백, 압축, 여백x2, 광고)를 뒤집어 넣는다.
    /// </summary>
    private void BuildStartMenu()
    {
        StartMenuPanel.Children.Clear();

        // 최상단: 스폰서(광고) 자리 — 지금은 MSI 로고 플레이스홀더, 파일 교체만으로 변경 가능
        StartMenuPanel.Children.Add(BuildSponsorCard());
        StartMenuPanel.Children.Add(Divider()); // 그룹 경계는 구분선으로 명확히 (v0.26.0 사용자 요청)

        // Settings·Hardware-info 묶음 (사용자 지정: zip 위에 공백 두고 Hardware-info, 그 위 Settings)
        AddSettingsItem();
        AddModuleItem("hardware");
        StartMenuPanel.Children.Add(Divider());

        AddModuleItem("archive");
        StartMenuPanel.Children.Add(Divider());

        // 사진-영상-문서 그룹 (아래부터 사진 → 위로 갈수록 문서)
        AddDocumentPlaceholder();
        AddModuleItem("video");
        AddModuleItem("image");
        // 하단 바 우측 Info·Settings 아이콘은 제거(v0.28.2) — 시작 메뉴 항목으로 일원화.
    }

    private void AddModuleItem(string moduleId)
    {
        var module = _router.Modules.FirstOrDefault(m => m.Id == moduleId);
        if (module is null) return;

        var item = MakeMenuItem(module.IconGlyph, module.DisplayName);
        item.Click += (_, _) =>
        {
            StartFlyout.Hide();
            OpenModule(module);
        };
        StartMenuPanel.Children.Add(item);
    }

    /// <summary>시작 메뉴의 Settings 항목 — 하단 바 우측 아이콘과 같은 동작.</summary>
    private void AddSettingsItem()
    {
        var item = MakeMenuItem("\uE713", "Settings");
        item.Click += (_, _) =>
        {
            StartFlyout.Hide();
            OnSettingsClick(item, new RoutedEventArgs());
        };
        StartMenuPanel.Children.Add(item);
    }

    /// <summary>문서 모듈 자리(마크다운·PDF·HWP 등 예정) — 메뉴 배치를 먼저 확정해 둔다.</summary>
    private void AddDocumentPlaceholder()
    {
        var item = MakeMenuItem("", "Documents");
        item.IsEnabled = false;
        ToolTipService.SetToolTip(item, "Coming soon — Markdown, PDF, HWP, and more");
        StartMenuPanel.Children.Add(item);
    }

    private static Button MakeMenuItem(string glyph, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        content.Children.Add(new TextBlock { Text = label });

        return new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
        };
    }

    private UIElement BuildSponsorCard()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "SPONSOR", FontSize = 10, Opacity = 0.5 });

        if (File.Exists(SponsorLogoPath))
        {
            // 광고 규격(v0.35.0 사용자 확정): 논리 100×50 = DPI 100%에서 100×50px.
            // 고DPI에서는 시스템 배율만큼 자동 확대된다. 로고는 박스 안에 Uniform으로 맞춘다.
            panel.Children.Add(new Image
            {
                Width = 100,
                Height = 50,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(SponsorLogoPath)),
            });
        }

        return new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = panel,
        };
    }

    /// <summary>시작 메뉴 그룹 구분선: 여백 + 1px 라인 (v0.26.0, 공백만으로는 정리가 안 보인다는 피드백).</summary>
    private static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(4, 8, 4, 8),
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    };

    private void OpenModule(IModule module)
    {
        SetTitle($"ZP {module.DisplayName}");
        ShowModule(module, OpenContext.Empty);
    }

    /// <summary>
    /// 앱 첫 화면 기본 뷰(Info/하드웨어). 사용자가 고른 화면이 아니므로
    /// IsUntouched를 되돌려서, 첫 파일 열기가 새 창을 만들지 않고 이 창을 쓰게 한다.
    /// </summary>
    public void ShowDefaultModule()
    {
        var module = _router.Modules.FirstOrDefault(m => m.Id == "hardware");
        if (module is null) return;
        OpenModule(module);
        IsUntouched = true;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SetTitle("ZP Settings");
        ModuleHost.Content = new SettingsView(_router);
        ModuleBarHost.Content = null;
        CurrentModuleId = null;
        IsUntouched = false;
        UpdateModeIndicator(null, isSettings: true);
        SetContentState(null, null);
    }

    // ---------- 파일 열기 ----------

    /// <summary>파일 라우팅의 종착점: 확장자로 모듈을 찾아 뷰를 띄운다.</summary>
    public void OpenFile(string path)
    {
        var module = _router.Resolve(path);
        if (module is null)
        {
            ModuleHost.Content = new TextBlock
            {
                Text = $"Unsupported file type: {Path.GetFileName(path)}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ModuleBarHost.Content = null;
            CurrentModuleId = null;
            IsUntouched = false;
            UpdateModeIndicator(null);
            SetContentState(null, null);
            return;
        }
        SetTitle($"ZP {module.DisplayName} — {Path.GetFileName(path)}");
        ShowModule(module, OpenContext.ForFile(path));
    }

    /// <summary>탐색기 우클릭 동사(여기에 풀기/압축) 진입점. 동사는 압축 모듈이 처리한다.</summary>
    public void OpenVerb(LaunchRequest request)
    {
        if (request.FilePath is not { } file) return;

        var module = _router.Modules.FirstOrDefault(m => m.Id == "archive");
        if (module is null || request.VerbToken is not { } token)
        {
            OpenFile(file);
            return;
        }

        SetTitle($"ZP {module.DisplayName} — {Path.GetFileName(file)}");
        ShowModule(module, new OpenContext { FilePath = file, Arguments = [token] });
    }

    // ---------- 창 전체 드래그&드롭 → 파일 라우팅 ----------

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return; // 압축 뷰 등 모듈이 이미 소비한 드래그
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var path = items.OfType<Windows.Storage.StorageFile>()
            .Select(f => f.Path)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (path is not null) OpenFile(path);
    }

    private void ShowModule(IModule module, OpenContext context)
    {
        var view = (UIElement)module.CreateView(context);
        ModuleHost.Content = view;
        // 모듈이 제공하는 하단 바 줄(동영상 트랜스포트 등)을 셸 하단 바에 통합 (v0.21.0)
        ModuleBarHost.Content = (view as IBottomBarProvider)?.TakeBottomBar() as UIElement;
        CurrentModuleId = module.Id;
        IsUntouched = false;
        UpdateModeIndicator(module);

        // 뷰 내부 열기(열기 버튼·◀/▶ 탐색·테스트 클립)도 셸과 동기화 (v0.25.0)
        if (view is IContentStateSource source)
            source.ContentOpened += path => DispatcherQueue.TryEnqueue(() => OnContentOpened(path));
        SetContentState(module, context.FilePath);
    }

    // ---------- 내장 탐색기 + Alt/Ctrl 오버레이 (v0.25.0) ----------

    /// <summary>현재 모듈·파일 상태를 바꾸고 탐색기/오버레이 표시를 갱신한다.</summary>
    private void SetContentState(IModule? module, string? filePath)
    {
        _currentModule = module;
        _currentFilePath = filePath;
        _infoPath = null;
        _infoText = null;
        UpdateEmptyExplorer();
        ResetKeyOverlays();
    }

    /// <summary>모듈 뷰가 파일을 열었다는 알림(IContentStateSource) — 탐색기를 내리고 기준 경로 갱신.</summary>
    private void OnContentOpened(string path)
    {
        _currentFilePath = path;
        _infoPath = null;
        _infoText = null;
        UpdateEmptyExplorer();
        if (AltOverlayRoot.Visibility == Visibility.Visible) ShowAltOverlay(); // 폴더가 바뀌었을 수 있다
        if (InfoOverlay.Visibility == Visibility.Visible) UpdateInfoOverlay();
    }

    /// <summary>
    /// 빈 상태(파일 없이 연 압축/이미지/동영상 모듈)면 중앙에 탐색기를 띄운다.
    /// 시작 위치는 바탕화면, 파일은 담당 확장자만(사용자 확정). Hardware/Settings에는 띄우지 않는다.
    /// </summary>
    private void UpdateEmptyExplorer()
    {
        if (_currentFilePath is null &&
            _currentModule is { Id: "archive" or "image" or "video" } module)
        {
            if (_emptyExplorer is null)
            {
                _emptyExplorer = new ExplorerPane();
                _emptyExplorer.FileActivated += OpenFile;
                ExplorerHost.Children.Add(_emptyExplorer);
            }
            _emptyExplorer.NavigateTo(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                module.SupportedExtensions);
            ExplorerHost.Visibility = Visibility.Visible;
        }
        else
        {
            ExplorerHost.Visibility = Visibility.Collapsed;
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Menu)
        {
            if (!_altHeld && !e.KeyStatus.WasKeyDown)
            {
                _altHeld = true;

                // Alt 2연타 = 고정 토글, 다시 2연타로 해제 (v0.32.0 — Ctrl 정보 오버레이와 동일 UX).
                // 고정하면 키를 놓거나 창 활성화를 뺏겨도(화면 공유 컨트롤 등) 리스트가 유지된다.
                var now = DateTime.UtcNow;
                if ((now - _lastAltDown).TotalMilliseconds < 450)
                {
                    _altPinned = !_altPinned;
                    _lastAltDown = DateTime.MinValue;
                }
                else
                {
                    _lastAltDown = now;
                }
                UpdateAltOverlay();
            }
            // Alt 기본 동작(메뉴 모드 진입)과의 충돌 방지 — 오버레이가 떠 있을 때만 소비한다.
            if (AltOverlayRoot.Visibility == Visibility.Visible) e.Handled = true;
        }
        else if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)
        {
            if (_ctrlHeld || e.KeyStatus.WasKeyDown) return;
            _ctrlHeld = true;
            if (_currentFilePath is null) return; // 콘텐츠 없으면 정보도 핀 토글도 없다

            // Ctrl 2연타 = 고정 토글, 다시 2연타로 해제 (사용자 확정)
            var now = DateTime.UtcNow;
            if ((now - _lastCtrlDown).TotalMilliseconds < 450)
            {
                _infoPinned = !_infoPinned;
                _lastCtrlDown = DateTime.MinValue;
            }
            else
            {
                _lastCtrlDown = now;
            }
            UpdateInfoOverlay();
        }
    }

    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Menu)
        {
            if (_altHeld) e.Handled = true;
            _altHeld = false;
            UpdateAltOverlay(); // 고정(_altPinned) 상태면 유지된다 (v0.32.0)
        }
        else if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)
        {
            _ctrlHeld = false;
            UpdateInfoOverlay();
        }
    }

    /// <summary>창 비활성화 등으로 KeyUp을 놓칠 수 있어 홀드 상태를 초기화한다(고정 오버레이는 유지).</summary>
    private void ResetKeyOverlays()
    {
        _altHeld = false;
        _ctrlHeld = false;
        UpdateAltOverlay();
        UpdateInfoOverlay();
    }

    /// <summary>Alt 홀드(또는 2연타 고정) 리스트 오버레이의 표시 상태를 갱신한다 (v0.32.0).</summary>
    private void UpdateAltOverlay()
    {
        var show = (_altHeld || _altPinned) && _currentFilePath is not null;
        if (show) ShowAltOverlay();
        else AltOverlayRoot.Visibility = Visibility.Collapsed;
        AltPinnedText.Visibility = AltOverlayRoot.Visibility == Visibility.Visible && _altPinned
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Alt 홀드: 현재 파일이 있는 폴더의 리스트 뷰를 우측 30%에 띄운다(콘텐츠 로딩 후에만).</summary>
    private void ShowAltOverlay()
    {
        if (_currentModule is null || _currentFilePath is null) return;
        if (Path.GetDirectoryName(_currentFilePath) is not { } folder || !Directory.Exists(folder)) return;

        if (_altList is null)
        {
            _altList = new ExplorerPane();
            _altList.ConfigureListOnly();
            _altList.FileActivated += OpenFile;
            AltListHost.Content = _altList;
        }
        _altList.NavigateTo(folder, _currentModule.SupportedExtensions);
        AltOverlayRoot.Visibility = Visibility.Visible;
    }

    /// <summary>Ctrl 홀드(또는 고정) 정보 오버레이의 표시 상태를 갱신한다.</summary>
    private void UpdateInfoOverlay()
    {
        var show = (_ctrlHeld || _infoPinned) && _currentFilePath is not null;
        InfoOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InfoOverlay.IsHitTestVisible = show && _infoPinned; // 고정했을 때만 스크롤 등 상호작용 허용
        InfoPinnedText.Visibility = show && _infoPinned ? Visibility.Visible : Visibility.Collapsed;
        if (show) _ = LoadContentInfoAsync();
    }

    /// <summary>모듈 제공 정보(IContentInfoProvider) 우선, 없으면 파일 기본 정보. 파일별 1회 캐시.</summary>
    private async Task LoadContentInfoAsync()
    {
        var path = _currentFilePath;
        if (path is null) return;
        if (_infoPath == path && _infoText is not null)
        {
            InfoOverlayText.Text = _infoText;
            return;
        }

        var seq = ++_infoSeq;
        InfoOverlayText.Text = "Loading info...";

        string? text = null;
        try
        {
            if (ModuleHost.Content is IContentInfoProvider provider)
                text = await provider.GetContentInfoAsync();
        }
        catch
        {
            // 모듈 정보 실패 → 아래 파일 기본 정보로 대체
        }
        text ??= BuildBasicFileInfo(path);

        if (seq != _infoSeq || _currentFilePath != path) return; // 그새 파일이 바뀜
        _infoPath = path;
        _infoText = text;
        InfoOverlayText.Text = text;
    }

    private static string BuildBasicFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Name}\n{ExplorerListing.FormatSize(info.Length)}\n"
                 + $"{info.LastWriteTime:yyyy-MM-dd HH:mm}\n{info.DirectoryName}";
        }
        catch (Exception ex)
        {
            return Path.GetFileName(path) + "\nInfo unavailable: " + ex.Message;
        }
    }

    // ---------- 현재 모드 시각 표시 (v0.20.0 → v0.26.0 개편) ----------

    /// <summary>
    /// 현재 모드 표시 갱신: 색 구분은 하단 바 스트립/칩 색 대신 창(타이틀바·작업표시줄)·
    /// 트레이의 모듈 색 ZP 아이콘이 담당한다(사용자 요청, v0.26.0).
    /// 칩은 모듈 글리프+브랜드명을 중립색으로만 표시.
    /// </summary>
    private void UpdateModeIndicator(IModule? module, bool isSettings = false)
    {
        ApplyWindowIcon(module?.Id);

        if (module is null && !isSettings)
        {
            ModeChip.Visibility = Visibility.Collapsed;
            return;
        }

        // 모듈이 하단 바 줄을 차지하면 칩은 생략 (v0.21.0)
        ModeChip.Visibility = ModuleBarHost.Content is null
            ? Visibility.Visible : Visibility.Collapsed;

        if (isSettings || module is null)
        {
            ModeChipIcon.Glyph = "\uE713"; // Settings gear
            ModeChipText.Text = "Settings";
        }
        else
        {
            ModeChipIcon.Glyph = module.IconGlyph;
            ModeChipText.Text = module.BrandName;
        }
    }

    /// <summary>타이틀바·작업표시줄·트레이 아이콘을 현재 모듈 색 ZP 아이콘으로 교체(v0.26.0).</summary>
    private void ApplyWindowIcon(string? moduleId)
    {
        var name = moduleId switch
        {
            "archive" => "app-archive.ico",
            "image" => "app-image.ico",
            "video" => "app-video.ico",
            "hardware" => "app-hardware.ico",
            _ => "app.ico", // 빈 셸·설정·미지원 파일 = 중립(브랜드 색)
        };
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        if (!File.Exists(path)) path = IconPath;
        if (!File.Exists(path)) return;

        AppWindow.SetIcon(path);
        WindowIcon.Apply(this, path);
        _tray.SetIcon(path);
    }

    public void BringToFront()
    {
        AppWindow.MoveInZOrderAtTop();
        Activate();
    }
}
