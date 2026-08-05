using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace WinUtil.App;

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
    private const string InstanceKey = "WinUtil-Main";

    /// <summary>시작 실패 로그 경로: %TEMP%\WinUtil\startup-error.log</summary>
    private static string LogPath =>
        Path.Combine(Path.GetTempPath(), "WinUtil", "startup-error.log");

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => LogFatal(e.ExceptionObject as Exception, "AppDomain");

        try
        {
            // Velopack 훅: 설치/업데이트/제거 시 넘어오는 특수 인자를 처리한다.
            // (해당 인자면 여기서 프로세스가 종료되므로 반드시 가장 먼저 호출)
            Velopack.VelopackApp.Build().Run();

            WinRT.ComWrappersSupport.InitializeComWrappers();

            var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

            if (!mainInstance.IsCurrent)
            {
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
                    LogFatal(ex, "Application.Start 콜백");
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

    /// <summary>시작 단계 치명 오류를 %TEMP%\WinUtil 로그와 메시지 박스로 알린다. (여기서 또 죽지 않게 방어)</summary>
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
                $"WinUtil 시작 중 오류가 발생했습니다.\n\n{ex?.GetType().Name}: {ex?.Message}\n\n자세한 내용: {LogPath}",
                "WinUtil 시작 오류",
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
