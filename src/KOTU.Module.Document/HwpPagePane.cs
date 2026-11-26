using System.Text.Json;
using KOTU.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace KOTU.Module.Document;

/// <summary>검사를 마친 HWP 스냅샷 한 개를 로컬 전용 페이지 뷰에 전달한다.</summary>
internal sealed class HwpPagePane(ContentControl host, Action requestEscape) : IReadOnlySearchSource
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
    private FrameworkElement? _bottomOverlay;
    public event Action? Failed;
    public event Action? StateChanged;
    public event Action<TextSearchCommand>? SearchCommand;
    public event Action? SearchBodyInteraction;
    private int _searchRequest;
    private int _searchResultRequest;
    private int _selectionRequest;
    private readonly Dictionary<int, TaskCompletionSource<ReadOnlySearchSummary>> _searchReplies = [];
    private readonly Dictionary<int, TaskCompletionSource<bool>> _selectionReplies = [];
    public int PageIndex { get; private set; }
    public int PageCount { get; private set; }
    public bool IsPagePending { get; private set; }
    public PdfFitMode FitMode { get; private set; } = PdfFitMode.Contain;

    public void SetBottomOverlay(object overlay) => _bottomOverlay = overlay as FrameworkElement;

    private void UpdateViewport(object? sender, object args)
    {
        if (_view is null || host.Visibility != Visibility.Visible) return;
        var height = host.ActualHeight;
        var bottom = height - PageViewport.VisibleHeight(host, host.ActualWidth, height, _bottomOverlay);
        if (Math.Abs(_view.Margin.Bottom - bottom) > 0.01)
            _view.Margin = new Thickness(0, 0, 0, bottom);
    }

    public void ApplyFit(PdfFitMode mode)
    {
        if (PageCount == 0 || _closed || _failed) return;
        FitMode = mode;
        Send(new { kind = "fit", mode = mode.ToString() });
        StateChanged?.Invoke();
    }

    public void ReapplyFit() => ApplyFit(FitMode);

    public void MovePage(int step)
    {
        var target = PageIndex + step;
        if (IsPagePending || target < 0 || target >= PageCount || _closed || _failed) return;
        IsPagePending = true;
        StateChanged?.Invoke();
        Send(new { kind = "page", index = target });
    }

    private void Send<T>(T message)
    {
        try { _view?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception) { Fail(); }
    }

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
            host.LayoutUpdated += UpdateViewport;
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
            if (json.Length > 512) throw new InvalidDataException();
            using var message = JsonDocument.Parse(json);
            var root = message.RootElement;
            switch (root.GetProperty("kind").GetString())
            {
                case "find":
                    DispatchSearchCommand(TextSearchCommand.Find);
                    break;
                case "find-options":
                    DispatchSearchCommand(TextSearchCommand.Replace);
                    break;
                case "find-next":
                    DispatchSearchCommand(TextSearchCommand.Next);
                    break;
                case "find-previous":
                    DispatchSearchCommand(TextSearchCommand.Previous);
                    break;
                case "search-escape-released":
                    DispatchSearchCommand(TextSearchCommand.EscapeReleased);
                    break;
                case "search-body-interaction":
                    host.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_closed && !_failed && host.IsLoaded && host.Visibility == Visibility.Visible &&
                            ReferenceEquals(host.Content, _view)) SearchBodyInteraction?.Invoke();
                    });
                    break;
                case "search-result":
                    var request = root.GetProperty("id").GetInt32();
                    if (!_searchReplies.Remove(request, out var reply)) break;
                    if (root.TryGetProperty("error", out var error) && error.GetBoolean())
                        reply.TrySetException(new InvalidOperationException("HWP search unavailable."));
                    else
                    {
                        var found = root.GetProperty("count").GetInt32();
                        if (found is < 0 or > 10000) { reply.TrySetException(new InvalidDataException()); break; }
                        reply.TrySetResult(new(found, root.GetProperty("limited").GetBoolean()));
                    }
                    break;
                case "search-selected":
                    var selection = root.GetProperty("selection").GetInt32();
                    if (_selectionReplies.Remove(selection, out var selected))
                    {
                        if (root.TryGetProperty("error", out var selectionError) && selectionError.GetBoolean())
                            selected.TrySetException(new InvalidOperationException("HWP match position unavailable."));
                        else selected.TrySetResult(true);
                    }
                    break;
                case "escape":
                    // 셸이 이 WebView를 닫을 수 있으므로 WebView2 콜백에서 나온 뒤 전달한다.
                    host.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_closed && !_failed && host.IsLoaded &&
                            host.Visibility == Microsoft.UI.Xaml.Visibility.Visible && ReferenceEquals(host.Content, _view))
                            requestEscape();
                    });
                    break;
                case "previous":
                    MovePage(-1);
                    break;
                case "next":
                    MovePage(1);
                    break;
                case "original":
                    ApplyFit(PdfFitMode.ActualSize);
                    break;
                case "reapply":
                    ReapplyFit();
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
                    var index = root.GetProperty("index").GetInt32();
                    var pages = root.GetProperty("count").GetInt32();
                    if (pages is < 1 or > 10000 || index < 0 || index >= pages ||
                        (PageCount > 0 && pages != PageCount)) throw new InvalidDataException();
                    PageIndex = index;
                    PageCount = pages;
                    IsPagePending = false;
                    StateChanged?.Invoke();
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
        CancelSearchReplies();
        _firstPage.TrySetResult(false);
        Failed?.Invoke();
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _source = null;
        CancelSearchReplies();
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
        host.LayoutUpdated -= UpdateViewport;
        host.Content = null;
        try { _view?.Close(); } catch (Exception) { /* 셸 종료 중 이미 해제될 수 있다. */ }
        _view = null;
    }

    private void DispatchSearchCommand(TextSearchCommand command) => host.DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closed && !_failed && host.IsLoaded && host.Visibility == Visibility.Visible && ReferenceEquals(host.Content, _view))
            SearchCommand?.Invoke(command);
    });

    private bool SendSearch<T>(T message)
    {
        if (_closed || _failed || _view?.CoreWebView2 is not { } core) return false;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); return true; }
        catch { return false; } // Search failure must not discard a still-readable page image.
    }

    public async Task<ReadOnlySearchSummary> SearchAsync(string query, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var id = ++_searchRequest;
        var reply = new TaskCompletionSource<ReadOnlySearchSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        _searchReplies.Add(id, reply);
        try
        {
            if (!SendSearch(new { kind = "search", id, query })) throw new InvalidOperationException();
            // The WASM call cannot be interrupted without destroying its render model. Wait for its
            // reply before submitting the next query; the viewer also owns a single latest mailbox.
            var result = await reply.Task.WaitAsync(TimeSpan.FromSeconds(35));
            cancellation.ThrowIfCancellationRequested();
            if (_closed || id != _searchRequest) throw new OperationCanceledException();
            _searchResultRequest = id;
            return result;
        }
        finally { _searchReplies.Remove(id); }
    }

    public async Task SelectMatchAsync(int index, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var id = _searchResultRequest;
        if (id == 0) return;
        var selection = ++_selectionRequest;
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _selectionReplies.Add(selection, reply);
        using var registration = cancellation.Register(() => host.DispatcherQueue.TryEnqueue(() =>
            SendSearch(new { kind = "cancel-selection", selection })));
        try
        {
            if (!SendSearch(new { kind = "select", id, selection, index })) throw new InvalidOperationException();
            await reply.Task.WaitAsync(TimeSpan.FromSeconds(35));
            cancellation.ThrowIfCancellationRequested();
        }
        finally { _selectionReplies.Remove(selection); }
    }

    public void ClearSearch()
    {
        _searchResultRequest = 0;
        SendSearch(new { kind = "clear-search", id = ++_searchRequest });
    }

    public void FocusDocument() => _view?.Focus(FocusState.Programmatic);

    private void CancelSearchReplies()
    {
        foreach (var reply in _searchReplies.Values) reply.TrySetCanceled();
        foreach (var reply in _selectionReplies.Values) reply.TrySetCanceled();
        _searchReplies.Clear();
        _selectionReplies.Clear();
    }
}
