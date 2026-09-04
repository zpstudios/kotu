using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace KOTU.App;

/// <summary>
/// 진입점. 프로세스는 단일 인스턴스를 유지한다:
/// 탐색기에서 파일을 여러 번 열어도 새 프로세스를 띄우지 않고
/// 기존 프로세스로 활성화 인자를 넘긴다. 창을 새로 열지 재사용할지는
/// 넘겨받은 쪽의 WindowManager가 결정한다(v0.14.0 멀티 윈도우).
/// 또한 unpackaged WinUI 앱은 초기화 실패 시 아무 UI 없이 조용히 죽기 쉬우므로,
/// 시작 단계 예외를 파일 로그 + 네이티브 메시지 박스로 반드시 드러낸다.
/// </summary>
public static class Program
{
    private const string InstanceKey = Branding.AppName + "-Main"; // A46/v0.86.0 리브랜딩 (구: ZP-Main, 그 전: WinUtil-Main)

    /// <summary>설치 후 첫 실행 여부(Velopack 훅). 미션 웰컴 다이얼로그 표시에 쓴다.</summary>
    internal static bool IsFirstRun { get; private set; }

    /// <summary>시작 실패 로그 경로: %TEMP%\KOTU\startup-error.log</summary>
    private static string LogPath =>
        Path.Combine(Path.GetTempPath(), Branding.AppName, "startup-error.log");

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => LogFatal(e.ExceptionObject as Exception, "AppDomain");

        try
        {
            // Velopack 훅: 설치/업데이트/제거 시 넘어오는 특수 인자를 처리한다.
            // (해당 인자면 여기서 프로세스가 종료되므로 반드시 가장 먼저 호출)
            // OnFirstRun: 설치 후 첫 실행 감지 → 웰컴 다이얼로그(App에서 표시)
            // (Velopack 1.x API — WithFirstRun은 구 0.x 이름이라 CS1061로 CI가 죽었음, v0.19.2)
            // OnBeforeUninstallFastCallback: 제거 직전에만 불리고 끝나면 프로세스가 종료된다
            //   (Windows 전용 · 30초 제한). A350 표시 키는 HKCU라 제거 관리자가 손대지 않으므로
            //   여기서 직접 지워야 삭제 후 유령 키가 남지 않는다.
            Velopack.VelopackApp.Build()
                .OnFirstRun(_ => IsFirstRun = true)
                .OnBeforeUninstallFastCallback(_ => Integration.TaskbarIdentity.RemoveAllDisplayKeys())
                .Run();

            // A350 후속(v0.343.1): 프로세스 AUMID를 "KOTU"로 못 박고 그 이름의 표시 키를 등록한다.
            // 반드시 창이 하나도 만들어지기 전(= 여기) — 프로세스 AUMID는 첫 창 생성 전에 박아야
            // 셸이 인식한다. 창별 AUMID(TaskbarIdentity.Apply)는 창 프로퍼티로 이 값을 덮으므로
            // 태스크바 인스턴스 분리(A105)는 그대로다.
            Integration.TaskbarIdentity.ApplyProcessIdentity();

            WinRT.ComWrappersSupport.InitializeComWrappers();

            var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

            if (!mainInstance.IsCurrent)
            {
                // A228: 리다이렉트 전에 주 인스턴스로 포그라운드 전환 권한을 이양한다.
                // 이 프로세스는 탐색기가 방금 시작해 대개 권한을 갖고 있지만, 백그라운드의
                // 주 인스턴스는 이 이양 없이는 새로 만든 창을 앞으로 못 올린다(OS 잠금).
                // 실패는 안에서 조용히 무시된다(주 프로세스 쪽 점멸 폴백으로 충분).
                Integration.ForegroundActivation.AllowNextForegroundChange(mainInstance.ProcessId);
                // 두 번째 실행 → 기존 인스턴스에 활성화 넘기고 즉시 종료
                mainInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait();
                return 0;
            }

            // 주의: 람다 매개변수를 "_"로 지으면 discard가 아닌 변수가 되어
            // 본문의 "_ = new App()"이 매개변수 대입으로 해석된다(CS0029).
            Microsoft.UI.Xaml.Application.Start(callbackParams =>
            {
                try
                {
                    var ctx = new DispatcherQueueSynchronizationContext(
                        DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(ctx);
                    _ = new App();
                }
                catch (Exception ex)
                {
                    LogFatal(ex, "Application.Start callback");
                    throw;
                }
            });
            return 0;
        }
        catch (Exception ex)
        {
            LogFatal(ex, "Main");
            return 1;
        }
    }

    /// <summary>시작 단계 치명 오류를 %TEMP%\KOTU 로그와 메시지 박스로 알린다. (여기서 또 죽지 않게 방어)</summary>
    internal static void LogFatal(Exception? ex, string stage)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] ({stage}) {ex}\n\n");
        }
        catch
        {
            // 로그조차 못 쓰는 상황이면 메시지 박스만 시도한다.
        }

        try
        {
            _ = MessageBoxW(
                IntPtr.Zero,
                $"{Branding.AppName} failed to start.\n\n{ex?.GetType().Name}: {ex?.Message}\n\nDetails: {LogPath}",
                $"{Branding.AppName} startup error",
                0x00000010 /* MB_ICONERROR */);
        }
        catch
        {
            // 마지막 안전망까지 실패하면 조용히 종료한다.
        }
    }

    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
