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
            router.Register(new KOTU.Module.Document.DocumentModule(
                sp.GetRequiredService<ISettingsService>())); // v0.44.0 (A171에서 설정 주입)
            router.Register(new KOTU.Module.Hardware.HardwareModule(
                sp.GetRequiredService<ISettingsService>())); // 트레이 센서 선택 복원 (A18)
            // A59(v0.113.0): All Readable 통합 모듈은 **맨 마지막**에 등록한다 —
            // 담당 확장자가 다른 모듈의 합집합이라, 먼저 등록하면 라우팅(등록 순서 = 우선순위)에서
            // 전용 모듈을 가로채 탐색기 더블클릭이 전부 이 모듈로 빨려 들어간다.
            // 자식 후보는 지금까지 등록된 모듈들(자기 자신 제외)에서 뽑는다.
            router.Register(new KOTU.Module.AllReadable.AllReadableModule(router.Modules.ToList()));
            // A36(v0.109.0): 설정 화면의 "Open settings.json"이 설정 파일을 문서 모듈 에디터로 연다.
            // .json은 어느 모듈의 SupportedExtensions에도 없어(탐색기 연결 대상이 아니다) 라우팅 재정의로만
            // 문서 모듈에 붙인다 — 레지스트리 등록 목록·파일 아이콘은 그대로다.
            router.SetOverride(".json", "document");
            return router;
        });
        // A222: 창 재사용 규칙 설정(A24 window.alwaysNewWindow) 폐지로 ISettingsService 주입 제거.
        services.AddSingleton(sp => new WindowManager(
            sp.GetRequiredService<FileTypeRouter>()));
        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 재전달 활성화가 창이 다 닫히는 순간과 겹쳐도 큐잉할 수 있게 UI 디스패처를 잡아둔다
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _windowManager = Services.GetRequiredService<WindowManager>();

        // A124: 관리자 재시작(runas) 창 세트 훅 배선 — 하드웨어 모듈은 Core에만 의존하므로
        // (아키텍처 규칙) Core의 RestartSession 훅에 셸 구현을 꽂는다. Writer = runas 직전
        // 창 세트 직렬화, Discarder = UAC 취소로 재시작이 무산됐을 때의 되지우기.
        // 실패는 훅·파일 계층에서 전부 조용히 무시된다.
        KOTU.Core.Integration.RestartSession.Writer = _windowManager.WriteRestartSession;
        KOTU.Core.Integration.RestartSession.Discarder = Integration.RestartSessionFile.Delete;
        // A94 4차(v0.151.0): runas 재시작 흐름 자체도 셸로 모았다(Integration.AdminRelaunch) —
        // 하드웨어 뷰 버튼과 탐색기의 접근 거부 안내가 같은 구현을 쓴다. 모듈 쪽 진입은 같은
        // 이유(Core 의존 규칙)로 훅 경유다.
        KOTU.Core.Integration.AdminRelaunchHook.Relauncher = Integration.AdminRelaunch.Relaunch;
        // A161(v0.174.0): 이미지 모듈 "Set as desktop background" 훅 배선 — 같은 이유(모듈은
        // Core에만 의존 + 모듈 프로젝트에 DllImport 0)로 P/Invoke·레지스트리 쓰기는 셸에만 둔다.
        // 이 훅만은 모듈의 뷰 전용 워커 스레드에서 불린다(PNG 변환·파일 쓰기를 끝낸 그 워커에서
        // 이어서). 배선이 창 생성보다 앞서므로 모듈이 미배선 상태를 만나는 일은 없다.
        KOTU.Core.Integration.DesktopWallpaperHook.Setter = Integration.DesktopWallpaper.TrySet;
        // A164: 오디오 모듈 "Input device" 훅 배선 — 같은 이유(모듈은 Core에만 의존 + COM
        // interop은 셸에 격리, A105 TaskbarIdentity와 같은 격리 파일)로 비공개 IPolicyConfig
        // 호출은 셸에만 둔다. UI 스레드(플라이아웃 클릭)에서 불리고 실패는 전부 false로
        // 접힌다 — 안내 문구는 모듈이 띄운다.
        KOTU.Core.Integration.DefaultAudioInputHook.Setter = Integration.DefaultAudioInput.TrySetDefault;
        // A306(v0.290.0): 영상 재생 중 화면보호기·디스플레이 꺼짐 억제 훅 배선 — 같은 이유(모듈은
        // Core에만 의존 + 모듈 프로젝트에 DllImport 0)로 kernel32 SetThreadExecutionState는
        // 셸에만 둔다. 이 API는 스레드 단위라 반드시 UI 스레드에서 걸고 풀어야 하는데, 호출은
        // 영상 뷰의 재생 상태 전이(전부 UI 스레드)에서만 오고 창이 여럿이어도 UI 스레드는
        // 하나라(WindowManager 주석) 그 조건이 구조적으로 성립한다. 창별 요구 개수 세기는
        // 훅이 한다 — 여기 구현은 "걸어라/풀어라"만 받는다.
        KOTU.Core.Integration.DisplayAwakeHook.Setter = Integration.DisplayAwake.Set;

        // 커맨드라인 인자 해석: 파일 열기 또는 탐색기 우클릭 동사(--extract-here/--compress)
        // → 멀티 윈도우 라우팅(같은 모듈 재사용/새 창)은 WindowManager가 담당
        var request = LaunchRequest.Parse(Environment.GetCommandLineArgs().Skip(1).ToList());
        // A124: 관리자 재시작 직전에 기록된 창 세트가 있으면(2분 유효) 기본 1창 대신 세트를
        // 재현한다. 쓰는 쪽이 하드웨어 모듈 Restart as admin 한 곳뿐이라, 일반 시작(파일 인자·
        // 바로가기)이 여기서 복원하게 되는 일은 실질적으로 승격 재기동뿐이다. 파일 인자가
        // 함께 온 극단 케이스도 복원 뒤 기존 라우팅으로 마저 연다(창 재사용 규칙 A24 그대로).
        if (!_windowManager.TryRestoreSession() || request.FilePath is not null)
            _windowManager.Dispatch(request);

        // 선택 센서 트레이(A18 SensorTray)는 A101(v0.137.0)에서 폐지 — 창별 트레이 아이콘이
        // 그 창의 선택값을 표시한다(HardwareView의 ITrayStatusProvider). 상시 표시 축은 소멸.

        // 셸 등록 정비: 구 브랜드 흔적 1회 청소(A46) + exe 경로 자동 재등록(A78, 매 실행).
        ShellRegistrationMaintenance();

        // 업데이트 전역 상태 준비 — 저장된 마지막 확인 결과를 읽기만 한다.
        // A206(v0.215.0): 여기서 확인도, 타이머 시작도 하지 않는다 — 자동 확인은 사용자가
        // 설정 화면에 들어간 순간부터(진입 즉시 1회 + 머무는 동안 2분 간격) 돌고, 화면을 떠나면
        // 멈춘다. 타이머는 그동안 프로세스당 1개다(창 수와 무관).
        // 새 버전을 찾아도 토스트는 없다(A114 알림 방식 b — 설정 화면 표시가 전부).
        Integration.UpdateCoordinator.Initialize();

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

                var mission = new TextBlock
                {
                    Text = Branding.MissionStatement,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 480,
                };

                // A79 ④(v0.119.0): 마스코트는 브랜드 레벨이 높을 때만 문구 위에 붙는다.
                // 꺼져 있으면 지금까지처럼 문구만 — 빈 자리를 만들지 않는다.
                object content = mission;
                if (BrandAssets.CreateMascot(128) is { } mascot)
                {
                    var stack = new StackPanel { Spacing = 12 };
                    stack.Children.Add(mascot);
                    stack.Children.Add(mission);
                    content = stack;
                }

                var dialog = new ContentDialog
                {
                    Title = $"Welcome to {Branding.AppName}",
                    Content = content,
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
