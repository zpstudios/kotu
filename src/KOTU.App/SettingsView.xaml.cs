using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KOTU.App.Integration;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;
using KOTU.Core.Settings;
using KOTU.Core.Threading;

namespace KOTU.App;

/// <summary>
/// 설정 페이지. UI 스케일(v0.24.0), 탐색기 통합(파일 연결·우클릭 메뉴)을 관리한다.
/// 탐색기 등록은 현재 사용자(HKCU) 범위 — 관리자 권한 불필요, 해제 시 흔적 없음.
/// 하단 바(광고 + ⛶ 전체화면)는 셸이 TakeBottomBar()로 가져간다(v0.50.0).
/// Updates 섹션은 전역 <see cref="UpdateCoordinator"/>의 상태를 표시만 하고, 확인은
/// "Check now" · <b>이 화면 진입 1회</b> · 2분 주기 타이머의 세 경로가 코디네이터에서 돈다
/// (A114, v0.136.0 — A95의 "수동 전용"을 대체. 토스트·오토체크 토글은 계속 없다).
/// 연결 토글의 레지스트리 작업·기본 앱 개수 조회는 전부 <see cref="Worker"/>에서 돌고
/// UI에는 진행률과 결과만 흘러온다(A77, v0.106.0).
/// </summary>
public sealed partial class SettingsView : UserControl, IBottomBarProvider
{
    private readonly TextBlock _status = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
    private readonly ISettingsService _settings;
    private bool _suppressToggle;

    /// <summary>
    /// 설정 화면 전용 직렬 워커(A42 계약, A77에서 도입). 레지스트리 등록·해제·UserChoice 쓰기·
    /// 기본 앱 개수 조회가 전부 여기서 돈다. 모듈별로 나누지 않고 하나로 둔 이유 —
    /// 모듈들이 Capabilities 키 하나를 공유해 동시 쓰기가 서로를 지울 수 있다.
    /// 화면 UI는 모듈마다 따로 놀지만(각자 링·텍스트·토글) 실제 작업은 큐 순서대로 직렬 실행된다.
    /// </summary>
    private ModuleWorker? _worker;

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다(ExplorerPane과 같은 규칙).</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker($"{Branding.AppName} settings worker");

    /// <summary>
    /// 뷰가 화면에 붙어 있는지 (A77). 워커 결과가 Unloaded 뒤에 도착해도 UI 요소·설정 페이지 열기
    /// 같은 부수효과로 새지 않게 막는 가드다.
    /// </summary>
    private bool _uiAlive = true;

    public SettingsView(FileTypeRouter router)
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        Build(router);
        Loaded += (_, _) =>
        {
            _uiAlive = true;
            Focus(FocusState.Programmatic); // F11/Esc 액셀러레이터가 바로 듣게
        };
        Unloaded += (_, _) =>
        {
            // A77: 화면을 떠난 뒤 워커가 끝나도 UI를 만지지 않는다.
            // 진행 중인 레지스트리 작업은 중간에 끊지 않고(반쯤 등록된 상태 방지) 워커가 마저 끝낸다.
            _uiAlive = false;
            _worker?.Dispose();
            _worker = null;
        };
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
        // A79 ③(v0.119.0): 설정 화면 상단 워드마크. 꺼져 있으면 요소를 만들지 않는다 — 빈 자리를 남기지 말 것.
        if (BrandAssets.CreateWordmark(40) is { } wordmark)
        {
            wordmark.HorizontalAlignment = HorizontalAlignment.Left;
            Root.Children.Add(wordmark);
        }

        BuildDisplaySection();
        BuildWindowsSection(); // 창 재사용 규칙 (A24)

        AddHeader("Explorer integration");
        Root.Children.Add(new TextBlock
        {
            Text = "Applies to the current user account only (no admin rights needed); turning a switch off removes the registration completely. "
                 + $"Turning a switch on also makes {Branding.AppName} the default app for those file types automatically. "
                 + "Windows may block this for a few protected types - those open the Windows default-apps page so you can confirm once, "
                 + "or use \"Set default...\" per extension. (A38)",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        // 토글 순서(A35, 사용자 확정 2026-08-10): 이미지 → 비디오 → 오디오 → 문서 → 압축.
        // 시작 메뉴 번호 순서(1이미지 2영상 3오디오 4문서 5압축)와 일치시킨 것 —
        // v0.28.0의 "압축→문서→영상→이미지"를 대체한다.
        // 파일을 다루지 않는 모듈(hardware)은 연결할 확장자가 없으므로 토글을 만들지 않는다.
        // A59(v0.113.0): All Readable도 이 섹션에 없다 — 담당 확장자가 다른 모듈의 합집합이라
        // 함께 등록하면 확장자마다 ProgID·UserChoice·Capabilities를 서로 덮어쓴다
        // (제외 판단의 단일 소스 = IModule.RegistersFileAssociations).
        string[] associationOrder = ["image", "video", "audio", "document", "archive"];
        var associationModules = router.Modules
            .Where(m => m.SupportedExtensions.Count > 0 && m.RegistersFileAssociations)
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
                VerticalAlignment = VerticalAlignment.Center,
            };

            // A77(v0.106.0): 토글 행 우측에 진행 링 + 진행/결과 텍스트.
            // StackPanel이 아니라 Grid를 쓰는 이유 — 헤더 문구가 길어도(확장자 나열) 링·텍스트가
            // MaxWidth 680 밖으로 밀려나 잘리지 않게 오른쪽에 고정하기 위함.
            // A79 ⑤(v0.119.0): 앱에서 가장 오래 도는 인디케이터라 발바닥 스피너를 여기 하나에만 붙였다.
            // 브랜드 레벨이 낮으면 지금까지의 ProgressRing 그대로다.
            var busySpinner = BrandSpinner.Create(16);
            busySpinner.Element.Visibility = Visibility.Collapsed;
            var progressText = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            var toggleRow = new Grid { ColumnSpacing = 8 };
            toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(toggle, 0);
            Grid.SetColumn(busySpinner.Element, 1);
            Grid.SetColumn(progressText, 2);
            toggleRow.Children.Add(toggle);
            toggleRow.Children.Add(busySpinner.Element);
            toggleRow.Children.Add(progressText);
            Root.Children.Add(toggleRow);

            // A25(v0.61.0): 현재 기본 앱 현황(n/m) + 확장자별 '연결 프로그램' 대화상자 진입
            var defaultsText = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                // 조회 결과가 오기 전 자리 — 숫자만 나중에 채워져 줄 너비가 튀지 않는다.
                Text = $"Default app for .../{module.SupportedExtensions.Count} extensions",
            };

            void ShowDefaults(int count) =>
                defaultsText.Text = $"Default app for {count}/{module.SupportedExtensions.Count} extensions";

            // A77: 레지스트리 조회는 워커에서, 대입만 UI에서. 큐가 직렬이라 등록 작업 뒤에 넣으면
            // 항상 '작업이 끝난 뒤의 값'을 읽는다.
            void RefreshDefaultsAsync()
            {
                var dispatcher = DispatcherQueue;
                Worker.Post(() =>
                {
                    var count = SafeCountDefaults(module);
                    dispatcher.TryEnqueue(() =>
                    {
                        if (_uiAlive) ShowDefaults(count);
                    });
                });
            }
            RefreshDefaultsAsync();

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
                    RefreshDefaultsAsync(); // 대화상자에서 고르면 즉시 반영된다
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

            // 같은 모듈의 재진입 방지 플래그(A77). 작업 중에는 토글도 비활성이라 사람 조작으로는
            // 도달하지 않지만, 실패 되돌리기(IsOn 재설정)나 프로그램적 변경까지 막아 준다.
            var busy = false;

            toggle.Toggled += async (_, _) =>
            {
                if (_suppressToggle || busy) return;
                var turnedOn = toggle.IsOn;
                var total = module.SupportedExtensions.Count;

                // 작업 시작: 이 모듈의 토글만 잠그고 진행 링을 켠다(다른 모듈 토글은 그대로).
                busy = true;
                toggle.IsEnabled = false;
                busySpinner.Element.Visibility = Visibility.Visible;
                busySpinner.SetActive(true);
                progressText.Text = $"{(turnedOn ? AssociationProgress.Registering : AssociationProgress.Unregistering)}... (0/{total})";
                _status.Text = string.Empty;

                var progress = new DispatcherProgress<AssociationProgress>(DispatcherQueue, p =>
                {
                    if (_uiAlive) progressText.Text = $"{p.Phase}... ({p.Done}/{p.Total})";
                });

                AssociationOutcome outcome;
                try
                {
                    outcome = await Worker.Run(ctx => ApplyAssociation(module, turnedOn, progress));
                }
                catch (Exception ex)
                {
                    // 워커가 이미 닫혔거나(뷰 이탈) 예상 못 한 실패 — 기존 Apply()와 같은 실패 처리로 보낸다.
                    // 개수는 세어 보지도 못했으므로 -1 = "모름"으로 두고 화면 숫자는 건드리지 않는다.
                    outcome = new AssociationOutcome(false, ex.Message, [], -1);
                }

                // 여기부터는 UI 스레드. 화면을 떠났어도 잠금은 풀어 둔다(다시 로드되면 그대로 쓰인다).
                busy = false;
                toggle.IsEnabled = true;
                busySpinner.SetActive(false);
                busySpinner.Element.Visibility = Visibility.Collapsed;
                if (!_uiAlive) return;

                if (outcome.Defaults >= 0) ShowDefaults(outcome.Defaults);

                if (!outcome.Ok)
                {
                    // 기존 Apply()의 실패 동작 유지: 토글을 원위치로 되돌리고 이유를 표시한다.
                    _suppressToggle = true;
                    toggle.IsOn = !toggle.IsOn;
                    _suppressToggle = false;
                    progressText.Text = string.Empty;
                    _status.Text = "Failed to apply: " + outcome.Error;
                    return;
                }

                if (!turnedOn)
                {
                    progressText.Text = string.Empty; // 해제는 부분 실패 개념이 없다
                    return;
                }

                // 켤 때만: A38 — 기본 앱까지 자동 지정 시도(ApplyAssociation 안에서 이미 끝났다).
                var failed = outcome.Failed;
                if (failed.Count == 0)
                {
                    progressText.Text = string.Empty;
                    _status.Text = $"{module.BrandName}: set as the default app for all {total} file types.";
                }
                else
                {
                    // 부분 실패는 토글을 되돌리지 않는다(A77 확정) — 결과를 행에 남겨 다음 조작 전까지 유지한다.
                    progressText.Text = $"Registered {total - failed.Count}/{total} ({failed.Count} failed)";

                    // 실패 확장자는 A25 폴백 — 설정 딥링크를 한 번 열어 사용자가 확정하게 한다
                    // (확장자별 대화상자는 "Set default..." 버튼으로 여전히 가능).
                    _status.Text = $"{module.BrandName}: set {total - failed.Count}/{total} automatically. "
                                 + $"Windows blocks the rest ({string.Join(" ", failed)}) - confirm them on the "
                                 + "page that just opened, or use \"Set default...\".";
                    ExplorerIntegration.OpenDefaultAppsSettings();
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

        BuildSettingsFileSection(); // A36: 연결 섹션 아래 "Open settings.json"

        AddHeader("Updates");
        var currentVersion = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "?";
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

    /// <summary>
    /// A36(v0.109.0): 연결 섹션 아래 "Open settings.json" 버튼 + 경로·주의 안내.
    /// 저장 위치·포맷은 현행 %AppData%\KOTU\settings.json 그대로고(부록 B 37번 확정)
    /// 표기만 실제 파일명에 맞춘다 — 경로 문자열은 하드코딩하지 않고 ISettingsService.FilePath에서 읽는다.
    /// 여는 방식은 <b>새 인스턴스</b>(WindowManager.OpenFileInNewWindow) — 보고 있던 설정 화면을 잃지 않게.
    /// .json은 어느 모듈의 SupportedExtensions에도 없어서 App의 라우팅 재정의(.json → document)가
    /// 이 파일을 문서 모듈(KOTU-doc) 에디터로 보낸다.
    /// 저장 후 자동 재로드는 넣지 않는다(사용자 확정) — 재시작 반영이며 아래 안내 줄이 그 고지다.
    /// </summary>
    private void BuildSettingsFileSection()
    {
        var openButton = new Button
        {
            Content = "Open settings.json",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Root.Children.Add(openButton);

        Root.Children.Add(new TextBlock
        {
            Text = $"{_settings.FilePath} - changes apply after restart. "
                 + "Editing this file directly can break your settings.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, // 경로를 그대로 복사해 갈 수 있게
        });

        // 실패 사유 전용 줄(성공하면 보이지 않는다) — 공용 _status는 연결 토글 결과가 쓴다.
        var status = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        Root.Children.Add(status);

        openButton.Click += (_, _) =>
        {
            var path = _settings.FilePath;
            try
            {
                // 설정을 한 번도 바꾸지 않은 프로필에는 파일이 아직 없다 — 현재 값을 먼저 디스크로 내린다.
                if (!File.Exists(path)) _settings.Save();
                if (!File.Exists(path))
                {
                    status.Text = "Could not create the settings file.";
                    status.Visibility = Visibility.Visible;
                    return;
                }

                status.Visibility = Visibility.Collapsed;
                App.Services.GetRequiredService<WindowManager>().OpenFileInNewWindow(path);
            }
            catch (Exception ex)
            {
                status.Text = "Could not open the settings file: " + ex.Message;
                status.Visibility = Visibility.Visible;
            }
        };
    }

    /// <summary>
    /// Updates 섹션(A95, v0.117.0 — 확인 정책은 A114, v0.136.0). 구성은 위에서부터
    /// <b>현재 버전 · 최신 버전 · 마지막 확인 시각 · [Check now][Update to vX] · 안내 문구</b>.
    /// 확인은 Check now · <b>이 섹션을 만들 때(설정 진입) 1회</b> · 2분 주기 타이머 셋 다 돌지만
    /// 새 버전 알림은 여기 표시가 전부다 — <b>토스트·팝업은 금지</b>(A114 알림 방식 b).
    /// (v0.17.0 → v0.105.0 → v0.117.0 → v0.136.0으로 네 번 뒤집힌 정책이다. 상세는 UpdateCoordinator 주석).
    /// 실제 확인은 전역 <see cref="UpdateCoordinator"/>가 소유하고 여기서는 그 상태를 <b>표시만</b> 한다 —
    /// 다른 창에서 확인해도 이 화면이 따라 갱신된다.
    /// 업데이트 불가 빌드에서는 표시를 숨기지 않고 비활성으로 남긴다(사용자 확정).
    /// </summary>
    private void BuildUpdatesSection(string currentVersion)
    {
        var available = UpdateCoordinator.IsAvailable;

        // TextBlock은 Control이 아니라 IsEnabled가 없다 — 업데이트 불가 빌드의 '비활성' 표현은
        // 흐리게(Opacity)로 대신한다. 버튼만 진짜 IsEnabled=false로 잠근다. (v0.108.1)
        var dim = available ? 0.7 : 0.4;
        var latest = new TextBlock { FontSize = 12, Opacity = dim };
        var lastChecked = new TextBlock { FontSize = 12, Opacity = dim };
        var status = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var checkNow = new Button { Content = "Check now", IsEnabled = available };
        var updateButton = new Button { Visibility = Visibility.Collapsed };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(checkNow);
        buttonRow.Children.Add(updateButton);

        Root.Children.Add(new TextBlock { Text = $"Current version: v{currentVersion}", Opacity = 0.8 });
        Root.Children.Add(latest);
        Root.Children.Add(lastChecked);
        Root.Children.Add(buttonRow);
        Root.Children.Add(status); // 안내 문구는 버튼 줄 '밑'이다(A95).

        // 다운로드·설치 중에는 그 진행 문구를 전역 상태 갱신이 덮어쓰지 않게 한다.
        var installing = false;

        void Render()
        {
            lastChecked.Text = UpdateCoordinator.DescribeLastCheck();
            checkNow.IsEnabled = available && !UpdateCoordinator.IsChecking;

            // 한 번 찾은 업데이트는 뒤이은 확인이 실패해도 적용 버튼을 유지한다.
            if (UpdateCoordinator.PendingUpdate is { } pending)
            {
                latest.Text = $"Latest version: v{pending.TargetFullRelease.Version}";
                updateButton.Content = $"Update to v{pending.TargetFullRelease.Version}";
                updateButton.Visibility = Visibility.Visible;
            }
            else if (UpdateCoordinator.LastCheckedAt is null)
            {
                latest.Text = "Latest version: not checked yet";
            }
            else
            {
                // 확인은 했는데 새 버전이 없다 = 지금 것이 최신. 실패했으면 최신이 뭔지 알 수 없다.
                latest.Text = UpdateCoordinator.LastCheckError.Length > 0
                    ? "Latest version: unknown"
                    : $"Latest version: v{currentVersion}";
            }

            if (installing) return;

            if (!available)
            {
                status.Text = "In-app updates are unavailable in this build. "
                            + "Install with Setup.exe from Releases to enable them.";
            }
            else if (UpdateCoordinator.IsChecking)
            {
                status.Text = "Checking for updates...";
            }
            else if (UpdateCoordinator.LastCheckError.Length > 0)
            {
                status.Text = "Update check failed: " + UpdateCoordinator.LastCheckError;
            }
            else if (UpdateCoordinator.PendingUpdate is not null)
            {
                status.Text = string.Empty; // 새 버전은 위 줄과 적용 버튼이 이미 말한다.
            }
            else
            {
                // 아직 한 번도 확인하지 않았으면 아무 말도 하지 않는다(A95).
                status.Text = UpdateCoordinator.LastCheckedAt is null
                    ? string.Empty
                    : "You are on the latest version.";
            }
        }

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

        // A114(v0.136.0): 설정 화면 진입마다 즉시 1회 확인. 이 뷰는 설정을 열 때마다 새로 만들어지므로
        // (MainWindow.ShowSettingsAsync가 new SettingsView) 여기 한 줄이 곧 "진입 1회"다.
        // 2분 주기 타이머와 겹쳐도 CheckNowAsync가 진행 중이면 되돌아가 요청이 두 번 나가지 않는다.
        // 발사 후 망각 — 예외는 코디네이터가 삼키고 결과는 Render로 돌아온다.
        _ = UpdateCoordinator.CheckNowAsync();
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
                status.Text = $"v{version} downloaded - click the button again to install when ready.";
                updateButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            status.Text = "Update failed: " + ex.Message;
            updateButton.IsEnabled = true;
        }
    }

    /// <summary>섹션 머리글 추가.</summary>
    private void AddHeader(string text)
    {
        Root.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
    }

    /// <summary>
    /// 연결 토글 한 번의 결과 (A77, v0.106.0). 워커에서 만들어 UI 스레드로 통째로 건너온다.
    /// </summary>
    /// <param name="Ok">등록/해제 자체가 성공했는지. false면 토글을 원위치로 되돌린다.</param>
    /// <param name="Error">실패 사유(성공이면 null).</param>
    /// <param name="Failed">기본 앱 지정(A38)에 실패한 확장자 — 부분 실패는 토글을 되돌리지 않는다.</param>
    /// <param name="Defaults">작업 후 다시 센 "기본 앱인 확장자" 개수. 세지 못했으면 -1(화면 숫자 유지).</param>
    private readonly record struct AssociationOutcome(
        bool Ok, string? Error, IReadOnlyList<string> Failed, int Defaults);

    /// <summary>
    /// 워커 스레드 전용 (A77, v0.106.0) — 연결 등록/해제 + (켤 때만) 기본 앱 지정 + 개수 조회를
    /// 한 작업으로 묶어 처리한다. UI 요소는 일절 건드리지 않고 진행률만 <paramref name="progress"/>로 흘린다.
    /// </summary>
    private static AssociationOutcome ApplyAssociation(IModule module, bool turnOn,
        IProgress<AssociationProgress> progress)
    {
        try
        {
            if (turnOn) ExplorerIntegration.RegisterAssociation(module, progress);
            else ExplorerIntegration.UnregisterAssociation(module, progress);
        }
        catch (Exception ex)
        {
            return new AssociationOutcome(false, ex.Message, [], SafeCountDefaults(module));
        }

        IReadOnlyList<string> failed = [];
        if (turnOn)
        {
            try { failed = ExplorerIntegration.SetAsDefault(module, progress); }
            catch { failed = module.SupportedExtensions; }
        }
        return new AssociationOutcome(true, null, failed, SafeCountDefaults(module));
    }

    /// <summary>기본 앱인 확장자 개수 — 조회 실패는 0으로 본다(워커에서만 호출).</summary>
    private static int SafeCountDefaults(IModule module)
    {
        try { return ExplorerIntegration.CountDefaults(module); }
        catch { return 0; }
    }

    /// <summary>
    /// 워커 → UI 진행률 마샬링 (A77, v0.106.0). <see cref="Progress{T}"/>의 SynchronizationContext
    /// 캡처 대신 DispatcherQueue.TryEnqueue를 쓴다(확정 사항). 창이 닫혀 큐가 멈춘 뒤의 보고는
    /// TryEnqueue가 false를 돌려주며 조용히 버려지고, 뷰가 살아 있는지는 콜백 안에서 따로 확인한다.
    /// </summary>
    private sealed class DispatcherProgress<T> : IProgress<T>
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
        private readonly Action<T> _onReport;

        public DispatcherProgress(Microsoft.UI.Dispatching.DispatcherQueue queue, Action<T> onReport)
        {
            _queue = queue;
            _onReport = onReport;
        }

        public void Report(T value) => _queue.TryEnqueue(() => _onReport(value));
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
