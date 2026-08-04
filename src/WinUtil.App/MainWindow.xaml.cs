using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = module.DisplayName,
                Tag = module.Id,
            });
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
        ShowModule(module, OpenContext.ForFile(path));
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
            if (module is not null) ShowModule(module, OpenContext.Empty);
        }
        // TODO Phase 4: args.IsSettingsSelected → 설정 페이지
    }

    public void BringToFront()
    {
        AppWindow.MoveInZOrderAtTop();
        Activate();
    }
}
