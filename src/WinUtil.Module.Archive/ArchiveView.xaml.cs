using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinUtil.Core.Contracts;

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
/// 모든 파일 I/O는 Task.Run으로 UI 스레드 밖에서 수행한다.
/// </summary>
public sealed partial class ArchiveView : UserControl
{
    private readonly IArchiveBackend _backend = new SevenZipBackend();
    private readonly string? _initialFile;
    private readonly Stack<ArchiveEntryNode> _navStack = new();

    private string? _archivePath;
    private string? _password;          // 마지막으로 성공/입력된 암호 (아카이브 단위로 유지)
    private ArchiveEntryNode? _root;
    private ArchiveEntryNode? _currentFolder;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _initialized;

    public ObservableCollection<ArchiveRow> Rows { get; } = [];

    public ArchiveView(OpenContext context)
    {
        InitializeComponent();
        _initialFile = context.FilePath;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        if (_initialFile is { } path && File.Exists(path))
            await LoadArchiveAsync(path);
        else
            UpdateToolbarState();
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
                var ok = await RunOperationAsync("목록 읽는 중...", (progress, _) =>
                {
                    entries = _backend.List(path, password);
                    progress.Report(1);
                });
                if (!ok) return;

                _root = ArchiveEntryTree.Build(entries);
                _navStack.Clear();
                _currentFolder = _root;
                RefreshRows();
                StatusText.Text = $"{Path.GetFileName(path)} · 전체 {ArchiveEntryTree.FormatSize(_root.Size)}";
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
                StatusText.Text = "열기 실패: " + ex.Message;
                return;
            }
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

        var tempDir = Path.Combine(Path.GetTempPath(), "WinUtil", "Archive", Guid.NewGuid().ToString("N"));
        if (!await ExtractWithRetryAsync(tempDir, [node.FullPath], "여는 중...")) return;

        var extracted = Path.Combine(tempDir, node.FullPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(extracted))
        {
            Process.Start(new ProcessStartInfo(extracted) { UseShellExecute = true });
            StatusText.Text = "기본 앱으로 열었습니다: " + node.Name;
        }
        else
        {
            StatusText.Text = "임시 해제 결과를 찾지 못했습니다: " + node.Name;
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

        if (await ExtractWithRetryAsync(folder.Path, SelectedEntryPaths(), "푸는 중..."))
        {
            StatusText.Text = "풀기 완료: " + folder.Path;
            OpenInExplorer(folder.Path);
        }
    }

    private async void OnExtractHereClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _archivePath is null || _root is null) return;

        // 대상 폴더 결정: 단일 루트면 이중 폴더 방지, 이름이 겹치면 "(2)" 등 빈 이름 사용.
        var plan = ExtractHerePlanner.Plan(
            _archivePath,
            _root.Children.Select(c => c.Name).ToList(),
            p => Directory.Exists(p) || File.Exists(p));

        if (await ExtractWithRetryAsync(plan.TargetDirectory, entryPaths: null, "푸는 중..."))
        {
            StatusText.Text = "풀기 완료: " + plan.ResultPath;
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
                StatusText.Text = "풀기 실패: " + ex.Message;
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

    /// <summary>새 압축 공통 흐름: 형식/암호 선택 → 저장 위치 선택 → 생성.</summary>
    private async Task StartCreateFlowAsync(IReadOnlyList<string> sourcePaths, bool? use7z)
    {
        if (_busy || sourcePaths.Count == 0) return;

        var options = await PromptCreateOptionsAsync(use7z);
        if (options is null) return;
        var (sevenZ, password) = options.Value;

        var extension = sevenZ ? ".7z" : ".zip";
        var savePicker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePaths[0]),
        };
        savePicker.FileTypeChoices.Add(sevenZ ? "7z 압축 파일" : "ZIP 압축 파일", new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, GetHwnd());
        var target = await savePicker.PickSaveFileAsync();
        if (target is null) return;

        try
        {
            var ok = await RunOperationAsync("압축 중...", (progress, ct) =>
            {
                if (sevenZ) _backend.Create7z(sourcePaths, target.Path, password, progress, ct);
                else _backend.CreateZip(sourcePaths, target.Path, password, progress, ct);
            });
            if (ok) StatusText.Text = "압축 완료: " + target.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = "압축 실패: " + ex.Message;
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
            Title = "암호가 필요합니다",
            Content = box,
            PrimaryButtonText = "확인",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && box.Password.Length > 0 ? box.Password : null;
    }

    /// <summary>새 압축 옵션(형식/암호) 대화상자. 취소하면 null.</summary>
    private async Task<(bool Use7z, string? Password)?> PromptCreateOptionsAsync(bool? use7z)
    {
        var formatBox = new ComboBox
        {
            Header = "형식",
            ItemsSource = new[] { "zip", "7z" },
            SelectedIndex = use7z == true ? 1 : 0,
        };
        var passwordBox = new PasswordBox { Header = "암호 (선택, 비워두면 없음)" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(formatBox);
        panel.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            Title = "새 압축",
            Content = panel,
            PrimaryButtonText = "만들기",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var password = passwordBox.Password.Length > 0 ? passwordBox.Password : null;
        return (formatBox.SelectedIndex == 1, password);
    }

    // ---------- 공통: 백그라운드 실행 / 진행률 / 취소 ----------

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>
    /// 작업을 Task.Run으로 실행하고 진행률/취소를 연결한다. 취소되면 false.
    /// ArchivePasswordException 등은 호출자에게 그대로 전파된다.
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
            await Task.Run(() => work(progress, cts.Token), CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "취소됨";
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
            ?? throw new InvalidOperationException("창 핸들을 확인할 수 없습니다.");
        return Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
    }

    /// <summary>SynchronizationContext에 의존하지 않는 IProgress 구현(명시적 디스패처 마샬링용).</summary>
    private sealed class DelegateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
