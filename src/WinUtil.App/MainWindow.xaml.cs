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

    private readonly FileTypeRouter _router;
    private readonly WindowManager _manager;
    private readonly TrayIcon _tray;
    private readonly ISettingsService _settings;
    private double _uiScaleFactor = 1.0; // 시스템 DPI 대비 상대 배율 (1.0 = 오버라이드 없음)
    private bool _xamlRootHooked;

    // ---- 내장 탐색기 + Alt/Ctrl 오버레이 상태 (v0.25.0; 정보 오버레이 키는
    //      v0.45.0에 Ctrl → Shift로 갔다가 A32에서 Ctrl로 회귀 — 모듈 전환이
    //      숫자 단독 키가 되면서 Ctrl이 다시 비었고, Shift는 A24 새 창 더블클릭이 쓴다) ----
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
        RegisterShortcuts(); // Ctrl+` 시작 메뉴, Ctrl+숫자 모듈 전환 (v0.45.0)
        RestoreWindowSize(); // 마지막 창 크기 복원 + 닫을 때 저장 (v0.55.0)
        // 광고 로테이션: 메뉴가 열릴 때 현재 분 기준 이미지로 갱신 (같은 분 = 같은 이미지)
        StartFlyout.Opening += (_, _) => UpdateSponsorImage();

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

        // Alt/Ctrl 홀드 감지(v0.25.0, A32에서 정보 키 Shift→Ctrl 회귀): 포커스가 모듈 뷰 안에 있어도 받도록 창 루트에서
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
            BottomBarRow.Height = full ? new GridLength(0) : GridLength.Auto; // 평소 Auto(최소 44, v0.64.2)
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

    // ---------- 창 크기 저장/복원 (v0.55.0 사용자 요청) ----------

    /// <summary>마지막으로 닫힌 창의 크기(물리 픽셀)를 복원한다. 저장값이 없으면 기본 크기.</summary>
    private void RestoreWindowSize()
    {
        var w = _settings.Get("window.width", 0);
        var h = _settings.Get("window.height", 0);
        if (w >= 320 && h >= 240)
        {
            try { AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h)); }
            catch { /* 모니터 구성이 바뀌었어도 열리기는 해야 한다 */ }
        }
        Closed += (_, _) => SaveWindowSize();
    }

    /// <summary>전체화면·최대화 상태는 저장하지 않는다 — 다음 실행이 이상한 크기로 열리지 않게.</summary>
    private void SaveWindowSize()
    {
        try
        {
            if (AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen) return;
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized }) return;
            var size = AppWindow.Size;
            if (size.Width < 320 || size.Height < 240) return;
            _settings.Set("window.width", size.Width);
            _settings.Set("window.height", size.Height);
            _settings.Save();
        }
        catch
        {
            // 저장 실패가 종료를 막으면 안 된다.
        }
    }

    // ---------- 단축키 (v0.45.0 사용자 지정) ----------

    /// <summary>
    /// 모듈 번호(메뉴 아래→위 순서): 1=이미지, 2=영상, 3=문서, 4=압축, 5=하드웨어.
    /// A32: Ctrl 없이 숫자 단독(사용자 확정) — Ctrl은 정보 오버레이로 회귀.
    /// 힌트 문자열은 시작 메뉴 항목 마우스 오버 시 툴팁으로 보조 표시된다.
    /// </summary>
    private static readonly (string Id, VirtualKey Key, string Hint)[] ModuleShortcuts =
    [
        ("image", VirtualKey.Number1, "1"),
        ("video", VirtualKey.Number2, "2"),
        ("document", VirtualKey.Number3, "3"),
        ("archive", VirtualKey.Number4, "4"),
        ("hardware", VirtualKey.Number5, "5"),
    ];

    private const string SettingsShortcutHint = "0";

    /// <summary>
    /// `(1 왼쪽 키) = 시작 메뉴, 숫자 = 모듈 전환, 0 = Settings — 전부 수정자 없는 단독 키(A32).
    /// Ctrl+N = 새 창(A24)만 수정자 유지. 단독 키는 텍스트 입력란에 포커스가 있으면
    /// 가로채지 않고 통과시킨다(A32 예외 — 압축 암호 입력 등에서 숫자를 쳐야 하므로).
    /// </summary>
    private void RegisterShortcuts()
    {
        // 액셀러레이터 키 이름 툴팁이 화면 중앙에 뜨는 WinUI 기본 동작 방지 (모듈 뷰들과 동일)
        RootLayout.KeyboardAcceleratorPlacementMode =
            Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        AddShortcut((VirtualKey)192, () => StartFlyout.ShowAt(StartButton)); // VK_OEM_3 = `(~)
        foreach (var (id, key, _) in ModuleShortcuts)
            AddShortcut(key, () => OpenModuleById(id));
        AddShortcut(VirtualKey.Number0, () => OnSettingsClick(StartButton, new RoutedEventArgs()));
        // 새 창 = 지금 보는 모듈의 빈 인스턴스(A24 사용자 확정). 설정 화면 등 모듈 없는 창은 기본 화면으로.
        AddShortcut(VirtualKey.N, () => _manager.OpenNewWindow(CurrentModuleId),
            Windows.System.VirtualKeyModifiers.Control);
    }

    private void AddShortcut(VirtualKey key, Action action,
        Windows.System.VirtualKeyModifiers modifiers = Windows.System.VirtualKeyModifiers.None)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            // A32 예외: 단독 키는 입력 컨트롤 타이핑을 뺏으면 안 된다.
            if (modifiers == Windows.System.VirtualKeyModifiers.None && IsTextInputFocused())
            {
                e.Handled = false; // 계속 흘려보내 컨트롤이 문자를 받게
                return;
            }
            e.Handled = true;
            action();
        };
        RootLayout.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>포커스가 텍스트 입력 컨트롤(TextBox·PasswordBox·RichEditBox 계열)에 있는지.</summary>
    private bool IsTextInputFocused()
        => RootLayout.XamlRoot is { } xr
           && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xr)
               is TextBox or PasswordBox or RichEditBox;

    /// <summary>단축키·센서 트레이(A18)로 모듈 전환. 이미 그 모듈이면 아무것도 하지 않는다(보던 파일 보호).</summary>
    internal void OpenModuleById(string id)
    {
        if (CurrentModuleId == id) return;
        var module = _router.Modules.FirstOrDefault(m => m.Id == id);
        if (module is not null) OpenModule(module);
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

        // New window(A24) — Settings 위, 최상단 그룹
        AddNewWindowItem();
        // Settings·Hardware-info 묶음 (사용자 지정: zip 위에 공백 두고 Hardware-info, 그 위 Settings)
        AddSettingsItem();
        AddModuleItem("hardware");
        StartMenuPanel.Children.Add(Divider());

        AddModuleItem("archive");
        StartMenuPanel.Children.Add(Divider());

        // 사진-영상-문서 그룹 (아래부터 사진 → 위로 갈수록 문서)
        AddModuleItem("document"); // v0.44.0 실제 모듈로 교체 (텍스트·마크다운 1단계)
        AddModuleItem("video");
        AddModuleItem("image");
        // 하단 바 우측 Info·Settings 아이콘은 제거(v0.28.2) — 시작 메뉴 항목으로 일원화.
    }

    private void AddModuleItem(string moduleId)
    {
        var module = _router.Modules.FirstOrDefault(m => m.Id == moduleId);
        if (module is null) return;

        var hint = ModuleShortcuts.FirstOrDefault(s => s.Id == moduleId).Hint;
        var item = MakeMenuItem(module.IconGlyph, module.DisplayName, hint);
        item.Click += (_, _) =>
        {
            StartFlyout.Hide();
            OpenModule(module);
        };
        StartMenuPanel.Children.Add(item);
    }

    /// <summary>시작 메뉴의 New window 항목(A24) — Ctrl+N과 같은 동작.</summary>
    private void AddNewWindowItem()
    {
        var item = MakeMenuItem("\uE78B", "New window", "Ctrl+N"); // NewWindow 글리프
        item.Click += (_, _) =>
        {
            StartFlyout.Hide();
            _manager.OpenNewWindow(CurrentModuleId);
        };
        StartMenuPanel.Children.Add(item);
    }

    /// <summary>시작 메뉴의 Settings 항목 — 하단 바 우측 아이콘과 같은 동작.</summary>
    private void AddSettingsItem()
    {
        var item = MakeMenuItem("\uE713", "Settings", SettingsShortcutHint);
        item.Click += (_, _) =>
        {
            StartFlyout.Hide();
            OnSettingsClick(item, new RoutedEventArgs());
        };
        StartMenuPanel.Children.Add(item);
    }

    /// <summary>
    /// 시작 메뉴 항목. shortcutHint("1" 등)가 있으면 표준 툴팁으로 단다 —
    /// 다른 버튼들과 같은 지연(약 1초)·모양으로 표시된다(A1, v0.57.0 —
    /// v0.45.0의 즉시 인라인 힌트를 사용자 지시로 교체).
    /// </summary>
    private static Button MakeMenuItem(string glyph, string label, string? shortcutHint = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        content.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            // A31: 히트 영역 확대 — 상하 패딩 8→12 + 최소 높이 44 (터치 타깃 권장 크기).
            // 좌우는 10 유지: 메뉴 폭 124(v0.35.0 축소 확정) 안에서 라벨 말줄임을 늘리지 않기 위해.
            Padding = new Thickness(10, 12, 10, 12),
            MinHeight = 44,
        };
        if (shortcutHint is not null)
            ToolTipService.SetToolTip(button, shortcutHint);
        return button;
    }

    private Image? _sponsorImage;

    private UIElement BuildSponsorCard()
    {
        // v0.43.0(사용자 스샷 피드백): 광고가 카드 전 영역을 차지하고(패딩 제거, 메뉴 폭에 맞춰 확대),
        // SPONSOR 라벨은 이미지 위 좌상단에 반투명 배지로 겹쳐서 아주 작게 표시한다.
        var host = new Grid
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Padding = new Thickness(2),
            MinHeight = 60,
        };

        if (SponsorAds.Any)
        {
            // 광고 표시 규격 120×60 (v0.54.0 사용자 확대 지시 — 원본 2:1 비율 유지 확대)
            _sponsorImage = new Image
            {
                Width = 120,
                Height = 60,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            SponsorAds.Apply(_sponsorImage);
            host.Children.Add(_sponsorImage);
        }

        host.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x59, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock
            {
                Text = "SPONSOR",
                FontSize = 7,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Opacity = 0.9,
            },
        });

        return host;
    }

    /// <summary>메뉴가 열릴 때 현재 분 기준 광고로 갱신한다(로직은 SponsorAds 공용, v0.50.0).</summary>
    private void UpdateSponsorImage()
    {
        if (_sponsorImage is not null) SponsorAds.Apply(_sponsorImage);
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
        var settings = new SettingsView(_router);
        ModuleHost.Content = settings;
        // 설정도 하단 바 제공(광고 + ⛶, v0.50.0) — 모듈들과 같은 통합 방식
        ModuleBarHost.Content = settings.TakeBottomBar() as UIElement;
        CurrentModuleId = null;
        IsUntouched = false;
        UpdateModeIndicator(null, isSettings: true);
        SetContentState(null, null);
    }

    // ---------- 파일 열기 ----------

    /// <summary>
    /// 내장 탐색기·Alt 리스트의 일반 더블클릭 열기(A24): 재사용 규칙이 "항상 새 창"이면
    /// WindowManager로 넘겨 새 창에 열고, 아니면(기본) 이 창에서 그대로 연다.
    /// 외부 진입(WindowManager가 창을 이미 골라 OpenFile을 부르는 경로)과 섞이지 않게 별도 메서드.
    /// </summary>
    private void OpenFileRouted(string path)
    {
        if (_settings.Get(WindowManager.AlwaysNewWindowKey, false))
            _manager.OpenFileInNewWindow(path);
        else
            OpenFile(path);
    }

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
        RememberLastFolder(); // 모듈별 마지막 폴더 저장 (v0.55.0)
        UpdateEmptyExplorer();
        ResetKeyOverlays();
    }

    /// <summary>현재 파일의 폴더를 모듈별 설정("lastFolder.{id}")에 기억한다 (v0.55.0).</summary>
    private void RememberLastFolder()
    {
        if (_currentModule is null || _currentFilePath is null) return;
        if (Path.GetDirectoryName(_currentFilePath) is not { Length: > 0 } folder) return;
        if (_settings.Get($"lastFolder.{_currentModule.Id}", string.Empty) == folder) return;
        _settings.Set($"lastFolder.{_currentModule.Id}", folder);
        _settings.Save();
    }

    /// <summary>모듈 뷰가 파일을 열었다는 알림(IContentStateSource) — 탐색기를 내리고 기준 경로 갱신.</summary>
    private void OnContentOpened(string path)
    {
        _currentFilePath = path;
        _infoPath = null;
        _infoText = null;
        RememberLastFolder(); // v0.55.0
        UpdateEmptyExplorer();
        if (AltOverlayRoot.Visibility == Visibility.Visible) ShowAltOverlay(); // 폴더가 바뀌었을 수 있다
        if (InfoOverlay.Visibility == Visibility.Visible) UpdateInfoOverlay();
    }

    /// <summary>
    /// 빈 상태(파일 없이 연 압축/이미지/동영상/문서 모듈)면 중앙에 탐색기를 띄운다.
    /// 시작 위치는 그 모듈의 마지막 폴더(v0.55.0, 없으면 바탕화면), 파일은 담당 확장자만.
    /// Hardware/Settings에는 띄우지 않는다.
    /// </summary>
    private void UpdateEmptyExplorer()
    {
        if (_currentFilePath is null &&
            _currentModule is { Id: "archive" or "image" or "video" or "document" } module)
        {
            if (_emptyExplorer is null)
            {
                _emptyExplorer = new ExplorerPane();
                _emptyExplorer.FileActivated += OpenFileRouted;                       // 재사용 규칙 적용(A24)
                _emptyExplorer.FileActivatedNewWindow += _manager.OpenFileInNewWindow; // Shift+더블클릭·우클릭 메뉴
                ExplorerHost.Children.Add(_emptyExplorer);
            }
            var start = _settings.Get($"lastFolder.{module.Id}", string.Empty);
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            _emptyExplorer.NavigateTo(start, module.SupportedExtensions);
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

            // Ctrl 2연타 = 고정 토글, 다시 2연타로 해제 (A32 — Shift에서 회귀, UX 동일)
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
            _altList.FileActivated += OpenFileRouted;                       // 재사용 규칙 적용(A24)
            _altList.FileActivatedNewWindow += _manager.OpenFileInNewWindow; // Shift+더블클릭·우클릭 메뉴
            AltListHost.Content = _altList;
        }
        _altList.NavigateTo(folder, _currentModule.SupportedExtensions);
        AltOverlayRoot.Visibility = Visibility.Visible;
    }

    /// <summary>Ctrl 홀드(또는 고정) 정보 오버레이의 표시 상태를 갱신한다(A32에서 Shift→Ctrl 회귀).</summary>
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
            "document" => "app-document.ico", // 아직 미생성 — 아래 File.Exists로 중립 아이콘 대체

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

    // ---------- 인스턴스 번호 배지 (A2, v0.58.0) ----------

    /// <summary>배지 색 팔레트 — 번호(1~9)마다 다른 색. 창을 눈으로 구분하는 용도.</summary>
    private static readonly Windows.UI.Color[] InstanceColors =
    [
        Windows.UI.Color.FromArgb(255, 0xE8, 0x11, 0x23), // 1 red
        Windows.UI.Color.FromArgb(255, 0x00, 0x78, 0xD7), // 2 blue
        Windows.UI.Color.FromArgb(255, 0x10, 0x7C, 0x10), // 3 green
        Windows.UI.Color.FromArgb(255, 0xF7, 0x63, 0x0C), // 4 orange
        Windows.UI.Color.FromArgb(255, 0x8E, 0x24, 0xAA), // 5 purple
        Windows.UI.Color.FromArgb(255, 0x00, 0x99, 0xBC), // 6 teal
        Windows.UI.Color.FromArgb(255, 0xC3, 0x00, 0x52), // 7 magenta
        Windows.UI.Color.FromArgb(255, 0x76, 0x76, 0x76), // 8 gray
        Windows.UI.Color.FromArgb(255, 0x4A, 0x37, 0x8C), // 9 indigo
    ];

    /// <summary>
    /// 인스턴스 번호 표시. 0 = 숨김(창이 하나뿐이거나 10번째 이상).
    /// 창이 2개가 되는 순간 1번 창에도 배지가 생기고, 중간 창이 닫히면
    /// WindowManager가 번호를 당겨서 다시 부른다.
    /// </summary>
    public void SetInstanceBadge(int number)
    {
        if (number <= 0)
        {
            InstanceBadge.Visibility = Visibility.Collapsed;
            return;
        }
        InstanceBadge.Visibility = Visibility.Visible;
        InstanceBadge.Background =
            new SolidColorBrush(InstanceColors[(number - 1) % InstanceColors.Length]);
        InstanceBadgeText.Text = number.ToString();
    }
}
