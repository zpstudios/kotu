using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace WinUtil.App;

/// <summary>
/// 진입점. 단일 인스턴스를 보장한다:
/// 탐색기에서 파일을 여러 번 열어도 새 창을 띄우지 않고
/// 기존 인스턴스로 활성화 인자를 넘긴다(파일 라우팅의 전제).
/// </summary>
public static class Program
{
    private const string InstanceKey = "WinUtil-Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        if (!mainInstance.IsCurrent)
        {
            // 두 번째 실행 → 기존 인스턴스에 활성화 넘기고 즉시 종료
            mainInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait();
            return 0;
        }

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }
}
