using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
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
