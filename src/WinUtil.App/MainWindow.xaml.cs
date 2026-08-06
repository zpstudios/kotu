using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinUtil.Core.Cli;
using WinUtil.Core.Contracts;
using WinUtil.Core.Routing;

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
        BuildStartMenu();

        // 타이틀바·작업표시줄 아이콘 (unpackaged는 exe 아이콘만으로는 타이틀바가 비어 보인다)
        if (File.Exists(IconPath)) AppWindow.SetIcon(IconPath);

        // 창 헤더만 브랜드 색(#15072E) — 본문은 시스템 테마 기본값
        TitleBarTheming.Apply(AppWindow.TitleBar);

        // 창별 트레이 미니 아이콘: 좌클릭=활성화, 우클릭=메뉴, 툴팁=창 제목
        _tray = new TrayIcon(File.Exists(IconPath) ? IconPath : null);
        _tray.ActivateRequested += BringToFront;
        _tray.CloseRequested += Close;
        _tray.ExitAllRequested += _manager.CloseAll;
        Closed += (_, _) => _tray.Dispose();
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
        StartMenuPanel.Children.Add(Spacer(16)); // 여백 x2

        AddModuleItem("archive");
        StartMenuPanel.Children.Add(Spacer(8)); // 여백

        // 사진-영상-문서 그룹 (아래부터 사진 → 위로 갈수록 문서)
        AddDocumentPlaceholder();
        AddModuleItem("video");
        AddModuleItem("image");

        // 하단 바 우측 Info 아이콘: 하드웨어 모듈 글리프 재사용
        var hardware = _router.Modules.FirstOrDefault(m => m.Id == "hardware");
        if (hardware is not null)
        {
            InfoButton.Content = new FontIcon { Glyph = hardware.IconGlyph, FontSize = 16 };
            ToolTipService.SetToolTip(InfoButton, hardware.DisplayName);
        }
        else
        {
            InfoButton.Visibility = Visibility.Collapsed;
        }
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

    /// <summary>문서 모듈 자리(마크다운·PDF·HWP 등 예정) — 메뉴 배치를 먼저 확정해 둔다.</summary>
    private void AddDocumentPlaceholder()
    {
        var item = MakeMenuItem("", "Document");
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
            panel.Children.Add(new Image
            {
                Height = 44,
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

    private static Border Spacer(double height) => new() { Height = height };

    private void OpenModule(IModule module)
    {
        SetTitle($"{module.DisplayName} — ZP");
        ShowModule(module, OpenContext.Empty);
    }

    private void OnInfoClick(object sender, RoutedEventArgs e)
    {
        var module = _router.Modules.FirstOrDefault(m => m.Id == "hardware");
        if (module is not null) OpenModule(module);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SetTitle("Settings — ZP");
        ModuleHost.Content = new SettingsView(_router);
        CurrentModuleId = null;
        IsUntouched = false;
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
            CurrentModuleId = null;
            IsUntouched = false;
            return;
        }
        SetTitle(Path.GetFileName(path) + " — ZP");
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

        SetTitle(Path.GetFileName(file) + " — ZP");
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
        ModuleHost.Content = (UIElement)module.CreateView(context);
        CurrentModuleId = module.Id;
        IsUntouched = false;
    }

    public void BringToFront()
    {
        AppWindow.MoveInZOrderAtTop();
        Activate();
    }
}
