using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinUtil.Core.Contracts;
using WinUtil.Core.Threading;

namespace WinUtil.Module.Archive;

/// <summary>ListView 한 행. x:Bind용 표시 전용 래퍼.</summary>
public sealed class ArchiveRow
{
    internal ArchiveRow(ArchiveEntryNode node) => Node = node;

    internal ArchiveEntryNode Node { get; }

    public string Name => Node.Name;

    public string Glyph => Node.IsDirectory ? "\uE8B7" : "\uE7C3"; // 폴더 / 문서 아이콘

    public string SizeText => ArchiveEntryTree.FormatSize(Node.Size);

    public string ModifiedText =>
        Node.Modified == default ? string.Empty : Node.Modified.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// 압축 화면. 내부 탐색(브레드크럼/더블클릭 진입/뒤로), 풀기(폴더 선택·여기에 풀기),
/// 새 압축(zip/7z, 드래그&amp;드롭 포함), 암호 재시도, 진행률/취소를 제공한다.
/// 모든 파일 I/O는 뷰 전용 워커(A42)에서 직렬로 수행하고 UI 스레드는 결과 반영만 한다 —
/// 창이 여러 개면 워커도 창마다 하나라 서로의 압축/해제를 기다리지 않는다.
/// </summary>
public sealed partial class ArchiveView : UserControl, WinUtil.Core.Contracts.IContentStateSource,
    IBottomBarProvider
{
    /// <summary>아카이브를 열면 셸에 알린다(v0.25.0 — 빈 상태 탐색기 내림·오버레이 기준 갱신).</summary>
    public event Action<string>? ContentOpened;

    /// <summary>
    /// 하단 상태바(열기·상태·진행·전체화면)를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다
    /// (v0.40.0 — 열기 버튼이 메뉴 버튼 바로 우측에 오게. 이미지 v0.27.0과 동일 패턴).
    /// 컨트롤 필드 참조는 그대로 유효하다.
    /// </summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(StatusBar);
        return StatusBar;
    }

    private readonly IArchiveBackend _backend = new SevenZipBackend();
    private readonly WinUtil.Core.Settings.ISettingsService _settings;
    private readonly string? _initialFile;
    private readonly IReadOnlyList<string> _initialArgs;
    private readonly Stack<ArchiveEntryNode> _navStack = new();

    private string? _archivePath;
    private string? _password;          // 마지막으로 성공/입력된 암호 (아카이브 단위로 유지)
    private ArchiveEntryNode? _root;
    private ArchiveEntryNode? _currentFolder;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _initialized;
    private ModuleWorker? _worker;      // 압축 목록/해제/생성 전용(A42) — 뷰별 분리

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU archive worker");

    public ObservableCollection<ArchiveRow> Rows { get; } = [];

    public ArchiveView(OpenContext context, WinUtil.Core.Settings.ISettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _initialFile = context.FilePath;
        _initialArgs = context.Arguments;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        if (_initialFile is not { } path || !File.Exists(path))
        {
            UpdateToolbarState();
            return;
        }

        // 탐색기 우클릭 동사 처리
        if (_initialArgs.Contains(WinUtil.Core.Cli.LaunchRequest.CompressToken))
        {
            // 대상은 압축 파일이 아니라 압축할 원본 — 목록을 읽지 말고 바로 새 압축 흐름으로.
            UpdateToolbarState();
            await StartCreateFlowAsync([path], use7z: null);
            return;
        }

        await LoadArchiveAsync(path);

        if (_initialArgs.Contains(WinUtil.Core.Cli.LaunchRequest.ExtractHereToken) && _root is not null)
            await ExtractHereAsync();
    }

    // ---------- 아카이브 열기 / 목록 ----------

    private async Task LoadArchiveAsync(string path)
    {
        if (_busy) return;
        _archivePath = path;
        _password = null;

        while (true)
        {
            try
            {
                var password = _password;
                IReadOnlyList<ArchiveEntry> entries = [];
                var ok = await RunOperationAsync("Reading archive...", (progress, _) =>
                {
                    entries = _backend.List(path, password);
                    progress.Report(1);
                });
                if (!ok) return;

                _root = ArchiveEntryTree.Build(entries);
                _navStack.Clear();
                _currentFolder = _root;
                RefreshRows();
                StatusText.Text = $"{Path.GetFileName(path)} · {ArchiveEntryTree.FormatSize(_root.Size)} total";
                UpdateDriveInfo(path); // v0.47.0 표시 — 조회는 워커에서(A42: UI 스레드 동기 I/O 제거)
                ContentOpened?.Invoke(path); // 셸 동기화 (v0.25.0)
                return;
            }
            catch (ArchivePasswordException)
            {
                var entered = await PromptPasswordAsync();
                if (entered is null) return; // 취소
                _password = entered;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to open: " + ex.Message;
                return;
            }
        }
    }

    /// <summary>
    /// 드라이브 정보 조회(DriveInfo — 네트워크 경로면 느릴 수 있다)를 워커로 보내고
    /// 결과만 UI에 반영한다. 부가 정보라 실패·지연은 조용히 무시.
    /// </summary>
    private async void UpdateDriveInfo(string path)
    {
        try
        {
            DriveInfoText.Text = await Worker.Run(_ => WinUtil.Core.Routing.DriveStatus.Describe(path));
        }
        catch
        {
            // 표시는 부가 기능 — 실패가 흐름을 막으면 안 된다.
        }
    }

    private void RefreshRows()
    {
        Rows.Clear();
        if (_currentFolder is not null)
        {
            foreach (var child in _currentFolder.Children)
                Rows.Add(new ArchiveRow(child));
        }
        PlaceholderText.Visibility = _root is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateBreadcrumb();
        UpdateToolbarState();
    }

    private void UpdateBreadcrumb()
    {
        if (_archivePath is null || _currentFolder is null)
        {
            BreadcrumbText.Text = string.Empty;
            return;
        }
        var name = Path.GetFileName(_archivePath);
        BreadcrumbText.Text = _currentFolder.FullPath.Length == 0
            ? name
            : name + " / " + _currentFolder.FullPath.Replace("/", " / ");
    }

    // ---------- 폴더 탐색 ----------

    private void EnterFolder(ArchiveEntryNode folder)
    {
        if (_currentFolder is null) return;
        _navStack.Push(_currentFolder);
        _currentFolder = folder;
        RefreshRows();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_navStack.Count == 0) return;
        _currentFolder = _navStack.Pop();
        RefreshRows();
    }

    private async void OnEntryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ArchiveRow row) return;
        e.Handled = true;

        if (row.Node.IsDirectory)
            EnterFolder(row.Node);
        else
            await OpenEntryExternallyAsync(row.Node);
    }

    /// <summary>파일 항목 더블클릭: 임시 폴더에 그 항목만 풀고 OS 기본 앱으로 연다.</summary>
    private async Task OpenEntryExternallyAsync(ArchiveEntryNode node)
    {
        if (_busy || _archivePath is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "KOTU", "Archive", Guid.NewGuid().ToString("N"));
        if (!await ExtractWithRetryAsync(tempDir, [node.FullPath], "Opening...")) return;

        var extracted = Path.Combine(tempDir, node.FullPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(extracted))
        {
            Process.Start(new ProcessStartInfo(extracted) { UseShellExecute = true });
            StatusText.Text = "Opened with the default app: " + node.Name;
        }
        else
        {
            StatusText.Text = "Extracted file not found: " + node.Name;
        }
    }

    // ---------- 해제 ----------

    private async void OnExtractToClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _archivePath is null) return;

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetHwnd());
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        // 마지막 풀기(저장) 위치를 설정에 기억 (v0.55.0 사용자 요청)
        _settings.Set("archive.lastExtractDir", folder.Path);
        _settings.Save();

        if (await ExtractWithRetryAsync(folder.Path, SelectedEntryPaths(), "Extracting..."))
        {
            StatusText.Text = "Extracted: " + folder.Path;
            OpenInExplorer(folder.Path);
        }
    }

    private async void OnExtractHereClick(object sender, RoutedEventArgs e) => await ExtractHereAsync();

    /// <summary>"여기에 풀기" 실행. 버튼과 탐색기 우클릭 동사가 공유한다.</summary>
    private async Task ExtractHereAsync()
    {
        if (_busy || _archivePath is null || _root is null) return;

        // 대상 폴더 결정: 단일 루트면 이중 폴더 방지, 이름이 겹치면 "(2)" 등 빈 이름 사용.
        var plan = ExtractHerePlanner.Plan(
            _archivePath,
            _root.Children.Select(c => c.Name).ToList(),
            p => Directory.Exists(p) || File.Exists(p));

        if (await ExtractWithRetryAsync(plan.TargetDirectory, entryPaths: null, "Extracting..."))
        {
            StatusText.Text = "Extracted: " + plan.ResultPath;
            OpenInExplorer(plan.ResultPath);
        }
    }

    /// <summary>풀기 결과를 탐색기로 보여준다. 실패해도 조용히 무시.</summary>
    private static void OpenInExplorer(string path)
    {
        try
        {
            // 결과가 파일이면 부모 폴더에서 해당 파일을 선택해 보여준다.
            var args = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch
        {
            // 탐색기 열기는 부가 기능 — 실패가 흐름을 막으면 안 된다.
        }
    }

    /// <summary>선택된 항목 경로 목록. 선택이 없으면 null(=전체).</summary>
    private IReadOnlyCollection<string>? SelectedEntryPaths()
    {
        var selected = EntryList.SelectedItems.OfType<ArchiveRow>()
            .Select(r => r.Node.FullPath)
            .ToList();
        return selected.Count > 0 ? selected : null;
    }

    /// <summary>해제 실행. 암호 오류면 입력을 받아 재시도한다. 성공 시 true.</summary>
    private async Task<bool> ExtractWithRetryAsync(
        string targetDirectory, IReadOnlyCollection<string>? entryPaths, string label)
    {
        while (true)
        {
            try
            {
                var password = _password;
                return await RunOperationAsync(label, (progress, ct) =>
                    _backend.Extract(_archivePath!, targetDirectory, entryPaths, password, progress, ct));
            }
            catch (ArchivePasswordException)
            {
                var entered = await PromptPasswordAsync();
                if (entered is null) return false;
                _password = entered;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Extract failed: " + ex.Message;
                return false;
            }
        }
    }

    // ---------- 새 압축 ----------

    private async void OnCreateZipClick(object sender, RoutedEventArgs e) =>
        await PickSourcesAndCreateAsync(use7z: false);

    private async void OnCreate7zClick(object sender, RoutedEventArgs e) =>
        await PickSourcesAndCreateAsync(use7z: true);

    private async Task PickSourcesAndCreateAsync(bool use7z)
    {
        if (_busy) return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetHwnd());
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        await StartCreateFlowAsync(files.Select(f => f.Path).ToList(), use7z);
    }

    /// <summary>
    /// 새 압축 공통 흐름: 대화상자 하나에서 대상 목록 확인 + 형식/암호 + 저장 위치까지 정하고 생성한다.
    /// 저장 위치 기본값은 원본과 같은 폴더이며, 같은 이름이 있으면 "(2)"를 붙인다.
    /// </summary>
    private async Task StartCreateFlowAsync(IReadOnlyList<string> sourcePaths, bool? use7z)
    {
        if (_busy || sourcePaths.Count == 0) return;

        var options = await PromptCreateOptionsAsync(use7z, sourcePaths);
        if (options is null) return;
        var (sevenZ, password, targetPath) = options.Value;

        try
        {
            var ok = await RunOperationAsync("Compressing...", (progress, ct) =>
            {
                if (sevenZ) _backend.Create7z(sourcePaths, targetPath, password, progress, ct);
                else _backend.CreateZip(sourcePaths, targetPath, password, progress, ct);
            });
            if (ok)
            {
                StatusText.Text = "Archive created: " + targetPath;
                OpenInExplorer(targetPath); // 결과 파일을 탐색기에서 선택해 보여준다
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Compress failed: " + ex.Message;
        }
    }

    // ---------- 드래그&드롭: 외부 파일을 떨어뜨리면 새 압축 흐름 ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true; // 압축 뷰의 드롭은 '새 압축' — 창 수준 파일 라우팅으로 넘기지 않는다.
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true; // 창 수준 라우팅과의 이중 처리 방지 (await 전에 동기로 지정해야 유효)
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;

        await StartCreateFlowAsync(paths, use7z: null); // 형식은 대화상자에서 선택
    }

    // ---------- 대화상자 ----------

    /// <summary>암호 입력 대화상자. 취소하면 null.</summary>
    private async Task<string?> PromptPasswordAsync()
    {
        var box = new PasswordBox();
        var dialog = new ContentDialog
        {
            Title = "Password required",
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && box.Password.Length > 0 ? box.Password : null;
    }

    /// <summary>
    /// 새 압축 옵션 대화상자: 무엇을(대상 목록), 어떤 형식/암호로, 어디에(저장 위치) 만드는지
    /// 전부 이 자리에서 보여주고 정한다. 취소하면 null.
    /// </summary>
    private async Task<(bool Use7z, string? Password, string TargetPath)?> PromptCreateOptionsAsync(
        bool? use7z, IReadOnlyList<string> sourcePaths)
    {
        var saveDir = Path.GetDirectoryName(sourcePaths[0]);
        if (string.IsNullOrEmpty(saveDir)) saveDir = ".";
        var baseName = Path.GetFileNameWithoutExtension(sourcePaths[0]);
        var existsCheck = (Func<string, bool>)(p => File.Exists(p) || Directory.Exists(p));

        var sourceList = new ItemsControl
        {
            ItemsSource = sourcePaths.Select(Path.GetFileName).ToList(),
        };
        var sourcePanel = new StackPanel { Spacing = 4 };
        sourcePanel.Children.Add(new TextBlock
        {
            Text = $"{sourcePaths.Count} item(s)",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        sourcePanel.Children.Add(new ScrollViewer
        {
            Content = sourceList,
            MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var formatBox = new ComboBox
        {
            Header = "Format",
            ItemsSource = new[] { "zip", "7z" },
            SelectedIndex = use7z == true ? 1 : 0,
        };
        var passwordBox = new PasswordBox { Header = "Password (optional)" };

        var locationText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };
        string CurrentTarget()
        {
            var ext = formatBox.SelectedIndex == 1 ? ".7z" : ".zip";
            return ExtractHerePlanner.UniquePath(Path.Combine(saveDir!, baseName + ext), existsCheck);
        }
        void UpdateLocationText() => locationText.Text = "Save to: " + CurrentTarget();
        UpdateLocationText();
        formatBox.SelectionChanged += (_, _) => UpdateLocationText();

        var changeLocation = new Button { Content = "Change location..." };
        changeLocation.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetHwnd());
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                saveDir = folder.Path;
                UpdateLocationText();
            }
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(sourcePanel);
        panel.Children.Add(formatBox);
        panel.Children.Add(passwordBox);
        panel.Children.Add(locationText);
        panel.Children.Add(changeLocation);

        var dialog = new ContentDialog
        {
            Title = "New archive",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var password = passwordBox.Password.Length > 0 ? passwordBox.Password : null;
        return (formatBox.SelectedIndex == 1, password, CurrentTarget());
    }

    // ---------- 공통: 백그라운드 실행 / 진행률 / 취소 ----------

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>
    /// 작업을 뷰 전용 워커에서 실행하고 진행률/취소를 연결한다(A42 계약: Run+WorkContext).
    /// 취소되면 false. ArchivePasswordException 등은 호출자에게 그대로 전파된다.
    /// </summary>
    private async Task<bool> RunOperationAsync(string label, Action<IProgress<double>, CancellationToken> work)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        var dispatcher = DispatcherQueue;
        var progress = new DelegateProgress(v =>
            dispatcher.TryEnqueue(() => OperationProgress.Value = Math.Clamp(v, 0, 1) * 100));

        SetBusy(true, label);
        try
        {
            await Worker.Run(ctx => work(ctx.Progress, ctx.Cancellation), cts.Token, progress);
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Canceled";
            return false;
        }
        finally
        {
            SetBusy(false, null);
            _cts = null;
            cts.Dispose();
        }
    }

    private void SetBusy(bool busy, string? label)
    {
        _busy = busy;
        OperationProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            OperationProgress.Value = 0;
            if (label is not null) StatusText.Text = label;
        }
        UpdateToolbarState();
    }

    private void UpdateToolbarState()
    {
        OpenButton.IsEnabled = !_busy;
        CreateButton.IsEnabled = !_busy;
        var hasArchive = !_busy && _root is not null;
        ExtractToButton.IsEnabled = hasArchive;
        ExtractHereButton.IsEnabled = hasArchive;
        BackButton.IsEnabled = !_busy && _navStack.Count > 0;
    }

    // ---------- 열기 버튼 / 창 핸들 ----------

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        foreach (var ext in ArchiveModule.Extensions) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetHwnd());
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await LoadArchiveAsync(file.Path);
    }

    /// <summary>피커 초기화용 창 핸들. Window 객체 없이 XamlRoot 경유로 얻는다.</summary>
    private nint GetHwnd()
    {
        var environment = XamlRoot?.ContentIslandEnvironment
            ?? throw new InvalidOperationException("Cannot determine the window handle.");
        return Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
    }

    // ---------- 전체화면 (v0.40.0 — 이미지·동영상과 동일 패턴) ----------

    private void ToggleFullScreen()
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;

        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(environment.AppWindowId);
        appWindow.SetPresenter(appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
            ? Microsoft.UI.Windowing.AppWindowPresenterKind.Default
            : Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }

    private void OnFullScreenButtonClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnFullScreenInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullScreen();
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(environment.AppWindowId);
        if (appWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen) return;

        args.Handled = true;
        appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
    }

    /// <summary>SynchronizationContext에 의존하지 않는 IProgress 구현(명시적 디스패처 마샬링용).</summary>
    private sealed class DelegateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
