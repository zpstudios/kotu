using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUtil.App.Integration;
using WinUtil.Core.Contracts;
using WinUtil.Core.Routing;

namespace WinUtil.App;

/// <summary>
/// 설정 페이지. 탐색기 통합(파일 연결·우클릭 메뉴)을 모듈별로 켜고 끈다.
/// 모든 등록은 현재 사용자(HKCU) 범위 — 관리자 권한 불필요, 해제 시 흔적 없음.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private readonly TextBlock _status = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private bool _suppressToggle;

    public SettingsView(FileTypeRouter router)
    {
        InitializeComponent();
        Build(router);
    }

    private void Build(FileTypeRouter router)
    {
        AddHeader("탐색기 통합");
        Root.Children.Add(new TextBlock
        {
            Text = "현재 사용자 계정에만 적용되며(관리자 권한 불필요), 끄면 등록이 완전히 제거됩니다. "
                 + "파일 연결은 \"연결 프로그램\" 목록에 후보로 추가하는 것이고, 기본 앱 지정은 Windows 설정에서 합니다.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var module in router.Modules)
        {
            var toggle = new ToggleSwitch
            {
                Header = $"{module.DisplayName} 파일 연결 등록  ({string.Join(" ", module.SupportedExtensions)})",
                IsOn = Safe(() => ExplorerIntegration.IsAssociationRegistered(module)),
            };
            toggle.Toggled += (_, _) => Apply(toggle,
                () => ExplorerIntegration.RegisterAssociation(module),
                () => ExplorerIntegration.UnregisterAssociation(module));
            Root.Children.Add(toggle);
        }

        var archiveExts = router.Modules.FirstOrDefault(m => m.Id == "archive")?.SupportedExtensions
            ?? (IReadOnlyList<string>)[];

        var extractToggle = new ToggleSwitch
        {
            Header = "압축 파일 우클릭 메뉴: \"WinUtil로 여기에 풀기\"",
            IsOn = Safe(() => ExplorerIntegration.IsExtractHereMenuRegistered(archiveExts)),
        };
        extractToggle.Toggled += (_, _) => Apply(extractToggle,
            () => ExplorerIntegration.RegisterExtractHereMenu(archiveExts),
            () => ExplorerIntegration.UnregisterExtractHereMenu(archiveExts));
        Root.Children.Add(extractToggle);

        var compressToggle = new ToggleSwitch
        {
            Header = "모든 파일 우클릭 메뉴: \"WinUtil로 압축\"",
            IsOn = Safe(ExplorerIntegration.IsCompressMenuRegistered),
        };
        compressToggle.Toggled += (_, _) => Apply(compressToggle,
            ExplorerIntegration.RegisterCompressMenu,
            ExplorerIntegration.UnregisterCompressMenu);
        Root.Children.Add(compressToggle);

        Root.Children.Add(new TextBlock
        {
            Text = "Windows 11에서는 우클릭 후 \"추가 옵션 표시\"(Shift+F10) 안에 나타납니다.",
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        Root.Children.Add(_status);

        AddHeader("정보");
        var version = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "?";
        Root.Children.Add(new TextBlock { Text = $"WinUtil v{version} · github.com/tsusaikang/winutil", Opacity = 0.7 });
    }

    private void AddHeader(string text) => Root.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 20,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 0),
    });

    /// <summary>토글 적용. 실패하면 토글을 원위치로 되돌리고 이유를 표시한다.</summary>
    private void Apply(ToggleSwitch toggle, Action register, Action unregister)
    {
        if (_suppressToggle) return;
        try
        {
            if (toggle.IsOn) register();
            else unregister();
            _status.Text = string.Empty;
        }
        catch (Exception ex)
        {
            _suppressToggle = true;
            toggle.IsOn = !toggle.IsOn;
            _suppressToggle = false;
            _status.Text = "적용 실패: " + ex.Message;
        }
    }

    private static bool Safe(Func<bool> check)
    {
        try { return check(); }
        catch { return false; }
    }
}
