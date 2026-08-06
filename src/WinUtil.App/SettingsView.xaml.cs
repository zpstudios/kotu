using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUtil.App.Integration;
using WinUtil.Core.Contracts;
using WinUtil.Core.Routing;
using WinUtil.Core.Settings;

namespace WinUtil.App;

/// <summary>
/// 설정 페이지. UI 스케일(v0.24.0), 탐색기 통합(파일 연결·우클릭 메뉴)을 관리한다.
/// 탐색기 등록은 현재 사용자(HKCU) 범위 — 관리자 권한 불필요, 해제 시 흔적 없음.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private readonly TextBlock _status = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly ISettingsService _settings;
    private bool _suppressToggle;

    public SettingsView(FileTypeRouter router)
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        Build(router);
    }

    private void Build(FileTypeRouter router)
    {
        BuildDisplaySection();

        AddHeader("Explorer integration");
        Root.Children.Add(new TextBlock
        {
            Text = "Applies to the current user account only (no admin rights needed); turning a switch off removes the registration completely. "
                 + "File association adds ZP to the \"Open with\" candidates; picking the default app is done in Windows Settings.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        // 토글 순서: 압축 → 문서 → 영상 → 이미지 (사용자 지정, v0.28.0 — 문서 모듈 v0.44.0 합류).
        // 파일을 다루지 않는 모듈(hardware)은 연결할 확장자가 없으므로 토글을 만들지 않는다.
        string[] associationOrder = ["archive", "document", "video", "image"];
        var associationModules = router.Modules
            .Where(m => m.SupportedExtensions.Count > 0)
            .OrderBy(m =>
            {
                var i = Array.IndexOf(associationOrder, m.Id);
                return i < 0 ? int.MaxValue : i;
            });

        foreach (var module in associationModules)
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

        // 우클릭 메뉴 토글 통합(v0.30.0 사용자 요청): "여기에 풀기"(압축 파일)와
        // "압축하기"(모든 파일)를 하나의 스위치로 함께 등록/해제한다.
        var menuToggle = new ToggleSwitch
        {
            Header = "Explorer right-click menu: \"Extract here with ZP-zip\" (archives) · \"Compress with ZP-zip\" (all files)",
            IsOn = Safe(() => ExplorerIntegration.IsExtractHereMenuRegistered(archiveExts)
                           || ExplorerIntegration.IsCompressMenuRegistered()),
        };
        menuToggle.Toggled += (_, _) => Apply(menuToggle,
            () =>
            {
                ExplorerIntegration.RegisterExtractHereMenu(archiveExts);
                ExplorerIntegration.RegisterCompressMenu();
            },
            () =>
            {
                ExplorerIntegration.UnregisterExtractHereMenu(archiveExts);
                ExplorerIntegration.UnregisterCompressMenu();
            });
        Root.Children.Add(menuToggle);

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
        var updateCountdown = new TextBlock { Opacity = 0.55, FontSize = 12 };
        var updateButton = new Button { Content = "Update", Visibility = Visibility.Collapsed };
        Root.Children.Add(updateStatus);
        Root.Children.Add(updateCountdown);
        Root.Children.Add(updateButton);

        // v0.27.0(사용자 요청): 설정 화면에 머무는 동안에만 1분 간격 재확인 + 다음 체크 카운트다운.
        // 화면 밖 백그라운드 주기 체크는 여전히 하지 않는다(기존 정책 유지).
        StartUpdateLoop(currentVersion, updateStatus, updateCountdown, updateButton);

        AddHeader("About");
        Root.Children.Add(new TextBlock { Text = $"ZP v{currentVersion} · github.com/zpstudios/zpro", Opacity = 0.7 });
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

        // Patreon 후원 안내 (v0.46.0 사용자 지정 문구·표시 URL)
        Root.Children.Add(new TextBlock
        {
            Text = "Kindly support us on Patreon !",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
        Root.Children.Add(new HyperlinkButton
        {
            Content = "www.patreon.com/zpstudios/",
            NavigateUri = new Uri("https://www.patreon.com/zpdevelop/"),
            Padding = new Thickness(0),
        });
    }

    /// <summary>
    /// Display 섹션: 앱 UI 스케일. 옵션은 윈도우 디스플레이 설정과 같은 배율 목록(UiScale.Percents),
    /// 바꾸면 저장 후 열린 모든 창에 즉시 적용된다(UiScale.Changed → MainWindow.ApplyUiScale).
    /// </summary>
    private void BuildDisplaySection()
    {
        AddHeader("Display");
        Root.Children.Add(new TextBlock
        {
            Text = "Scale of the ZP interface. \"System default\" follows the Windows display scaling; "
                 + "picking a value overrides it for this app only, applied to all open windows immediately.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var scaleBox = new ComboBox { Header = "UI scale", MinWidth = 200 };
        scaleBox.Items.Add("System default");
        foreach (var p in UiScale.Percents)
            scaleBox.Items.Add($"{p}%");

        var current = _settings.Get(UiScale.SettingKey, 0);
        var index = Array.IndexOf(UiScale.Percents, current);
        scaleBox.SelectedIndex = current <= 0 || index < 0 ? 0 : index + 1;

        scaleBox.SelectionChanged += (_, _) =>
        {
            var value = scaleBox.SelectedIndex <= 0 ? 0 : UiScale.Percents[scaleBox.SelectedIndex - 1];
            if (value == _settings.Get(UiScale.SettingKey, 0)) return;
            _settings.Set(UiScale.SettingKey, value);
            _settings.Save();
            UiScale.NotifyChanged();
        };
        Root.Children.Add(scaleBox);
    }

    private DispatcherTimer? _updateTimer;
    private int _nextCheckSeconds;
    private bool _updateChecking;

    /// <summary>
    /// 설정 화면 체류 중 업데이트 루프(v0.27.0): 진입 즉시 1회 확인 후 60초마다 재확인.
    /// 1초 틱 타이머로 다음 체크까지 카운트다운을 보여주고, Unloaded(화면 이탈)에서 멈춘다.
    /// 새 버전을 찾으면 더 확인할 게 없으므로 루프를 중단한다.
    /// </summary>
    private void StartUpdateLoop(string currentVersion, TextBlock status, TextBlock countdown, Button updateButton)
    {
        if (!UpdateService.IsUpdatableBuild)
        {
            status.Text = "Automatic updates are unavailable in this build. "
                        + "Install with Setup.exe from Releases to enable them.";
            return;
        }

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _updateTimer.Tick += async (_, _) =>
        {
            if (_updateChecking) return;
            _nextCheckSeconds--;
            if (_nextCheckSeconds <= 0)
                await CheckOnceAsync(currentVersion, status, countdown, updateButton);
            else
                countdown.Text = $"Next check in {_nextCheckSeconds}s";
        };
        Unloaded += (_, _) => _updateTimer?.Stop();

        _ = CheckOnceAsync(currentVersion, status, countdown, updateButton);
        _updateTimer.Start();
    }

    /// <summary>1회 확인. 새 버전이 있으면 현재/새 버전을 함께 표시하고 루프를 멈춘다.</summary>
    private async Task CheckOnceAsync(string currentVersion, TextBlock status, TextBlock countdown, Button updateButton)
    {
        _updateChecking = true;
        status.Text = "Checking for updates...";
        countdown.Text = string.Empty;
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is not null)
            {
                var newVersion = info.TargetFullRelease.Version;
                status.Text = $"New version v{newVersion} is available (current: v{currentVersion}).";
                updateButton.Content = $"Update to v{newVersion}";
                if (updateButton.Visibility != Visibility.Visible)
                {
                    updateButton.Visibility = Visibility.Visible;
                    updateButton.Click += async (_, _) => await DownloadAndInstallAsync(status, updateButton, info);
                }
                _updateTimer?.Stop(); // 찾았으면 주기 확인 종료
                _updateChecking = false;
                return;
            }
            status.Text = $"You are on the latest version (v{currentVersion}).";
        }
        catch (Exception ex)
        {
            status.Text = "Update check failed: " + ex.Message;
        }
        _nextCheckSeconds = 60;
        countdown.Text = "Next check in 60s";
        _updateChecking = false;
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
