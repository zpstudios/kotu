using System.Text;
using Microsoft.UI; // Win32Interop
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUtil.Core.Contracts;
using WinUtil.Core.Threading;

namespace WinUtil.Module.Document;

/// <summary>
/// 문서 뷰어 화면(1단계): 텍스트·마크다운 원문 표시. UTF-8(BOM 포함) 우선,
/// 깨지면 CP949로 재해석(레거시 한글 텍스트). 큰 파일은 앞부분만 보여준다.
/// 파일 I/O는 뷰 전용 워커(A42)에서 수행하고 UI 스레드는 결과 반영만 한다.
/// </summary>
public sealed partial class DocumentView : UserControl, IContentStateSource, IBottomBarProvider
{
    /// <summary>파일을 열면 셸에 알린다(빈 상태 탐색기 내림·오버레이 기준 갱신).</summary>
    public event Action<string>? ContentOpened;

    /// <summary>4MB 초과 텍스트는 앞부분만 표시(TextBlock 성능 보호).</summary>
    private const int MaxBytes = 4 * 1024 * 1024;

    private int _openSeq; // 느린 읽기가 최신 열기를 덮지 않게
    private ModuleWorker? _worker; // 파일 읽기·드라이브 조회 전용(A42) — 뷰별 분리

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("ZP document worker");

    static DocumentView() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // CP949 사용 전 1회 등록

    public DocumentView(OpenContext context)
    {
        InitializeComponent();
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        Unloaded += (_, _) =>
        {
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
        };

        if (context.FilePath is { } path && File.Exists(path))
            OpenPath(path);
        else
            PlaceholderText.Visibility = Visibility.Visible;
    }

    /// <summary>하단 상태바를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(이미지 v0.27.0과 동일 패턴).</summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(StatusBar);
        return StatusBar;
    }

    // ---------- 파일 열기 ----------

    private async void OpenPath(string path)
    {
        var seq = ++_openSeq;
        string text;
        try
        {
            text = await Worker.Run(_ => ReadTextSmart(path));
        }
        catch (OperationCanceledException)
        {
            return; // 뷰가 내려가며 워커가 닫힘
        }
        catch (Exception ex)
        {
            PlaceholderText.Text = "Failed to open: " + ex.Message;
            PlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        if (seq != _openSeq) return; // 그새 다른 파일이 열렸다
        ContentText.Text = text;
        PlaceholderText.Visibility = Visibility.Collapsed;
        FileNameText.Text = Path.GetFileName(path);
        UpdateDriveInfo(path); // v0.47.0 표시 — 조회는 워커에서(A42: UI 스레드 동기 I/O 제거)
        ContentOpened?.Invoke(path); // 셸 동기화
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

    /// <summary>
    /// BOM이 있으면 그대로, 없으면 엄격 UTF-8로 시도하고 깨질 때만 CP949로 해석한다
    /// (동영상 자막 SubtitleCharset과 같은 접근 — 모듈 간 참조 금지라 별도 구현).
    /// </summary>
    private static string ReadTextSmart(string path)
    {
        using var stream = File.OpenRead(path);
        var truncated = stream.Length > MaxBytes;
        var bytes = new byte[Math.Min(stream.Length, MaxBytes)];
        stream.ReadExactly(bytes);

        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.GetEncoding(949).GetString(bytes); // 레거시 한글(CP949)
            }
        }

        return truncated
            ? text + $"\n\n--- Showing the first {MaxBytes / 1024 / 1024} MB of this file ---"
            : text;
    }

    private async Task PickAndOpenAsync()
    {
        // Window 객체 없이 파일 선택기를 띄우려면 XamlRoot 경유로 HWND를 얻어야 한다.
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;
        var hwnd = Win32Interop.GetWindowFromWindowId(environment.AppWindowId);

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in DocumentModule.Extensions)
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, hwnd);

        if (await picker.PickSingleFileAsync() is { } file)
            OpenPath(file.Path);
    }

    private void OnOpenButtonClick(object sender, RoutedEventArgs e) => _ = PickAndOpenAsync();
    // 드래그&드롭은 창 수준(MainWindow)에서 확장자 라우팅으로 일괄 처리한다.

    // ---------- 전체화면 (전 모듈 공통 패턴) ----------

    private void ToggleFullScreen()
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;

        var appWindow = AppWindow.GetFromWindowId(environment.AppWindowId);
        appWindow.SetPresenter(appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
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
        var appWindow = AppWindow.GetFromWindowId(environment.AppWindowId);
        if (appWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen) return;

        args.Handled = true;
        appWindow.SetPresenter(AppWindowPresenterKind.Default);
    }
}
