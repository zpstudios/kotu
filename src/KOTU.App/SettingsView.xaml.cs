using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KOTU.App.Integration;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;
using KOTU.Core.Settings;

namespace KOTU.App;

/// <summary>
/// 설정 페이지. UI 스케일(v0.24.0), 탐색기 통합(파일 연결·우클릭 메뉴)을 관리한다.
/// 탐색기 등록은 현재 사용자(HKCU) 범위 — 관리자 권한 불필요, 해제 시 흔적 없음.
/// 하단 바(광고 + ⛶ 전체화면)는 셸이 TakeBottomBar()로 가져간다(v0.50.0).
/// Updates 섹션은 전역 <see cref="UpdateCoordinator"/>의 상태를 표시·조작만 한다
/// (A26·A76, v0.105.0 — 주기 확인·토스트는 앱 전역 서비스가 소유).
/// </summary>
public sealed partial class SettingsView : UserControl, IBottomBarProvider
{
    private readonly TextBlock _status = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly ISettingsService _settings;
    private bool _suppressToggle;

    public SettingsView(FileTypeRouter router)
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        Build(router);
        Loaded += (_, _) => Focus(FocusState.Programmatic); // F11/Esc 액셀러레이터가 바로 듣게
    }

    /// <summary>하단 바(광고·⛶)를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(v0.50.0).</summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(ControlBar);
        return ControlBar;
    }

    // ---------- 전체화면 (전 모듈 공통 패턴, v0.50.0) ----------

    private void ToggleFullScreen()
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;

        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(environment.AppWindowId);
        appWindow.SetPresenter(
            appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.Default
                : Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }

    private void OnFullScreenButtonClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnFullScreenInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullScreen();
    }

    private void OnEscapeInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(environment.AppWindowId);
        if (appWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen) return;

        args.Handled = true;
        appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
    }

    private void Build(FileTypeRouter router)
    {
        BuildDisplaySection();
        BuildWindowsSection(); // 창 재사용 규칙 (A24)

        AddHeader("Explorer integration");
        Root.Children.Add(new TextBlock
        {
            Text = "Applies to the current user account only (no admin rights needed); turning a switch off removes the registration completely. "
                 + $"Turning a switch on also makes {Branding.AppName} the default app for those file types automatically. "
                 + "Windows may block this for a few protected types — those open the Windows default-apps page so you can confirm once, "
                 + "or use \"Set default...\" per extension. (A38)",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        // 토글 순서(A35, 사용자 확정 2026-08-10): 이미지 → 비디오 → 오디오 → 문서 → 압축.
        // 시작 메뉴 번호 순서(1이미지 2영상 3오디오 4문서 5압축)와 일치시킨 것 —
        // v0.28.0의 "압축→문서→영상→이미지"를 대체한다.
        // 파일을 다루지 않는 모듈(hardware)은 연결할 확장자가 없으므로 토글을 만들지 않는다.
        string[] associationOrder = ["image", "video", "audio", "document", "archive"];
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
            Root.Children.Add(toggle);

            // A25(v0.61.0): 현재 기본 앱 현황(n/m) + 확장자별 '연결 프로그램' 대화상자 진입
            var defaultsText = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            };

            void RefreshDefaults()
            {
                int count;
                try { count = ExplorerIntegration.CountDefaults(module); }
                catch { count = 0; }
                defaultsText.Text = $"Default app for {count}/{module.SupportedExtensions.Count} extensions";
            }
            RefreshDefaults();

            var setDefaultButton = new DropDownButton
            {
                Content = "Set default...",
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 2),
            };
            var flyout = new MenuFlyout();
            foreach (var ext in module.SupportedExtensions)
            {
                var item = new MenuFlyoutItem { Text = ext };
                item.Click += (_, _) =>
                {
                    ExplorerIntegration.ShowSetDefaultDialog(GetHwnd(), ext);
                    RefreshDefaults(); // 대화상자에서 고르면 즉시 반영된다
                };
                flyout.Items.Add(item);
            }
            setDefaultButton.Flyout = flyout;

            var defaultsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, -8, 0, 0),
            };
            defaultsRow.Children.Add(defaultsText);
            defaultsRow.Children.Add(setDefaultButton);
            Root.Children.Add(defaultsRow);

            toggle.Toggled += (_, _) =>
            {
                if (_suppressToggle) return;
                var turnedOn = toggle.IsOn;
                Apply(toggle,
                    () => ExplorerIntegration.RegisterAssociation(module),
                    () => ExplorerIntegration.UnregisterAssociation(module));

                // 켤 때만: A38 — 기본 앱까지 자동 지정 시도. Apply가 실패로 토글을 되돌렸으면 건너뛴다.
                if (turnedOn && toggle.IsOn)
                {
                    IReadOnlyList<string> failed;
                    try { failed = ExplorerIntegration.SetAsDefault(module); }
                    catch { failed = module.SupportedExtensions; }

                    RefreshDefaults();

                    var total = module.SupportedExtensions.Count;
                    if (failed.Count == 0)
                    {
                        _status.Text = $"{module.BrandName}: set as the default app for all {total} file types.";
                    }
                    else
                    {
                        // 실패 확장자는 A25 폴백 — 설정 딥링크를 한 번 열어 사용자가 확정하게 한다
                        // (확장자별 대화상자는 "Set default..." 버튼으로 여전히 가능).
                        _status.Text = $"{module.BrandName}: set {total - failed.Count}/{total} automatically. "
                                     + $"Windows blocks the rest ({string.Join(" ", failed)}) — confirm them on the "
                                     + "page that just opened, or use \"Set default...\".";
                        ExplorerIntegration.OpenDefaultAppsSettings();
                    }
                }
                else
                {
                    RefreshDefaults();
                }
            };
        }

        var archiveModule = router.Modules.FirstOrDefault(m => m.Id == "archive");
        var archiveExts = archiveModule?.SupportedExtensions ?? (IReadOnlyList<string>)[];
        // 우클릭 메뉴 라벨은 모듈 BrandName을 따른다(A52로 KOTU-zip → KOTU-archive).
        var archiveBrand = archiveModule?.BrandName ?? Branding.AppName;

        // 우클릭 메뉴 토글 통합(v0.30.0 사용자 요청): "여기에 풀기"(압축 파일)와
        // "압축하기"(모든 파일)를 하나의 스위치로 함께 등록/해제한다.
        var menuToggle = new ToggleSwitch
        {
            Header = $"Explorer right-click menu: \"Extract here with {archiveBrand}\" (archives) · \"Compress with {archiveBrand}\" (all files)",
            IsOn = Safe(() => ExplorerIntegration.IsExtractHereMenuRegistered(archiveExts)
                           || ExplorerIntegration.IsCompressMenuRegistered()),
        };
        menuToggle.Toggled += (_, _) => Apply(menuToggle,
            () =>
            {
                ExplorerIntegration.RegisterExtractHereMenu(archiveExts, archiveBrand);
                ExplorerIntegration.RegisterCompressMenu(archiveBrand);
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

        _updatesHeader = AddHeader("Updates");
        var currentVersion = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "?";
        Root.Children.Add(new TextBlock { Text = $"Current version: v{currentVersion}", Opacity = 0.8 });
        BuildUpdatesSection(currentVersion);

        AddHeader("About");
        // 저장소 주소는 클릭해서 이동 가능 (v0.52.0 사용자 요청)
        var aboutLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        aboutLine.Children.Add(new TextBlock
        {
            Text = $"KOTU v{currentVersion} ·",
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        aboutLine.Children.Add(new HyperlinkButton
        {
            Content = "github.com/zpstudios/kotu",
            NavigateUri = new Uri("https://github.com/zpstudios/kotu"),
            Padding = new Thickness(0),
        });
        Root.Children.Add(aboutLine);
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
        // Patreon 후원 문구는 About 본문이 아니라 하단 바에 표시한다 (v0.52.0 사용자 정정).
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
            Text = $"Scale of the {Branding.AppName} interface. \"System default\" follows the Windows display scaling; "
                 + "picking a value overrides it for this app only, applied to all open windows immediately.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        // A44(A21 보강): 현재 윈도우 배율을 별도 줄이 아니라 배율 목록 항목 옆에 표기한다.
        // XamlRoot.RasterizationScale = 이 창이 떠 있는 모니터의 시스템 배율(앱 자체 배율과 무관).
        // 항목을 ComboBoxItem으로 만들어 Content만 갱신 — 선택 상태를 건드리지 않고 라이브 갱신 가능.
        // 생성 시점엔 XamlRoot가 없으므로 Loaded에서 채우고, 모니터 이동/배율 변경(Changed)에 추종.
        var scaleBox = new ComboBox { Header = "UI scale", MinWidth = 200 };
        scaleBox.Items.Add(new ComboBoxItem { Content = "System default" });
        foreach (var p in UiScale.Percents)
            scaleBox.Items.Add(new ComboBoxItem { Content = $"{p}%", Tag = p });

        // 윈도우 배율이 특이값(예: 커스텀 110%)이라 목록에 일치 항목이 없을 때만 보이는 안내 줄.
        var offListNote = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        void UpdateWindowsScaleMark()
        {
            if (XamlRoot is not { } xr) return;
            var winPercent = (int)Math.Round(xr.RasterizationScale * 100);
            var matched = false;
            foreach (var item in scaleBox.Items)
            {
                if (item is not ComboBoxItem { Tag: int p } cbi) continue;
                var text = p == winPercent ? $"{p}% (current Windows setting)" : $"{p}%";
                if (!Equals(cbi.Content as string, text)) cbi.Content = text;
                matched |= p == winPercent;
            }
            offListNote.Text = matched
                ? string.Empty
                : $"Current Windows display scaling on this monitor is {winPercent}%, which is not in the list above.";
            offListNote.Visibility = matched ? Visibility.Collapsed : Visibility.Visible;
        }
        Loaded += (_, _) =>
        {
            UpdateWindowsScaleMark();
            if (XamlRoot is { } xr) xr.Changed += (_, _) => UpdateWindowsScaleMark();
        };

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
        Root.Children.Add(offListNote);
    }

    /// <summary>
    /// Windows 섹션(A24): 창 재사용 규칙. 기본 off = 같은 모듈 창 재사용(현행).
    /// on = 파일을 열 때마다 새 창(내장 탐색기·외부 열기 공통). Shift+N(A84)·Shift+더블클릭·
    /// 우클릭 "Open in new instance"는 규칙과 무관하게 항상 새 창이므로 여기 영향 없음.
    /// 문구는 A53에서 "new window" → "new instance"로 통일.
    /// </summary>
    private void BuildWindowsSection()
    {
        AddHeader("Windows");
        var toggle = new ToggleSwitch
        {
            Header = "Always open files in a new instance",
            IsOn = _settings.Get(WindowManager.AlwaysNewWindowKey, false),
        };
        toggle.Toggled += (_, _) =>
        {
            _settings.Set(WindowManager.AlwaysNewWindowKey, toggle.IsOn);
            _settings.Save();
        };
        Root.Children.Add(toggle);
        Root.Children.Add(new TextBlock
        {
            Text = "Off: a file opens in the existing instance of the same module (default). "
                 + "On: every file opens a new instance. Explicit \"new instance\" actions "
                 + "(Shift+N, Shift+double-click, right-click menu) always open a new instance either way.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    /// <summary>업데이트 섹션 머리글 — 토스트 클릭 진입 시 스크롤 목표(A26, v0.105.0).</summary>
    private TextBlock? _updatesHeader;

    /// <summary>
    /// 토스트 클릭으로 열린 설정 화면을 업데이트 섹션까지 스크롤한다(A26, v0.105.0).
    /// 호출 시점에 아직 레이아웃 전일 수 있어 Loaded와 낮은 우선순위 큐 양쪽에서 시도한다.
    /// </summary>
    public void ScrollToUpdates()
    {
        if (_updatesHeader is not { } target) return;

        void OnLoadedOnce(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoadedOnce;
            target.StartBringIntoView();
        }
        Loaded += OnLoadedOnce;

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => target.StartBringIntoView());
    }

    /// <summary>
    /// Updates 섹션(A26·A76, v0.105.0). 실제 확인은 전역 <see cref="UpdateCoordinator"/>가 소유하고
    /// 여기서는 그 상태를 <b>표시·조작만</b> 한다 — 설정 화면을 닫아도 주기 확인은 계속된다.
    /// v0.27.0의 "설정 화면 체류 중 1분 카운트다운 루프"는 이걸로 대체됐다.
    /// 업데이트 불가 빌드에서는 토글·시각 표시를 숨기지 않고 비활성으로 남긴다(사용자 확정).
    /// </summary>
    private void BuildUpdatesSection(string currentVersion)
    {
        var available = UpdateCoordinator.IsAvailable;

        var autoToggle = new ToggleSwitch
        {
            Header = "Check for updates automatically",
            IsOn = UpdateCoordinator.AutoCheckEnabled,
            IsEnabled = available,
        };
        Root.Children.Add(autoToggle);
        Root.Children.Add(new TextBlock
        {
            Text = "Checks every 10 minutes in the background and notifies you when a new version is available.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var lastChecked = new TextBlock { FontSize = 12, Opacity = 0.7, IsEnabled = available };
        var status = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var checkNow = new Button { Content = "Check now", IsEnabled = available };
        var updateButton = new Button { Visibility = Visibility.Collapsed };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(checkNow);
        buttonRow.Children.Add(updateButton);

        Root.Children.Add(lastChecked);
        Root.Children.Add(status);
        Root.Children.Add(buttonRow);

        // 다운로드·설치 중에는 그 진행 문구를 전역 상태 갱신이 덮어쓰지 않게 한다.
        var installing = false;

        void Render()
        {
            autoToggle.IsOn = UpdateCoordinator.AutoCheckEnabled; // 다른 창에서 바꿔도 따라온다
            lastChecked.Text = UpdateCoordinator.DescribeLastCheck();
            checkNow.IsEnabled = available && !UpdateCoordinator.IsChecking;

            // 이미 찾아 둔 업데이트가 있으면 오토체크를 꺼도 적용 버튼은 유지한다(사용자 확정).
            if (UpdateCoordinator.PendingUpdate is { } pending)
            {
                updateButton.Content = $"Update to v{pending.TargetFullRelease.Version}";
                updateButton.Visibility = Visibility.Visible;
            }

            if (installing) return;

            if (!available)
            {
                status.Text = "Automatic updates are unavailable in this build. "
                            + "Install with Setup.exe from Releases to enable them.";
            }
            else if (UpdateCoordinator.IsChecking)
            {
                status.Text = "Checking for updates...";
            }
            else if (UpdateCoordinator.PendingUpdate is { } newer)
            {
                status.Text = $"New version v{newer.TargetFullRelease.Version} is available (current: v{currentVersion}).";
            }
            else if (UpdateCoordinator.LastCheckError.Length > 0)
            {
                status.Text = "Update check failed: " + UpdateCoordinator.LastCheckError;
            }
            else
            {
                status.Text = UpdateCoordinator.LastCheckedAt is null
                    ? string.Empty
                    : $"You are on the latest version (v{currentVersion}).";
            }
        }

        autoToggle.Toggled += (_, _) => UpdateCoordinator.SetAutoCheck(autoToggle.IsOn);
        checkNow.Click += async (_, _) => await UpdateCoordinator.CheckNowAsync();
        updateButton.Click += async (_, _) =>
        {
            if (UpdateCoordinator.PendingUpdate is not { } info) return;
            installing = true;
            await DownloadAndInstallAsync(status, updateButton, info);
            installing = false;
        };

        UpdateCoordinator.Changed += Render;
        Unloaded += (_, _) => UpdateCoordinator.Changed -= Render;
        Render();
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
                Content = $"KOTU will close and restart to finish installing v{version}. Install now?",
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

    /// <summary>섹션 머리글 추가. 만든 요소를 돌려준다(A26 — 업데이트 섹션 스크롤 목표로 쓴다).</summary>
    private TextBlock AddHeader(string text)
    {
        var header = new TextBlock
        {
            Text = text,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Root.Children.Add(header);
        return header;
    }

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

    /// <summary>'연결 프로그램' 대화상자 소유자용 창 핸들 — Window 객체 없이 XamlRoot 경유 (A25).</summary>
    private nint GetHwnd()
    {
        var environment = XamlRoot?.ContentIslandEnvironment
            ?? throw new InvalidOperationException("Cannot determine the window handle.");
        return Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
    }
}
