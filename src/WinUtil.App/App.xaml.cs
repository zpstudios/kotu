using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using WinUtil.App.Integration;
using WinUtil.Core.Cli;
using WinUtil.Core.Routing;
using WinUtil.Core.Settings;

namespace WinUtil.App;

public partial class App : Application
{
    private WindowManager? _windowManager;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();

        // XAML 스레드에서 잡히지 않은 예외도 로그·안내 (unpackaged 앱은 그냥 조용히 죽는다)
        UnhandledException += (_, e) => Program.LogFatal(e.Exception, "Xaml UnhandledException");

        Services = ConfigureServices();

        // 실행 중 다른 인스턴스에서 파일이 넘어올 때
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton(sp =>
        {
            var router = new FileTypeRouter();
            // Phase 1+에서 모듈 등록. 등록 순서 = 우선순위.
            router.Register(new WinUtil.Module.Image.ImageModule());
            router.Register(new WinUtil.Module.Archive.ArchiveModule());
            router.Register(new WinUtil.Module.Video.VideoModule(
                sp.GetRequiredService<ISettingsService>()));
            router.Register(new WinUtil.Module.Hardware.HardwareModule());
            return router;
        });
        services.AddSingleton(sp => new WindowManager(sp.GetRequiredService<FileTypeRouter>()));
        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 재전달 활성화가 창이 다 닫히는 순간과 겹쳐도 큐잉할 수 있게 UI 디스패처를 잡아둔다
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _windowManager = Services.GetRequiredService<WindowManager>();

        // 커맨드라인 인자 해석: 파일 열기 또는 탐색기 우클릭 동사(--extract-here/--compress)
        // → 멀티 윈도우 라우팅(같은 모듈 재사용/새 창)은 WindowManager가 담당
        var request = LaunchRequest.Parse(Environment.GetCommandLineArgs().Skip(1).ToList());
        _windowManager.Dispatch(request);

        // 백그라운드 주기 업데이트 확인 (Velopack 관리 빌드에서만 동작)
        _ = PeriodicUpdateCheckAsync(_windowManager);
    }

    /// <summary>
    /// 시작 3초 뒤 첫 확인, 이후 60초 간격으로 업데이트를 확인한다.
    /// 같은 버전을 "나중에"로 거절하면 이 세션에서는 다시 묻지 않는다.
    /// 실패(오프라인·GitHub API 시간당 한도 등)는 조용히 넘기고 다음 주기에 재시도.
    /// </summary>
    private static async Task PeriodicUpdateCheckAsync(WindowManager manager)
    {
        string? declinedVersion = null;
        var prompting = false;

        await Task.Delay(TimeSpan.FromSeconds(3)); // 시작 직후에는 UI에 양보

        while (true)
        {
            try
            {
                if (!prompting)
                {
                    var info = await UpdateService.CheckAsync();
                    var version = info?.TargetFullRelease.Version.ToString();

                    // 멀티 윈도우: 다이얼로그는 가장 최근 활성화된 창에 띄운다
                    var window = manager.ActiveWindow;

                    if (info is not null && version is not null && version != declinedVersion
                        && window is not null)
                    {
                        prompting = true;
                        window.DispatcherQueue.TryEnqueue(async () =>
                        {
                            try
                            {
                                var dialog = new ContentDialog
                                {
                                    Title = "Update available",
                                    Content = $"Version v{version} is available.\n"
                                            + "It will be downloaded and applied with an automatic restart.",
                                    PrimaryButtonText = "Update now",
                                    CloseButtonText = "Later",
                                    DefaultButton = ContentDialogButton.Primary,
                                    XamlRoot = window.Content.XamlRoot,
                                };
                                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                                    await DownloadWithProgressAsync(window, info);
                                else
                                    declinedVersion = version;
                            }
                            catch
                            {
                                // 다른 다이얼로그가 열려 있는 등 — 다음 주기에 다시 시도.
                            }
                            finally
                            {
                                prompting = false;
                            }
                        });
                    }
                }
            }
            catch
            {
                // 확인 실패는 다음 주기에 재시도.
            }

            await Task.Delay(TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>진행률 다이얼로그를 띄우고 다운로드 → 적용·재시작. (패키지가 커서 무표시면 멈춘 것처럼 보인다)</summary>
    private static async Task DownloadWithProgressAsync(MainWindow window, Velopack.UpdateInfo info)
    {
        var label = new TextBlock { Text = "Preparing download..." };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(label);
        panel.Children.Add(bar);

        var progressDialog = new ContentDialog
        {
            Title = $"Updating to v{info.TargetFullRelease.Version}",
            Content = panel,
            XamlRoot = window.Content.XamlRoot,
        };
        _ = progressDialog.ShowAsync(); // 버튼 없음 — 완료/실패 시 코드로 닫는다

        try
        {
            await UpdateService.DownloadAsync(info, percent =>
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    bar.Value = percent;
                    label.Text = $"Downloading... {percent}%";
                }));

            label.Text = "Applying and restarting...";
            bar.Value = 100;
            await Task.Delay(400); // 사용자에게 상태 전환을 보여줄 짧은 틈
            UpdateService.ApplyAndRestart(info);
        }
        catch (Exception ex)
        {
            progressDialog.Hide();
            var error = new ContentDialog
            {
                Title = "Update failed",
                Content = ex.Message + "\nIt will be retried on the next run.",
                CloseButtonText = "Close",
                XamlRoot = window.Content.XamlRoot,
            };
            _ = error.ShowAsync();
        }
    }

    private void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        var manager = _windowManager;
        if (manager is null) return;

        _uiDispatcher?.TryEnqueue(() =>
        {
            if (e.Kind == ExtendedActivationKind.File &&
                e.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs &&
                fileArgs.Files.FirstOrDefault() is Windows.Storage.IStorageFile file)
            {
                manager.OpenFile(file.Path);
            }
            else if (e.Kind == ExtendedActivationKind.Launch &&
                     e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch &&
                     !string.IsNullOrWhiteSpace(launch.Arguments))
            {
                // 두 번째 인스턴스의 커맨드라인이 그대로 넘어온다(선행 exe 토큰 포함 가능).
                manager.Dispatch(LaunchRequest.ParseCommandLine(launch.Arguments));
            }
            else
            {
                // 인자 없는 재실행: 최근 창만 앞으로
                manager.ActiveWindow?.BringToFront();
            }
        });
    }
}
