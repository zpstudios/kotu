using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace KOTU.Module.Document;

/// <summary>검사를 마친 HWP 스냅샷 한 개를 로컬 전용 페이지 뷰에 전달한다.</summary>
internal sealed class HwpPagePane(ContentControl host, Action requestEscape)
{
    private const string HostName = "kotu-hwp.invalid";
    private const string PageUrl = "https://" + HostName + "/index.html";
    private const int ChunkSize = 96 * 1024;
    private readonly TaskCompletionSource<bool> _firstPage = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebView2? _view;
    private byte[]? _source;
    private int _offset;
    private bool _closed;
    private bool _failed;
    public event Action? Failed;

    public async Task OpenAsync(byte[] source, CancellationToken cancel)
    {
        try
        {
            var folder = await Task.Run(() =>
            {
                var path = Path.Combine(AppContext.BaseDirectory, "HwpViewer");
                if (!File.Exists(Path.Combine(path, "index.html")))
                    path = Path.Combine(AppContext.BaseDirectory, "KOTU.Module.Document", "HwpViewer");
                return path;
            }, cancel);
            if (_closed) return;
            _source = source;
            // 독립 배포에서는 기존 HTML 뷰와 같은 임시 WebView 프로필을 사용한다.
            if (KOTU.Core.Integration.DistributionPolicy.IsStandalone)
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(Path.GetTempPath(), "WebView2"));
            _view = new WebView2();
            host.Content = _view;
            await _view.EnsureCoreWebView2Async().AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancel);
            if (_closed || cancel.IsCancellationRequested) return;
            var core = _view.CoreWebView2 ?? throw new InvalidOperationException("WebView2 unavailable.");
            var settings = core.Settings;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(HostName, folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += ResourceRequested;
            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += NavigationCompleted;
            core.DownloadStarting += DownloadStarting;
            core.NewWindowRequested += NewWindowRequested;
            core.PermissionRequested += PermissionRequested;
            core.ProcessFailed += ProcessFailed;
            core.WebMessageReceived += MessageReceived;
            core.Navigate(PageUrl);
            await _firstPage.Task.WaitAsync(TimeSpan.FromSeconds(90), cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception) { Fail(); }
    }

    private static void ResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var request = args.Request;
        if (request.Method == "GET" && Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == "https" && uri.Authority == HostName && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
                uri.AbsolutePath is "/index.html" or "/viewer.js" or "/render-worker.js" or "/engine/rhwp.js" or "/engine/rhwp_bg.wasm") return;
            if (args.ResourceContext == CoreWebView2WebResourceContext.Image &&
                request.Uri.StartsWith("blob:https://" + HostName + "/", StringComparison.Ordinal)) return;
        }
        args.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
    }

    private static void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) => args.Cancel = args.Uri != PageUrl;
    private void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) { if (!args.IsSuccess) Fail(); }
    private static void DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;
    private static void NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
    private static void PermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;
    private void ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args) => Fail();

    private void MessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_closed || _failed || args.Source != PageUrl) return;
        try
        {
            var json = args.WebMessageAsJson;
            if (json.Length > 128) throw new InvalidDataException();
            using var message = JsonDocument.Parse(json);
            var root = message.RootElement;
            switch (root.GetProperty("kind").GetString())
            {
                case "escape":
                    // 셸이 이 WebView를 닫을 수 있으므로 WebView2 콜백에서 나온 뒤 전달한다.
                    host.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_closed && !_failed && host.IsLoaded &&
                            host.Visibility == Microsoft.UI.Xaml.Visibility.Visible && ReferenceEquals(host.Content, _view))
                            requestEscape();
                    });
                    break;
                case "ready" when _source is not null && _offset == 0:
                    sender.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "open", length = _source.Length }));
                    break;
                case "read" when _source is not null && root.GetProperty("offset").GetInt32() == _offset:
                    var count = Math.Min(ChunkSize, _source.Length - _offset);
                    var chunk = Convert.ToBase64String(_source, _offset, count);
                    sender.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "chunk", offset = _offset, data = chunk }));
                    _offset += count;
                    if (_offset == _source.Length) _source = null;
                    break;
                case "shown" when _source is null:
                    _firstPage.TrySetResult(true);
                    break;
                default:
                    Fail();
                    break;
            }
        }
        catch (Exception) { Fail(); }
    }

    private void Fail()
    {
        if (_closed || _failed) return;
        _failed = true;
        _source = null;
        _firstPage.TrySetResult(false);
        Failed?.Invoke();
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _source = null;
        _firstPage.TrySetResult(false);
        if (_view?.CoreWebView2 is { } core)
        {
            core.WebResourceRequested -= ResourceRequested;
            core.NavigationStarting -= NavigationStarting;
            core.NavigationCompleted -= NavigationCompleted;
            core.DownloadStarting -= DownloadStarting;
            core.NewWindowRequested -= NewWindowRequested;
            core.PermissionRequested -= PermissionRequested;
            core.ProcessFailed -= ProcessFailed;
            core.WebMessageReceived -= MessageReceived;
        }
        host.Content = null;
        try { _view?.Close(); } catch (Exception) { /* 셸 종료 중 이미 해제될 수 있다. */ }
        _view = null;
    }
}
