#if KOTU_THUMBNAIL_TEST_HARNESS
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using System.Text.Json;

namespace KOTU.App.Integration;

// 명시 컴파일에서만 존재한다. App/Program/설정/인스턴스 리디렉션을 실행하지 않는다.
internal static class ThumbnailRetryHarness
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var output = Path.GetFullPath(args[0]);
        void Trace(string message) => File.AppendAllText(output + ".trace.log", DateTimeOffset.Now + " " + message + Environment.NewLine);
        try
        {
            Trace("Main");
            AppDomain.CurrentDomain.UnhandledException += (_, error) => Trace("AppDomain " + error.ExceptionObject);
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Trace("ComWrappers ready");
            Application.Start(callback =>
            {
                try
                {
                    Trace("Application.Start callback");
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                    _ = new HarnessApp(output, Trace);
                    Trace("HarnessApp constructed");
                }
                catch (Exception ex)
                {
                    Trace("Application.Start failure " + ex);
                    File.WriteAllText(output, JsonSerializer.Serialize(new { FakeProvider = true, Passed = false, Error = ex.ToString() }));
                    Environment.Exit(1);
                }
            });
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { FakeProvider = true, Passed = false, Error = ex.ToString() }));
            return 1;
        }
    }

    private sealed class HarnessApp : Application, IXamlMetadataProvider
    {
        private readonly string _output;
        private readonly Action<string> _trace;
        // XAML 1차 컴파일에는 이 생성 형식이 아직 없다. 실행 시 같은 어셈블리의 제공자만 만든다.
        private readonly IXamlMetadataProvider _metadata = (IXamlMetadataProvider)Activator.CreateInstance(
            typeof(ThumbnailRetryHarness).Assembly.GetType("KOTU.App.KOTU_App_XamlTypeInfo.XamlMetaDataProvider", throwOnError: true)!)!;
        private Window? _window;
        public IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);
        public IXamlType GetXamlType(string fullName) => _metadata.GetXamlType(fullName);
        public XmlnsDefinition[] GetXmlnsDefinitions() => _metadata.GetXmlnsDefinitions();
        public HarnessApp(string output, Action<string> trace)
        {
            _output = output;
            _trace = trace;
            UnhandledException += (_, error) =>
            {
                _trace("WinUI UnhandledException " + error.Message + " " + error.Exception);
                File.WriteAllText(_output, JsonSerializer.Serialize(new { FakeProvider = true, Passed = false, Error = error.Exception.ToString() }));
            };
        }
        private void InitializeResources()
        {
            _trace("OnLaunched resources begin");
            Resources = new ResourceDictionary();
            _trace("Application resource dictionary assigned");
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            _trace("XamlControlsResources ready");
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 32d));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 32d));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(4)));
            Resources["BottomBarButtonStyle"] = style;
            _trace("HarnessApp resources ready");
        }
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                _trace("OnLaunched");
                InitializeResources();
                _window = new Window { Title = "A399 conditional retry — fake provider harness" };
                _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 900));
                _window.Activate();
                _trace("Window activated");
                var report = await Controls.ThumbnailExplorer.RunHarnessAsync(_window);
                File.WriteAllText(_output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(_output, JsonSerializer.Serialize(new { FakeProvider = true, Passed = false, Error = ex.ToString() },
                    new JsonSerializerOptions { WriteIndented = true }));
                Environment.ExitCode = 1;
            }
            finally { _window?.Close(); Exit(); }
        }
    }
}
#endif
