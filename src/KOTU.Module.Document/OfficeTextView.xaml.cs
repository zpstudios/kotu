using KOTU.Core.Contracts;
using KOTU.Core.Threading;
using KOTU.DocumentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace KOTU.Module.Document;

/// <summary>A45: HWP 쪽 보기와 오피스 본문 읽기 화면.</summary>
public sealed partial class OfficeTextView : UserControl, IContentStateSource, IContentOpenFailedSource,
    IBottomBarProvider, IBottomOverlayConsumer, IDriveStripHost, ITrayStatusProvider, IContentInfoProvider, IEditorEscapeSource
{
    private readonly string _path;
    private readonly CancellationTokenSource _cancel = new();
    private ModuleWorker? _worker;
    private OfficeTextPreview? _preview;
    private HwpPagePane? _pages;
    private object? _bottomOverlay;
    private string _textNotice;
    private bool _showPages;
    private bool _started;
    private bool _closed;

    public event Action<string>? ContentOpened;
    public event Action? ContentOpenFailed;
    public event Action? EditorEscapeRequested;
    public event Action? TrayStatusChanged;

    public OfficeTextView(string path)
    {
        InitializeComponent();
        _path = path;
        FileNameText.Text = Path.GetFileName(path);
        _textNotice = Path.GetExtension(path).Equals(".hwp", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : "Text preview. Original layout, images, charts and formulas are not rendered. Tables are flattened; reading order may differ. Export to PDF for the original appearance.";
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            if (_closed) return;
            _closed = true;
            _cancel.Cancel();
            // 파싱·정보 조회가 토큰을 다 쓴 뒤 워커 큐에서 폐기한다.
            if (_worker is { } worker) worker.Post(_cancel.Dispose);
            else _cancel.Dispose();
            _worker?.Dispose();
            _worker = null;
            PageControls.Detach();
            _pages?.Close();
            _pages = null;
            _preview = null;
            TextList.ItemsSource = null;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || _closed) return;
        _started = true;
        _worker = new ModuleWorker("KOTU office preview worker");
        try
        {
            var result = await _worker.Run(_ =>
            {
                var (preview, source) = OfficeTextReader.ReadForDisplay(_path, _cancel.Token);
                return (Preview: preview, Source: source, Chunks: Chunk(preview.Text));
            }, _cancel.Token);
            if (_closed) return;
            _preview = result.Preview;
            TextList.ItemsSource = result.Chunks;
            if (_preview.Truncated)
                _textNotice = (_textNotice + " Preview limited to the first 200,000 characters; Copy text also copies only this preview.").TrimStart();
            CopyButton.IsEnabled = _preview.Text.Length > 0;
            if (result.Source is not null)
            {
                _pages = new HwpPagePane(PageHost, () => EditorEscapeRequested?.Invoke());
                if (_bottomOverlay is { } overlay) _pages.SetBottomOverlay(overlay);
                _pages.Failed += PageFailed;
                PageControls.Attach(_pages);
            }
            SetView(_pages is not null);
            ContentOpened?.Invoke(_path);
            if (!_closed && result.Source is not null && _pages is { } pane)
                await pane.OpenAsync(result.Source, _cancel.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_closed) return;
            StatusText.Text = "Unable to preview this document. " + ex.Message;
            FileNameText.Text = "No file open";
            ContentOpenFailed?.Invoke();
        }
    }

    private void PageFailed()
    {
        // WebView2 이벤트 핸들러가 반환된 다음 컨트롤을 닫는다.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || _pages is null) return;
            PageControls.Detach();
            _pages.Close();
            _pages = null;
            _textNotice = ("Page preview unavailable for this document. Showing text preview. " + _textNotice).TrimEnd();
            SetView(false);
        });
    }

    private void SwitchView(object sender, RoutedEventArgs e) => SetView(!_showPages && _pages is not null);

    private void SetView(bool pages)
    {
        _showPages = pages;
        ViewMode.Visibility = _pages is null ? Visibility.Collapsed : Visibility.Visible;
        var viewAction = pages ? "Text view" : "Page view";
        AutomationProperties.SetName(ViewMode, viewAction);
        ToolTipService.SetToolTip(ViewMode, viewAction);
        ViewModeIcon.Glyph = pages ? "\uE8A5" : "\uE890";
        PageControls.Visibility = PageHost.Visibility = pages ? Visibility.Visible : Visibility.Collapsed;
        Notice.Text = _textNotice;
        Notice.Visibility = !pages && _textNotice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var textVisibility = pages ? Visibility.Collapsed : Visibility.Visible;
        TextList.Visibility = CopyButton.Visibility = TextSmaller.Visibility = TextLarger.Visibility = textVisibility;
        StatusText.Text = "No previewable text was found. Export to PDF to view graphical content.";
        StatusText.Visibility = !pages && _preview?.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrayStatusChanged?.Invoke();
    }

    // 한 개의 거대한 TextBox 대입 대신 짧은 문자열 목록을 가상화한다.
    private static IReadOnlyList<string> Chunk(string text)
    {
        var result = new List<string>();
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(2048, text.Length - start);
            if (start + length < text.Length)
            {
                var line = text.LastIndexOf('\n', start + length - 1, length);
                if (line >= start) length = line - start + 1;
                else if (char.IsHighSurrogate(text[start + length - 1])) length--;
            }
            result.Add(text.Substring(start, length));
            start += length;
        }
        return result;
    }

    private void CopyText(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        try
        {
            var data = new DataPackage();
            data.SetText(_preview.Text);
            Clipboard.SetContent(data);
        }
        catch { StatusText.Text = "The clipboard is currently unavailable."; StatusText.Visibility = Visibility.Visible; }
    }

    private void SmallerText(object sender, RoutedEventArgs e) => TextList.FontSize = Math.Max(10, TextList.FontSize - 2);
    private void LargerText(object sender, RoutedEventArgs e) => TextList.FontSize = Math.Min(40, TextList.FontSize + 2);
    public object? TakeBottomBar() { RootGrid.Children.Remove(BottomBar); PageHost.Margin = new Thickness(0); return BottomBar; }
    public void SetBottomOverlay(object overlay)
    {
        _bottomOverlay = overlay;
        _pages?.SetBottomOverlay(overlay);
    }
    public void AttachDriveStrip(object strip) => DriveStripHost.Content = strip as UIElement;
    public void ShowDriveStrip(bool show)
    {
        DriveStripHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }
    public TrayStatus GetTrayStatus() => _preview is null ? TrayStatus.Idle("DOC") : TrayStatus.Open(_preview.Format, _showPages ? "PAGE" : "TXT");
    public async Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync()
    {
        if (_closed || _preview is null || _worker is null) return null;
        try
        {
            var rows = await _worker.Run(_ => DocumentQuickInfo.BuildRows(_path), _cancel.Token);
            return _closed ? null : rows;
        }
        catch { return null; }
    }
}
