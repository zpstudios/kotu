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
    private MainWindow? _window;

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
            return router;
        });
        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // 커맨드라인 인자 해석: 파일 열기 또는 탐색기 우클릭 동사(--extract-here/--compress)
        var request = LaunchRequest.Parse(Environment.GetCommandLineArgs().Skip(1).ToList());
        DispatchRequest(request);

        // 백그라운드 업데이트 확인 (Velopack 관리 빌드에서만 동작)
        _ = PromptUpdateIfAvailableAsync(_window);
    }

    /// <summary>시작 몇 초 뒤 업데이트를 확인하고, 있으면 사용자에게 적용을 물어본다. 실패는 조용히 무시.</summary>
    private static async Task PromptUpdateIfAvailableAsync(MainWindow window)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3)); // 시작 직후에는 UI에 양보
            var info = await UpdateService.CheckAsync();
            if (info is null) return;

            window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = "업데이트 가능",
                        Content = $"새 버전 v{info.TargetFullRelease.Version}이(가) 있습니다.\n"
                                + "다운로드 후 자동으로 재시작해 적용합니다.",
                        PrimaryButtonText = "지금 업데이트",
                        CloseButtonText = "나중에",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = window.Content.XamlRoot,
                    };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                        await UpdateService.DownloadAndRestartAsync(info);
                }
                catch
                {
                    // 업데이트 실패는 다음 실행 때 다시 시도된다.
                }
            });
        }
        catch
        {
            // 오프라인·API 제한 등 — 시작을 방해하지 않는다.
        }
    }

    /// <summary>해석된 실행 요청을 창으로 보낸다. 파일이 없거나 사라졌으면 무시.</summary>
    private void DispatchRequest(LaunchRequest request)
    {
        if (_window is null || request.FilePath is not { } file || !File.Exists(file)) return;

        if (request.Verb == LaunchVerb.Open) _window.OpenFile(file);
        else _window.OpenVerb(request);
    }

    private void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        var window = _window;
        window?.DispatcherQueue.TryEnqueue(() =>
        {
            window.BringToFront();

            if (e.Kind == ExtendedActivationKind.File &&
                e.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs &&
                fileArgs.Files.FirstOrDefault() is Windows.Storage.IStorageFile file)
            {
                window.OpenFile(file.Path);
            }
            else if (e.Kind == ExtendedActivationKind.Launch &&
                     e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch &&
                     !string.IsNullOrWhiteSpace(launch.Arguments))
            {
                // 두 번째 인스턴스의 커맨드라인이 그대로 넘어온다(선행 exe 토큰 포함 가능).
                DispatchRequest(LaunchRequest.ParseCommandLine(launch.Arguments));
            }
        });
    }
}
