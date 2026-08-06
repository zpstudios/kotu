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
                 + "File association adds ZP to the \"Open with\" candidates; picking the default app is done in Windows Settings.",
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
            Header = "Archive right-click menu: \"Extract here with ZP-zip\"",
            IsOn = Safe(() => ExplorerIntegration.IsExtractHereMenuRegistered(archiveExts)),
        };
        extractToggle.Toggled += (_, _) => Apply(extractToggle,
            () => ExplorerIntegration.RegisterExtractHereMenu(archiveExts),
            () => ExplorerIntegration.UnregisterExtractHereMenu(archiveExts));
        Root.Children.Add(extractToggle);

        var compressToggle = new ToggleSwitch
        {
            Header = "All-files right-click menu: \"Compress with ZP-zip\"",
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
        var currentVersion = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "?";
        Root.Children.Add(new TextBlock { Text = $"Current version: v{currentVersion}", Opacity = 0.8 });

        var updateStatus = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var updateButton = new Button { Content = "Update", Visibility = Visibility.Collapsed };
        Root.Children.Add(updateStatus);
        Root.Children.Add(updateButton);

        // 주기 체크 금지(사용자 결정) — 설정 화면에 들어올 때만 한 번 확인한다
        _ = CheckForUpdatesAsync(updateStatus, updateButton);

        AddHeader("About");
        Root.Children.Add(new TextBlock { Text = $"ZP v{currentVersion} · github.com/tsusaikang/winutil", Opacity = 0.7 });
        Root.Children.Add(new TextBlock
        {
            Text = "Mission Statement",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });
        Root.Children.Add(new TextBlock
        {
            Text = Branding.MissionStatement,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    /// <summary>설정 진입 시 1회 업데이트 확인: 새 버전이 있을 때만 Update 버튼을 보여준다.</summary>
    private async Task CheckForUpdatesAsync(TextBlock status, Button updateButton)
    {
        if (!UpdateService.IsUpdatableBuild)
        {
            status.Text = "Automatic updates are unavailable in this build. "
                        + "Install with Setup.exe from Releases to enable them.";
            return;
        }

        status.Text = "Checking for updates...";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                status.Text = "You are on the latest version.";
                return;
            }

            var newVersion = info.TargetFullRelease.Version;
            status.Text = $"New version v{newVersion} is available.";
            updateButton.Content = $"Update to v{newVersion}";
            updateButton.Visibility = Visibility.Visible;
            updateButton.Click += async (_, _) => await DownloadAndInstallAsync(status, updateButton, info);
        }
        catch (Exception ex)
        {
            status.Text = "Update check failed: " + ex.Message;
        }
    }

    /// <summary>다운로드 → 사람 확인(Install and restart / Later) 대기 → 적용. 자동 재시작 없음.</summary>
    private async Task DownloadAndInstallAsync(TextBlock status, Button updateButton, Velopack.UpdateInfo info)
    {
        var version = info.TargetFullRelease.Version;
        updateButton.IsEnabled = false;
        try
        {
            await UpdateService.DownloadAsync(info, percent =>
                DispatcherQueue.TryEnqueue(() => status.Text = $"Downloading v{version}... {percent}%"));
            status.Text = $"v{version} downloaded.";

            var confirm = new ContentDialog
            {
                Title = "Ready to install",
                Content = $"ZP will close and restart to finish installing v{version}. Install now?",
                PrimaryButtonText = "Install and restart",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                status.Text = "Applying and restarting...";
                UpdateService.ApplyAndRestart(info);
            }
            else
            {
                status.Text = $"v{version} downloaded — click the button again to install when ready.";
                updateButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            status.Text = "Update failed: " + ex.Message;
            updateButton.IsEnabled = true;
        }
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
