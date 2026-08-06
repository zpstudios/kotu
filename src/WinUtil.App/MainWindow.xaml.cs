using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinUtil.Core.Cli;
using WinUtil.Core.Contracts;
using WinUtil.Core.Routing;

namespace WinUtil.App;

public sealed partial class MainWindow : Window
{
    private static readonly string IconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

    private readonly FileTypeRouter _router;
    private readonly WindowManager _manager;
    private readonly TrayIcon _tray;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings;
    private bool _suppressNavSelection;

    /// <summary>지금 보여주는 모듈 ID. 빈 셸·설정·미지원 파일 안내면 null. 창 재사용 판단에 쓴다.</summary>
    public string? CurrentModuleId { get; private set; }

    /// <summary>아직 아무 콘텐츠도 안 연 빈 셸인지. 창 재사용 판단에 쓴다.</summary>
    public bool IsUntouched { get; private set; } = true;

    public MainWindow(WindowManager manager)
    {
        InitializeComponent();
        Title = "WinUtil";
        _manager = manager;
        _router = App.Services.GetRequiredService<FileTypeRouter>();
        BuildNavigation();

        // 타이틀바·작업표시줄 아이콘 (unpackaged는 exe 아이콘만으로는 타이틀바가 비어 보인다)
        if (File.Exists(IconPath)) AppWindow.SetIcon(IconPath);

        // 창 헤더를 시스템 강조색으로. 실행 중 사용자가 테마 컬러를 바꾸면 따라간다
        TitleBarTheming.ApplyAccent(AppWindow.TitleBar);
        _uiSettings = new Windows.UI.ViewManagement.UISettings();
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged; // 백그라운드 스레드로 옴
        Closed += (_, _) => _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;

        // 창별 트레이 미니 아이콘: 좌클릭=활성화, 우클릭=메뉴, 툴팁=창 제목
        _tray = new TrayIcon(File.Exists(IconPath) ? IconPath : null);
        _tray.ActivateRequested += BringToFront;
        _tray.CloseRequested += Close;
        _tray.ExitAllRequested += _manager.CloseAll;
        Closed += (_, _) => _tray.Dispose();
    }

    private void OnSystemColorsChanged(Windows.UI.ViewManagement.UISettings sender, object args)
        => DispatcherQueue.TryEnqueue(() => TitleBarTheming.ApplyAccent(AppWindow.TitleBar));

    /// <summary>창 제목과 트레이 툴팁을 함께 갱신한다.</summary>
    private void SetTitle(string title)
    {
        Title = title;
        _tray.SetTooltip(title);
    }

    /// <summary>등록된 모듈들로 네비게이션 메뉴를 구성한다.</summary>
    private void BuildNavigation()
    {
        foreach (var module in _router.Modules)
        {
            var item = new NavigationViewItem
            {
                Content = module.DisplayName,
                Tag = module.Id,
                // 접힌 네비게이션에서는 아이콘만 보이므로 아이콘이 없으면 텍스트가 잘려 보인다.
                Icon = new FontIcon { Glyph = module.IconGlyph },
            };
            ToolTipService.SetToolTip(item, module.DisplayName);
            Nav.MenuItems.Add(item);
        }
    }

    /// <summary>파일 라우팅의 종착점: 확장자로 모듈을 찾아 뷰를 띄운다.</summary>
    public void OpenFile(string path)
    {
        var module = _router.Resolve(path);
        if (module is null)
        {
            ModuleHost.Content = new TextBlock
            {
                Text = $"지원하지 않는 파일 형식입니다: {Path.GetFileName(path)}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            CurrentModuleId = null;
            IsUntouched = false;
            return;
        }
        SetTitle(Path.GetFileName(path) + " — WinUtil");
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

        SetTitle(Path.GetFileName(file) + " — WinUtil");
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
        // 중요: SelectedItem 대입이 SelectionChanged를 동기 재진입시키므로,
        // 억제 플래그 없이는 방금 만든 파일 컨텍스트 뷰가 빈 뷰로 즉시 덮여버린다(v0.5.8 실기기 버그).
        _suppressNavSelection = true;
        Nav.SelectedItem = Nav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == module.Id);
        _suppressNavSelection = false;

        ModuleHost.Content = (UIElement)module.CreateView(context);
        CurrentModuleId = module.Id;
        IsUntouched = false;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavSelection) return;

        if (args.IsSettingsSelected)
        {
            SetTitle("설정 — WinUtil");
            ModuleHost.Content = new SettingsView(_router);
            CurrentModuleId = null;
            IsUntouched = false;
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string id })
        {
            var module = _router.Modules.FirstOrDefault(m => m.Id == id);
            if (module is not null)
            {
                SetTitle($"{module.DisplayName} — WinUtil");
                ShowModule(module, OpenContext.Empty);
            }
        }
    }

    public void BringToFront()
    {
        AppWindow.MoveInZOrderAtTop();
        Activate();
    }
}
