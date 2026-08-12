using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using KOTU.Core.Cli;
using KOTU.Core.Routing;
using KOTU.Core.Settings;

namespace KOTU.App;

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
            router.Register(new KOTU.Module.Image.ImageModule());
            router.Register(new KOTU.Module.Archive.ArchiveModule(
                sp.GetRequiredService<ISettingsService>()));
            router.Register(new KOTU.Module.Video.VideoModule(
                sp.GetRequiredService<ISettingsService>()));
            router.Register(new KOTU.Module.Audio.AudioModule(
                sp.GetRequiredService<ISettingsService>())); // 음악 재생 분리 (A10, v0.75.0)
            router.Register(new KOTU.Module.Document.DocumentModule()); // v0.44.0
            router.Register(new KOTU.Module.Hardware.HardwareModule(
                sp.GetRequiredService<ISettingsService>())); // 트레이 센서 선택 복원 (A18)
            // A36(v0.109.0): 설정 화면의 "Open settings.json"이 설정 파일을 문서 모듈 에디터로 연다.
            // .json은 어느 모듈의 SupportedExtensions에도 없어(탐색기 연결 대상이 아니다) 라우팅 재정의로만
            // 문서 모듈에 붙인다 — 레지스트리 등록 목록·파일 아이콘은 그대로다.
            router.SetOverride(".json", "document");
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

        // 셸 등록 정비: 구 브랜드 흔적 1회 청소(A46) + exe 경로 자동 재등록(A78, 매 실행).
        ShellRegistrationMaintenance();

        // 업데이트 백그라운드 주기 체크 + 네이티브 토스트(A26·A76, v0.105.0).
        // v0.17.0의 "주기 체크 금지, 설정 진입 시에만" 정책을 대체한다 —
        // 타이머는 프로세스당 1개(UpdateCoordinator)이고 설정 화면은 그 상태를 표시만 한다.
        Integration.UpdateCoordinator.Initialize(Services.GetRequiredService<WindowManager>());

        // 설치 직후 첫 실행이면 미션 스테이트먼트 웰컴을 띄운다.
        if (Program.IsFirstRun) ShowFirstRunWelcome();
    }

    /// <summary>
    /// 시작 시 셸 등록 정비 — 레지스트리 접근이라 UI를 막지 않게 워커 스레드에서 실행.
    ///  · 구 브랜드(ZP·WinUtil) 청소(A46): 설정 플래그로 1회만. 설정 파일은 새 폴더(%AppData%\KOTU)라
    ///    리브랜딩 후 첫 실행에서는 항상 플래그가 없다 = 정확히 한 번 돈다.
    ///  · exe 경로 자동 재등록(A78): 1회 플래그가 아니라 매 실행 확인 — 경로는 언제든 다시 바뀔 수 있다
    ///    (자동 업데이트로 exe명 변경, 포터블 이동 등).
    /// </summary>
    private static void ShellRegistrationMaintenance()
    {
        var settings = Services.GetRequiredService<ISettingsService>();
        const string key = "integration.legacyBrandCleanupDone";
        var needsLegacyCleanup = !settings.Get(key, false);

        var modules = Services.GetRequiredService<FileTypeRouter>().Modules.ToList();
        var worker = new KOTU.Core.Threading.ModuleWorker(
            $"{Branding.AppName} shell registration maintenance", ThreadPriority.BelowNormal);
        worker.Post(() =>
        {
            if (needsLegacyCleanup)
            {
                Integration.ExplorerIntegration.CleanUpLegacyBrandRegistrations(modules);
                settings.Set(key, true);
                settings.Save();
            }

            // A78: 우클릭 메뉴 라벨은 하드코딩하지 않고 압축 모듈 BrandName을 따른다(A52와 동일 원칙).
            var archiveBrand = modules.FirstOrDefault(m => m.Id == "archive")?.BrandName
                               ?? Branding.AppName;
            Integration.ExplorerIntegration.ReRegisterIfExeMoved(modules, archiveBrand);

            worker.Dispose();
        });
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
                    Title = $"Welcome to {Branding.AppName}",
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
