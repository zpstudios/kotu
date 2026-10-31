using System.Text.Json;
using KOTU.DocumentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace KOTU.Module.Document;

/// <summary>로컬 편집기 세션. 문서 버퍼만 소유하며 네이티브 TextBox를 복제하지 않는다.</summary>
internal sealed partial class VirtualEditorPane : UserControl
{
    private const string Origin = "https://kotu-editor.invalid";
    private readonly EditorRequestRegistry _requests;
    private readonly EditorMessageAssembler _messages;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _layout = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebView2? _view;
    private readonly EditorSurfaceLifetime _surfaceLifetime = new();
    private bool _closed => _surfaceLifetime.IsReleased;
    private bool _failed => _surfaceLifetime.IsFailed;
    private readonly EditorFreezeOwnership _freeze = new();
    public VirtualDocumentBuffer Buffer { get; }
    public bool IsReady { get; private set; }
    public bool HasActivated { get; private set; }
    public bool HasAcceptedEdits => Buffer.Version > 1;
    public bool IsFrozen => _freeze.Owner is not null;
    public long? FreezeOwner => _freeze.Owner;
    public bool IsFailed => _failed;
    public readonly record struct EditorViewport(double Top, double Left, double Width, double Height);
    public EditorViewport Viewport { get; private set; }
    public long SelectionGeneration { get; private set; }
    public event Action<IReadOnlyList<EditorDelta>>? Changed;
    public event Action? StateChanged;
    public event Action<string>? Shortcut;
    public event Action<int>? ZoomRequested;
    public event Action<string>? Failed;
    public event Action<string>? ActionFailed;

    public VirtualEditorPane(string text)
    {
        Buffer = new(text);
        _requests = new(Buffer.Epoch);
        _messages = new(Buffer.Epoch);
        Tag = KOTU.Input.HotkeySupport.VirtualTextInputTag;
        IsTabStop = false;
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => _surfaceLifetime.Attach();
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _surfaceLifetime.Detach();
        _editMenu?.Hide();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (ActualWidth > 0 && ActualHeight > 0) _layout.TrySetResult(true);
    }

    public async Task<bool> LoadAsync(bool readOnly, object visual)
    {
        try
        {
            var assets = await Task.Run(() =>
            {
                // SDK publish와 WinUI 참조 콘텐츠 복사는 서로 다른 고정 배치 경로를 사용한다.
                var folder = Path.Combine(AppContext.BaseDirectory, "VirtualEditor");
                if (!Directory.Exists(folder)) folder = Path.Combine(AppContext.BaseDirectory, "KOTU.Module.Document", "VirtualEditor");
                foreach (var name in new[] { "index.html", "editor.js", "editor.css", "editor.worker.js", "codicon.ttf" })
                    if (!File.Exists(Path.Combine(folder, name))) throw new FileNotFoundException("The local editor files are incomplete.");
                return folder;
            }, _lifetime.Token);
            if (_closed) return false;
            if (ActualWidth > 0 && ActualHeight > 0) _layout.TrySetResult(true);
            await _layout.Task.WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token);
            _view = new WebView2 { DefaultBackgroundColor = Microsoft.UI.Colors.Transparent,
                Tag = KOTU.Input.HotkeySupport.VirtualTextInputTag };
            Content = _view;
            await _view.EnsureCoreWebView2Async();
            if (_closed || _view?.CoreWebView2 is not { } core) return false;
            var settings = core.Settings;
            settings.IsScriptEnabled = true;
            settings.IsWebMessageEnabled = true;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.AreHostObjectsAllowed = false;
            core.SetVirtualHostNameToFolderMapping("kotu-editor.invalid", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += OnResourceRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindow;
            core.PermissionRequested += OnPermission;
            core.DownloadStarting += OnDownload;
            core.ProcessFailed += OnProcessFailed;
            core.WebMessageReceived += OnMessage;
            core.Navigate(Origin + "/index.html#" + Buffer.Epoch);
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token);
            Post(new { kind = "load", epoch = Buffer.Epoch, text = Buffer.Text, readOnly, visual });
            await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token);
            IsReady = !_closed && !_failed;
            if (IsReady)
            {
                Post(new { kind = "activate", epoch = Buffer.Epoch });
                HasActivated = true;
            }
            return IsReady;
        }
        catch (Exception ex) { if (!_closed) Fail(ex.Message); return false; }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) =>
        args.Cancel = args.Uri != Origin + "/index.html#" + Buffer.Epoch;
    private void OnNewWindow(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
    private void OnPermission(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;
    private void OnDownload(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;
    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args) => Fail("The editor process stopped. Keep this document open; the received text is retained for recovery.");
    private static bool IsPage(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "kotu-editor.invalid" && uri.IsDefaultPort &&
        uri.AbsolutePath == "/index.html" && uri.Query.Length == 0 && uri.UserInfo.Length == 0;
    private void OnResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var allowed = Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host == "kotu-editor.invalid" && uri.IsDefaultPort &&
            uri.Query.Length == 0 && uri.UserInfo.Length == 0 &&
            uri.AbsolutePath is "/index.html" or "/editor.js" or "/editor.css" or "/editor.worker.js" or "/codicon.ttf";
        if (!allowed || args.Request.Method != "GET")
            args.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
    }

    private void Post(object message)
    {
        if (_closed || _failed || _view?.CoreWebView2 is not { } core)
            throw new InvalidOperationException("The editor is unavailable. Keep this document open and retry.");
        core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_closed || _failed || !IsPage(args.Source)) return;
        try
        {
            var json = args.WebMessageAsJson;
            if (json.Length > VirtualDocumentBuffer.MaxMessageChars) throw new InvalidDataException("Editor message limit exceeded.");
            ProcessMessage(json);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void ProcessMessage(string json)
    {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("epoch").GetString() != Buffer.Epoch) return;
            var kind = root.GetProperty("kind").GetString();
            if (kind == "frame")
            {
                var complete = _messages.Accept(Buffer.Epoch, root.GetProperty("transfer").GetInt64(), root.GetProperty("index").GetInt32(), root.GetProperty("total").GetInt32(), root.GetProperty("data").GetString()!);
                if (complete is not null) ProcessMessage(complete);
                return;
            }
            if (kind == "ready") { _ready.TrySetResult(true); return; }
            if (kind == "loaded")
            {
                Buffer.VerifySnapshot(Buffer.Epoch, root.GetProperty("version").GetInt32(), root.GetProperty("text").GetString()!, false);
                if (root.GetProperty("width").GetDouble() <= 0 || root.GetProperty("height").GetDouble() <= 0)
                    throw new InvalidDataException("The editor has no visible layout.");
                _loaded.TrySetResult(true);
                return;
            }
            if (!_loaded.Task.IsCompletedSuccessfully) return;
            var version = root.GetProperty("version").GetInt32();
            if (kind == "change")
            {
                var items = root.GetProperty("changes");
                if (items.GetArrayLength() > VirtualDocumentBuffer.MaxChanges) throw new InvalidDataException("Too many editor changes.");
                var changes = items.EnumerateArray().Select(c => new EditorDelta(c.GetProperty("offset").GetInt32(), c.GetProperty("length").GetInt32(), c.GetProperty("text").GetString()!)).ToArray();
                Buffer.Apply(Buffer.Epoch, version, changes);
                Changed?.Invoke(changes);
            }
            else if (kind == "state")
            {
                var selectionGeneration = root.GetProperty("selectionGeneration").GetInt64();
                if (selectionGeneration < SelectionGeneration) throw new InvalidDataException("Invalid selection generation.");
                SelectionGeneration = selectionGeneration;
                Buffer.SetState(Buffer.Epoch, version, new(root.GetProperty("anchor").GetInt32(), root.GetProperty("position").GetInt32(), root.GetProperty("line").GetInt32(), root.GetProperty("column").GetInt32()), root.GetProperty("composing").GetBoolean());
                var viewport = root.GetProperty("viewport");
                Viewport = new(viewport.GetProperty("top").GetDouble(), viewport.GetProperty("left").GetDouble(), viewport.GetProperty("width").GetDouble(), viewport.GetProperty("height").GetDouble());
                StateChanged?.Invoke();
            }
            else if (kind == "zoom")
            {
                var steps = root.GetProperty("steps").GetInt32();
                if (steps is >= -100 and <= 100) ZoomRequested?.Invoke(steps);
            }
            else if (kind == "shortcut") Shortcut?.Invoke(root.GetProperty("key").GetString()!);
            else if (kind == "context") ShowEditMenu(root);
            else if (kind == "clipboard") _ = HandleClipboardAsync(root.Clone());
            else if (kind == "response")
            {
                var id = root.GetProperty("id").GetInt64();
                if (!_requests.TryGet(Buffer.Epoch, id, out var request) || request is null) return;
                if (root.TryGetProperty("error", out var error))
                { _requests.Complete(Buffer.Epoch, id, version, null, error.GetString()); return; }
                EditorSnapshot? snapshot = null;
                if (request.Kind is "snapshot" or "freeze")
                {
                    snapshot = Buffer.VerifySnapshot(Buffer.Epoch, version, root.GetProperty("text").GetString()!, root.GetProperty("frozen").GetBoolean());
                    if (request.Kind == "freeze" && !snapshot.Frozen) throw new InvalidDataException("Editor did not freeze.");
                }
                _requests.Complete(Buffer.Epoch, id, version, snapshot);
            }
    }

    private async Task<EditorSnapshot?> RequestAsync(string kind, object? payload, int expectedVersion, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(20), linked.Token);
        var request = _requests.Begin(kind, expectedVersion);
        try
        {
            Post(new { kind, epoch = Buffer.Epoch, id = request.Id, expectedVersion, payload });
            var snapshot = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
            if (kind == "freeze") _freeze.Acquire(request.Id);
            return snapshot;
        }
        catch
        {
            // 실패 응답이 늦어도 브라우저의 같은 freeze 소유권을 반드시 해제한다.
            if (kind == "freeze" && !_closed && !_failed)
                Post(new { kind = "release", epoch = Buffer.Epoch, freezeId = request.Id });
            throw;
        }
        finally { _requests.Abandon(request); }
    }

    public async Task<EditorSnapshot> SnapshotAsync(bool freeze, CancellationToken cancellationToken = default) =>
        await RequestAsync(freeze ? "freeze" : "snapshot", null, Buffer.Version, cancellationToken)
        ?? throw new InvalidDataException("The editor returned no snapshot.");

    public async Task ReleaseFreezeAsync(long owner, CancellationToken cancellationToken = default)
    {
        if (!_freeze.Owns(owner)) return;
        await RequestAsync("thaw", new { freezeId = owner }, Buffer.Version, cancellationToken);
        _freeze.Release(owner);
    }

    // 선택/치환은 정확한 버전을 요구한다. 옵션과 포커스는 본문을 변경하지 않는다.
    public Task<EditorSnapshot?> SelectAsync(int anchor, int position, bool reveal, int version, CancellationToken token = default) =>
        RequestAsync("select", new { anchor, position, reveal, selectionGeneration = SelectionGeneration }, version, token);
    public Task<EditorSnapshot?> ReplaceAsync(IReadOnlyList<EditorDelta> edits, int version, CancellationToken token = default,
        int? selectionEnd = null, EditorSelection? expectedSelection = null) =>
        RequestAsync("replace", new { edits = edits.Select(e => new { offset = e.Offset, length = e.Length, text = e.Text }).ToArray(),
            selectionEnd, expectedSelection = expectedSelection is { } selection ? new { anchor = selection.Anchor, position = selection.Position } : null }, version, token);
    public Task<EditorSnapshot?> OptionsAsync(object options, CancellationToken token = default) => RequestAsync("options", options, Buffer.Version, token);
    public Task<EditorSnapshot?> FocusEditorAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        FocusNativeEditor();
        return RequestAsync("focus", null, Buffer.Version, token);
    }

    private void FocusNativeEditor()
    {
        if (!IsReady || _closed || _failed || !_surfaceLifetime.IsAttached || Visibility != Visibility.Visible ||
            _view is not { IsLoaded: true } view || !ReferenceEquals(Content, view) ||
            XamlRoot is null || !ReferenceEquals(view.XamlRoot, XamlRoot))
            throw new OperationCanceledException("The editor is no longer available for focus.");
        // DOM focus does not transfer the native keyboard focus from a WinUI Find field.
        if (!view.Focus(FocusState.Programmatic))
            throw new InvalidOperationException("The editor could not receive keyboard focus. Click the document and retry.");
    }

    private void Fail(string reason)
    {
        if (!_surfaceLifetime.Fail()) return;
        IsReady = false;
        _ready.TrySetException(new InvalidOperationException(reason));
        _loaded.TrySetException(new InvalidOperationException(reason));
        _requests.Fail(new InvalidOperationException(reason));
        _messages.Clear();
        Failed?.Invoke(reason);
    }

    public void MarkFailed(string reason) => Fail(reason);

    public void Close()
    {
        if (!_surfaceLifetime.Release()) return;
        IsReady = false;
        _editMenu?.Hide();
        _editMenu = null;
        _lifetime.Cancel();
        _requests.Dispose();
        _messages.Clear();
        SizeChanged -= OnSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        var view = _view;
        _view = null;
        if (view?.CoreWebView2 is { } core)
        {
            core.WebResourceRequested -= OnResourceRequested;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindow;
            core.PermissionRequested -= OnPermission;
            core.DownloadStarting -= OnDownload;
            core.ProcessFailed -= OnProcessFailed;
            core.WebMessageReceived -= OnMessage;
        }
        Content = null;
        try { view?.Close(); }
        catch (Exception) { /* 종료 중 컨트롤 정리 실패가 셸을 막지 않게 한다. */ }
        _lifetime.Dispose();
    }
}
