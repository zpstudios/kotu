using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using KOTU.Core.Contracts;
using KOTU.Core.Threading;
using KOTU.Input;

namespace KOTU.Module.Document;

/// <summary>
/// 문서 화면: 플레인 텍스트(txt·md·log·ini)는 열어서 바로 편집·저장까지 하고(A37 — 뷰어→에디터 승격),
/// PDF는 PdfPane으로 본다(A16). 텍스트 인코딩은 열 때 감지한 것(UTF-8/UTF-8 BOM/UTF-16/CP949)을
/// 저장 시 그대로 보존하고, 줄바꿈도 원본 스타일(CRLF/LF)을 유지한다.
/// 큰 파일(4MB 초과)은 앞부분만 읽으므로 읽기 전용.
/// 파일 I/O는 뷰 전용 워커(A42)에서 수행하고 UI 스레드는 결과 반영만 한다.
/// </summary>
public sealed partial class DocumentView : UserControl,
    IContentStateSource, IBottomBarProvider, IDriveStripHost, ICloseGuard, ITrayStatusProvider
{
    /// <summary>파일을 열면 셸에 알린다(빈 상태 탐색기 내림·오버레이 기준 갱신).</summary>
    public event Action<string>? ContentOpened;

    /// <summary>트레이 아이콘 표시 값이 바뀌었다(A54) — 텍스트·PDF 열기와 닫기 시점.</summary>
    public event Action? TrayStatusChanged;

    /// <summary>
    /// 지금 보고 있는 파일(트레이 표기용, A54). 편집 대상인 <c>_path</c>와 별개다 —
    /// PDF는 편집하지 않아 <c>_path</c>가 null이지만 트레이에는 열린 파일로 보여야 한다.
    /// </summary>
    private string? _shownPath;

    /// <summary>트레이 아이콘 내용(A54): 열림 = 확장자 · 용량, 유휴 = "DOC".</summary>
    public TrayStatus GetTrayStatus()
    {
        if (_shownPath is not { } path) return TrayStatus.Idle("DOC");
        long bytes = -1;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch
        {
            // 크기를 못 읽으면 그 줄만 "—"가 된다.
        }
        return TrayStatus.Open(TrayFormat.Extension(path), TrayFormat.Size(bytes));
    }

    /// <summary>미저장 상태 변화(A37) — 셸이 창 제목 ● 표시에 쓴다.</summary>
    public event Action<bool>? UnsavedChanged;

    /// <summary>4MB 초과 텍스트는 앞부분만 표시(TextBox 성능 보호) — 이때는 편집 불가.</summary>
    private const int MaxBytes = 4 * 1024 * 1024;

    private int _openSeq; // 느린 읽기가 최신 열기를 덮지 않게
    private ModuleWorker? _worker; // 파일 읽기·쓰기 전용(A42) — 뷰별 분리
                                   // (드라이브 조회는 A22에서 셸의 드라이브 줄 워커로 옮겼다)

    // ---- 편집 상태 (A37) ----
    private string? _path;                 // 지금 편집 중인 파일
    private TextEncodingKind _encoding;    // 열 때 감지한 인코딩 — 저장 시 보존
    private string _newLine = "\r\n";      // 원본 줄바꿈 스타일 — 저장 시 보존
    private bool _truncated;               // 4MB 잘림 → 읽기 전용
    private bool _dirty;                   // 미저장 변경 여부
    private bool _loadingText;             // 프로그램적 Text 설정 중 TextChanged 무시용

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU document worker");

    static DocumentView() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // CP949 사용 전 1회 등록

    public DocumentView(OpenContext context)
    {
        InitializeComponent();
        SetupHotkeys(); // A34: 하단 바 버튼 핫키 + 툴팁 표기

        // A22(v0.108.0): A49의 "좁으면 드라이브 텍스트 숨김"(임계 760) 규칙은 제거했다 —
        // 드라이브 표시가 Auto 폭 텍스트에서 남는 폭(star 칸)을 쓰는 슬롯으로 바뀌어
        // 더는 버튼들을 밀어내지 않는다(넘치면 줄 자체가 스크롤한다). 게다가 이제는
        // 파일이 열려 있지 않을 때만 뜨는데, 그때는 페이지·Fit 표시가 아예 없어 자리도 넉넉하다.

        Loaded += (_, _) => Focus(FocusState.Programmatic);
        Unloaded += (_, _) =>
        {
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
        };

        if (context.FilePath is { } path && File.Exists(path))
            OpenAny(path);
        else
            PlaceholderText.Visibility = Visibility.Visible;
    }

    /// <summary>확장자로 텍스트/PDF 경로를 나눈다(A16).</summary>
    private void OpenAny(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            OpenPdf(path);
        else
            OpenPath(path);
    }

    /// <summary>하단 상태바를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(이미지 v0.27.0과 동일 패턴).</summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(StatusBar);
        return StatusBar;
    }

    /// <summary>
    /// A22(v0.108.0): 셸이 만든 공용 드라이브 줄을 하단 바 슬롯에 끼운다.
    /// v0.47.0의 모듈별 드라이브 텍스트(DriveInfoText)를 대체한다.
    /// </summary>
    public void AttachDriveStrip(object strip) => DriveStripHost.Content = strip as UIElement;

    /// <summary>
    /// 드라이브 줄과 파일명은 같은 칸을 나눠 쓴다 — 줄이 뜨는 동안(파일 없음, 파일명은
    /// "No file open"뿐)에는 파일명을 비켜준다.
    /// </summary>
    public void ShowDriveStrip(bool show)
    {
        DriveStripHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- 파일 열기 ----------

    private async void OpenPath(string path)
    {
        var seq = ++_openSeq;
        LoadedText loaded;
        try
        {
            loaded = await Worker.Run(_ => ReadTextSmart(path));
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

        HidePdf(); // PDF → 텍스트 전환 (A16)
        _path = path;
        _encoding = loaded.Encoding;
        _newLine = loaded.NewLine;
        _truncated = loaded.Truncated;

        _loadingText = true; // 프로그램적 설정 — dirty 아님
        EditorBox.Text = loaded.Text;
        _loadingText = false;
        EditorBox.IsReadOnly = loaded.Truncated; // 잘린 채 저장되는 사고 방지
        SetDirty(false);

        EditorBox.Visibility = Visibility.Visible;
        PlaceholderText.Visibility = Visibility.Collapsed;
        FileNameText.Text = Path.GetFileName(path);
        _shownPath = path;
        ContentOpened?.Invoke(path); // 셸 동기화 — A22: 셸이 드라이브 줄을 내린다
        TrayStatusChanged?.Invoke(); // A54: 트레이 = 확장자 · 용량
    }

    // ---------- PDF (A16) ----------

    private PdfPane? _pdfPane; // 지연 생성 — 텍스트만 쓰는 세션에는 만들지 않는다

    private async void OpenPdf(string path)
    {
        var seq = ++_openSeq; // 텍스트 열기와 같은 경쟁 방지 시퀀스 공유

        if (_pdfPane is null)
        {
            _pdfPane = new PdfPane();
            _pdfPane.PageChanged += (current, total) =>
                PageInfoText.Text = total > 0 ? $"{current} / {total}" : string.Empty;
            RootGrid.Children.Insert(0, _pdfPane); // 상태바·플레이스홀더보다 뒤(z 순서)
        }

        // 화면 전환: 에디터 내리고 PDF 패널 표시. PDF는 편집 대상이 아니다 — 저장 상태 초기화.
        _path = null;
        _truncated = false;
        SetDirty(false);
        EditorBox.Visibility = Visibility.Collapsed;
        PlaceholderText.Visibility = Visibility.Collapsed;
        _pdfPane.Visibility = Visibility.Visible;
        PageInfoText.Visibility = Visibility.Visible;
        PageInfoText.Text = string.Empty;
        FileNameText.Text = Path.GetFileName(path);

        // A49: 1:1·Fit 버튼은 PDF 모드에서만. 파일이 바뀌면 버튼 표시도 Auto-fit으로
        // 회귀(A30 규칙, 기억 안 함) — 실제 배율 적용은 PdfPane.LoadAsync가 한다.
        ActualSizeButton.Visibility = Visibility.Visible;
        FitButton.Visibility = Visibility.Visible;
        if (_lastFitOption != PdfFitMode.AutoFit)
        {
            _lastFitOption = PdfFitMode.AutoFit;
            UpdateFitButton();
        }

        var ok = await _pdfPane.LoadAsync(path); // 실패 다이얼로그는 패널이 띄운다
        if (seq != _openSeq) return;             // 그새 다른 파일이 열렸다
        if (!ok)
        {
            HidePdf();
            FileNameText.Text = "No file open";
            PlaceholderText.Visibility = Visibility.Visible;
            _shownPath = null;
            TrayStatusChanged?.Invoke(); // A54: 열기 실패 → 유휴("DOC")
            return;
        }

        _shownPath = path;
        ContentOpened?.Invoke(path); // 셸 동기화 — A22: 셸이 드라이브 줄을 내린다
        TrayStatusChanged?.Invoke(); // A54: 트레이 = 확장자 · 용량
    }

    /// <summary>PDF 패널을 내린다(텍스트로 전환·열기 실패 시). 비트맵·문서 참조 해제.</summary>
    private void HidePdf()
    {
        if (_pdfPane is null) return;
        _pdfPane.Clear();
        _pdfPane.Visibility = Visibility.Collapsed;
        PageInfoText.Visibility = Visibility.Collapsed;
        ActualSizeButton.Visibility = Visibility.Collapsed; // A49: 텍스트 에디터 모드는 Fit 대상 아님
        FitButton.Visibility = Visibility.Collapsed;
    }

    // ---------- PDF 맞춤 보기 (A49 — A30 규격) ----------

    /// <summary>
    /// A30 규격: Fit 버튼 본체가 표시·재적용할 마지막 핏 옵션(Auto/좌우/상하 — 1:1은 별도 버튼이라 제외).
    /// 기억하지 않는다 — 파일이 바뀌면 Auto-fit으로 회귀(A30 규칙).
    /// </summary>
    private PdfFitMode _lastFitOption = PdfFitMode.AutoFit;

    /// <summary>A30 규격: Fit 버튼 본체 내용(A 텍스트/좌우/상하 아이콘)과 툴팁을 마지막 옵션에 맞춘다.</summary>
    private void UpdateFitButton()
    {
        (object content, string tip) = _lastFitOption switch
        {
            PdfFitMode.FitWidth =>
                ((object)new FontIcon { Glyph = "\uE8AB", FontSize = 18 }, "Fit width"),
            PdfFitMode.FitHeight =>
                (new FontIcon { Glyph = "\uE8CB", FontSize = 18 }, "Fit height"),
            _ => (new TextBlock
            {
                Text = "A",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, "Auto-fit to window - whole page fits, or actual size if smaller"),
        };
        FitButton.Content = content;
        ToolTipService.SetToolTip(FitButton, HotkeySupport.Tip(tip, FitKey)); // A34: 표기는 키 상수에서
    }

    /// <summary>플라이아웃에서 옵션 선택 — 즉시 적용하고 버튼 표시를 그 옵션으로 바꾼다.</summary>
    private void SelectFitOption(PdfFitMode option)
    {
        _lastFitOption = option;
        UpdateFitButton();
        _pdfPane?.ApplyFit(option);
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e) =>
        _pdfPane?.ApplyFit(PdfFitMode.ActualSize);

    /// <summary>A30 규격: 본체 클릭 = 버튼에 표시된 마지막 옵션 재적용(1:1에서 되돌아올 때도 이 경로).</summary>
    private void OnFitClicked(SplitButton sender, SplitButtonClickEventArgs args) =>
        _pdfPane?.ApplyFit(_lastFitOption);

    private void OnFitAutoClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(PdfFitMode.AutoFit);

    private void OnFitWidthClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(PdfFitMode.FitWidth);

    private void OnFitHeightClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(PdfFitMode.FitHeight);

    // ---------- 인코딩 감지·보존 (A37) ----------

    /// <summary>열 때 감지해 저장 시 그대로 재현하는 인코딩 종류.</summary>
    private enum TextEncodingKind
    {
        Utf8,       // BOM 없는 UTF-8
        Utf8Bom,    // EF BB BF
        Utf16LeBom, // FF FE
        Utf16BeBom, // FE FF
        Cp949,      // 레거시 한글 (BOM 없음, UTF-8 해석 실패 시)
    }

    private sealed record LoadedText(
        string Text, TextEncodingKind Encoding, string NewLine, bool Truncated);

    /// <summary>
    /// BOM이 있으면 그대로, 없으면 엄격 UTF-8로 시도하고 깨질 때만 CP949로 해석한다
    /// (동영상 자막 SubtitleCharset과 같은 접근 — 모듈 간 참조 금지라 별도 구현).
    /// 감지한 인코딩·줄바꿈 스타일은 저장 시 보존을 위해 함께 반환한다(A37).
    /// </summary>
    private static LoadedText ReadTextSmart(string path)
    {
        using var stream = File.OpenRead(path);
        var truncated = stream.Length > MaxBytes;
        var bytes = new byte[Math.Min(stream.Length, MaxBytes)];
        stream.ReadExactly(bytes);

        string text;
        TextEncodingKind kind;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            kind = TextEncodingKind.Utf8Bom;
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            kind = TextEncodingKind.Utf16LeBom;
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            kind = TextEncodingKind.Utf16BeBom;
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(bytes);
                kind = TextEncodingKind.Utf8;
            }
            catch (DecoderFallbackException)
            {
                kind = TextEncodingKind.Cp949;
                text = Encoding.GetEncoding(949).GetString(bytes); // 레거시 한글(CP949)
            }
        }

        // 줄바꿈 스타일: 첫 줄바꿈 기준(혼합 파일은 첫 스타일로 통일 저장 — 단순함 우선).
        // 줄바꿈이 없는 파일은 Windows 기본 CRLF.
        var lf = text.IndexOf('\n');
        var newline = lf > 0 && text[lf - 1] == '\r' ? "\r\n" : lf >= 0 ? "\n" : "\r\n";

        if (truncated)
            text += $"\n\n--- Showing the first {MaxBytes / 1024 / 1024} MB of this file (read-only) ---";
        return new LoadedText(text, kind, newline, truncated);
    }

    /// <summary>저장용 인코드. CP949로 표현 못 하는 문자는 예외로 알린다(무단 '?' 치환 방지).</summary>
    private static byte[] EncodeStrict(string text, TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8Bom =>
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
        TextEncodingKind.Utf16LeBom =>
            [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
        TextEncodingKind.Utf16BeBom =>
            [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
        TextEncodingKind.Cp949 => Encoding.GetEncoding(949,
            EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback).GetBytes(text),
        _ => Encoding.UTF8.GetBytes(text), // BOM 없는 UTF-8
    };

    // ---------- 편집·저장 (A37) ----------

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingText || _dirty || _path is null) return;
        SetDirty(true);
    }

    /// <summary>Tab이 포커스 이동 대신 탭 문자를 넣게 한다(에디터 기본기). Shift+Tab은 포커스 이동 유지.</summary>
    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || EditorBox.IsReadOnly) return;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

        e.Handled = true;
        var pos = EditorBox.SelectionStart;
        EditorBox.SelectedText = "\t"; // 선택 영역을 탭으로 치환(없으면 삽입)
        EditorBox.SelectionStart = pos + 1;
        EditorBox.SelectionLength = 0;
    }

    private void SetDirty(bool dirty)
    {
        ModifiedText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = dirty;
        if (_dirty == dirty) return; // 파일 전환 직후 UI 초기화만 필요한 경우
        _dirty = dirty;
        UnsavedChanged?.Invoke(dirty);
    }

    /// <summary>
    /// 현재 내용을 원본 인코딩·줄바꿈으로 저장한다. true = 저장 완료(또는 저장할 것 없음).
    /// CP949로 표현 못 하는 문자가 생겼으면 UTF-8 전환을 물어보고, 거부하면 false(취소).
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (_path is null || _truncated || !_dirty) return true;

        // WinUI TextBox는 줄바꿈을 '\r'로 정규화한다 — 원본 스타일로 되돌린다.
        var text = EditorBox.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (_newLine != "\n") text = text.Replace("\n", _newLine);

        byte[] bytes;
        try
        {
            bytes = EncodeStrict(text, _encoding);
        }
        catch (EncoderFallbackException)
        {
            if (!await ConfirmUtf8FallbackAsync()) return false;
            _encoding = TextEncodingKind.Utf8; // 이후 저장도 UTF-8
            bytes = EncodeStrict(text, _encoding);
        }

        var path = _path;
        try
        {
            await Worker.Run(_ => File.WriteAllBytes(path, bytes));
        }
        catch (OperationCanceledException)
        {
            return false; // 뷰가 내려가는 중 — 저장 완료를 주장하지 않는다
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Save failed", ex.Message);
            return false;
        }

        if (path == _path) SetDirty(false); // 그새 다른 파일로 안 바뀐 경우만
        return true;
    }

    /// <summary>CP949 원본에 CP949로 못 쓰는 문자가 들어왔을 때: UTF-8 전환 확인.</summary>
    private async Task<bool> ConfirmUtf8FallbackAsync()
    {
        if (XamlRoot is null) return false;
        var dialog = new ContentDialog
        {
            Title = "Encoding change required",
            Content = "Some characters can't be saved in the file's original encoding (CP949).\n"
                      + "Save as UTF-8 instead?",
            PrimaryButtonText = "Save as UTF-8",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void OnSaveButtonClick(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private void OnSaveInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = SaveAsync();
    }

    // ---------- 미저장 가드 (A37 — ICloseGuard) ----------

    public bool HasUnsavedChanges => _dirty;

    /// <summary>저장/버리기/취소 확인. 셸이 뷰 교체·창 닫기 전에 부르고, 뷰 내부 열기도 직접 부른다.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!_dirty) return true;
        if (XamlRoot is null) return true; // 다이얼로그를 띄울 수 없으면 막지 않는다

        var dialog = new ContentDialog
        {
            Title = "Unsaved changes",
            Content = $"Save changes to {Path.GetFileName(_path)}?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveAsync(), // 저장 실패·취소면 닫기도 취소
            ContentDialogResult.Secondary => true,            // 버리기
            _ => false,                                       // 취소
        };
    }

    // A99: 모듈 열기 버튼·O 키·파일 대화상자(PickAndOpenAsync)는 제거 — 파일 열기는
    // 셸 S4 'Open file'(A90)로 일원화됐다(미저장 가드는 셸이 열기 전에 통과시킨다 — A37).
    // 드래그&드롭은 종전대로 창 수준(MainWindow)에서 확장자 라우팅으로 일괄 처리한다.

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

    // ---------- 하단 바 버튼 핫키 (A34) ----------

    /// <summary>Fit 키 — 툴팁 표기(UpdateFitButton)와 액셀러레이터가 이 한 값을 함께 쓴다.</summary>
    private const VirtualKey FitKey = VirtualKey.F;

    /// <summary>
    /// A34: 하단 바 버튼에 단독 문자 키를 걸고 툴팁 "(키)" 표기까지 같은 호출에서 만든다.
    /// **이 모듈은 에디터(TextBox)가 본문**이라 통과 규칙이 특히 중요하다 — 타이핑 중에는
    /// HotkeySupport가 A·F를 삼키지 않고 글자를 그대로 흘려보낸다(A32/A84 규칙 재사용).
    /// 1:1(A)·Fit(F)은 PDF에서만 보이는 버튼이라, 텍스트 모드(Collapsed)에서는 키도 동작하지 않는다.
    /// 저장은 Ctrl+S 그대로(A84의 유일한 Ctrl 예외) — XAML 액셀러레이터에 남겨 둔다.
    /// </summary>
    private void SetupHotkeys()
    {
        HotkeySupport.Bind(this, ActualSizeButton, VirtualKey.A,
            "Actual size (100%)", () => _pdfPane?.ApplyFit(PdfFitMode.ActualSize));
        HotkeySupport.Register(this, FitButton, FitKey, () => _pdfPane?.ApplyFit(_lastFitOption));
        UpdateFitButton(); // Fit 툴팁은 표시 상태를 따라가므로 초기값도 여기서 만든다
    }
}
