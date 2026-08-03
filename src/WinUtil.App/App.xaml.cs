using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
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
            // router.Register(new ImageModule(...));
            // router.Register(new ArchiveModule(...));
            // router.Register(new VideoModule(...));
            return router;
        });
        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // 커맨드라인 인자로 파일이 넘어온 경우(파일 연결/드래그 실행)
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length > 1 && File.Exists(cmdArgs[1]))
            _window.OpenFile(cmdArgs[1]);
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
        });
    }
}
