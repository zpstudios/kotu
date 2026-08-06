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
        AddHeader("Explorer integration");
        Root.Children.Add(new TextBlock
        {
            Text = "Applies to the current user account only (no admin rights needed); turning a switch off removes the registration completely. "
                 + "File association adds zp to the \"Open with\" candidates; picking the default app is done in Windows Settings.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var module in router.Modules)
        {
            var toggle = new ToggleSwitch
            {
                Header = $"Register {module.BrandName} file associations  ({string.Join(" ", module.SupportedExtensions)})",
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
            Header = "Archive right-click menu: \"Extract here with zp-zip\"",
            IsOn = Safe(() => ExplorerIntegration.IsExtractHereMenuRegistered(archiveExts)),
        };
        extractToggle.Toggled += (_, _) => Apply(extractToggle,
            () => ExplorerIntegration.RegisterExtractHereMenu(archiveExts),
            () => ExplorerIntegration.UnregisterExtractHereMenu(archiveExts));
        Root.Children.Add(extractToggle);

        var compressToggle = new ToggleSwitch
        {
            Header = "All-files right-click menu: \"Compress with zp-zip\"",
            IsOn = Safe(ExplorerIntegration.IsCompressMenuRegistered),
        };
        compressToggle.Toggled += (_, _) => Apply(compressToggle,
            ExplorerIntegration.RegisterCompressMenu,
            ExplorerIntegration.UnregisterCompressMenu);
        Root.Children.Add(compressToggle);

        Root.Children.Add(new TextBlock
        {
            Text = "On Windows 11 these appear under \"Show more options\" (Shift+F10).",
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        Root.Children.Add(_status);

        AddHeader("Updates");
        var updateStatus = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var checkButton = new Button { Content = "Check for updates" };
        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            updateStatus.Text = "Checking...";
            try
            {
                if (!UpdateService.IsUpdatableBuild)
                {
                    updateStatus.Text = "Automatic updates are unavailable in the portable zip. "
                                      + "Install with Setup.exe from Releases to enable them.";
                    return;
                }
                var info = await UpdateService.CheckAsync();
                if (info is null)
                {
                    updateStatus.Text = "You are on the latest version.";
                    return;
                }
                await UpdateService.DownloadAsync(info, percent =>
                    DispatcherQueue.TryEnqueue(() =>
                        updateStatus.Text = $"Downloading v{info.TargetFullRelease.Version}... {percent}%"));
                updateStatus.Text = "Applying and restarting...";
                UpdateService.ApplyAndRestart(info);
            }
            catch (Exception ex)
            {
                updateStatus.Text = "Update check failed: " + ex.Message;
            }
            finally
            {
                checkButton.IsEnabled = true;
            }
        };
        Root.Children.Add(checkButton);
        Root.Children.Add(updateStatus);

        AddHeader("About");
        var version = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "?";
        Root.Children.Add(new TextBlock { Text = $"zp v{version} · github.com/tsusaikang/winutil", Opacity = 0.7 });
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
            _status.Text = "Failed to apply: " + ex.Message;
        }
    }

    private static bool Safe(Func<bool> check)
    {
        try { return check(); }
        catch { return false; }
    }
}
