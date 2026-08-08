using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
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
            router.Register(new WinUtil.Module.Archive.ArchiveModule(
                sp.GetRequiredService<ISettingsService>()));
            router.Register(new WinUtil.Module.Video.VideoModule(
                sp.GetRequiredService<ISettingsService>()));
            router.Register(new WinUtil.Module.Document.DocumentModule()); // v0.44.0
            router.Register(new WinUtil.Module.Hardware.HardwareModule(
                sp.GetRequiredService<ISettingsService>())); // 트레이 센서 선택 복원 (A18)
            return router;
        });
        services.AddSingleton(sp => new WindowManager(
            sp.GetRequiredService<FileTypeRouter>(),
            sp.GetRequiredService<ISettingsService>())); // 창 재사용 규칙(A24)
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

        // 선택 센서 트레이(A18): 저장된 선택(기본 CPU 온도/전력)이 있으면 즉시 표시.
        // 하드웨어 창이 없어도 앱이 살아 있는 동안 유지된다(사용자 확정).
        SensorTray.Initialize(_windowManager);

        // 업데이트 주기 체크 금지(사용자 결정) — 설정 화면 진입 시 SettingsView가 1회 확인한다.
        // 설치 직후 첫 실행이면 미션 스테이트먼트 웰컴을 띄운다.
        if (Program.IsFirstRun) ShowFirstRunWelcome();
    }

    /// <summary>
    /// 설치 직후 첫 실행: 미션 스테이트먼트 웰컴 다이얼로그.
    /// Setup.exe는 원클릭(질문 없음)이라, 설치 흐름에서 사람 입력을 받는 첫 접점은 여기다.
    /// </summary>
    private void ShowFirstRunWelcome()
    {
        var window = _windowManager?.ActiveWindow;
        window?.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(600); // 창 콘텐츠·XamlRoot 준비 대기

                var dialog = new ContentDialog
                {
                    Title = "Welcome to ZP",
                    Content = new TextBlock
                    {
                        Text = Branding.MissionStatement,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 480,
                    },
                    CloseButtonText = "Get started",
                    XamlRoot = window.Content.XamlRoot,
                };
                await dialog.ShowAsync();
            }
            catch
            {
                // 웰컴 실패는 치명적이지 않다 — 조용히 넘어간다.
            }
        });
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
