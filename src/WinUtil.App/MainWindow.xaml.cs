using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinUtil.Core.Contracts;
using WinUtil.Core.Routing;

namespace WinUtil.App;

public sealed partial class MainWindow : Window
{
    private readonly FileTypeRouter _router;
    private bool _suppressNavSelection;

    public MainWindow()
    {
        InitializeComponent();
        Title = "WinUtil";
        _router = App.Services.GetRequiredService<FileTypeRouter>();
        BuildNavigation();
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
            return;
        }
        Title = Path.GetFileName(path) + " — WinUtil";
        ShowModule(module, OpenContext.ForFile(path));
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
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavSelection) return;

        if (args.SelectedItem is NavigationViewItem { Tag: string id })
        {
            var module = _router.Modules.FirstOrDefault(m => m.Id == id);
            if (module is not null)
            {
                Title = "WinUtil";
                ShowModule(module, OpenContext.Empty);
            }
        }
        // TODO Phase 4: args.IsSettingsSelected → 설정 페이지
    }

    public void BringToFront()
    {
        AppWindow.MoveInZOrderAtTop();
        Activate();
    }
}
