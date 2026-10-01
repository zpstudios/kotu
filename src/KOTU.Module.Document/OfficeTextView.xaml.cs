using KOTU.Core.Contracts;
using KOTU.Core.Threading;
using KOTU.DocumentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace KOTU.Module.Document;

/// <summary>A45: 오피스 본문 전용 읽기 화면. 에디터·저장·인쇄 계약을 구현하지 않는다.</summary>
public sealed partial class OfficeTextView : UserControl, IContentStateSource, IContentOpenFailedSource,
    IBottomBarProvider, IDriveStripHost, ITrayStatusProvider, IContentInfoProvider
{
    private readonly string _path;
    private readonly CancellationTokenSource _cancel = new();
    private ModuleWorker? _worker;
    private OfficeTextPreview? _preview;
    private bool _started;
    private bool _closed;

    public event Action<string>? ContentOpened;
    public event Action? ContentOpenFailed;
    public event Action? TrayStatusChanged;

    public OfficeTextView(string path)
    {
        InitializeComponent();
        _path = path;
        FileNameText.Text = Path.GetFileName(path);
        if (Path.GetExtension(path).Equals(".hwp", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".hwpx", StringComparison.OrdinalIgnoreCase))
            Notice.Text += "\nThis product was developed by referring to Hancom's public HWP document file specification."
                + "\n본 제품은 한글과컴퓨터의 한글 문서 파일(.hwp) 공개 문서를 참고하여 개발하였습니다.";
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
                var preview = OfficeTextReader.Read(_path, _cancel.Token);
                return (Preview: preview, Chunks: Chunk(preview.Text));
            }, _cancel.Token);
            if (_closed) return;
            _preview = result.Preview;
            TextList.ItemsSource = result.Chunks;
            StatusText.Text = _preview.Text.Length == 0 ? "No previewable text was found. Export to PDF to view graphical content." : "";
            StatusText.Visibility = _preview.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_preview.Truncated) Notice.Text += " Preview limited to the first 200,000 characters; Copy text also copies only this preview.";
            CopyButton.IsEnabled = _preview.Text.Length > 0;
            ContentOpened?.Invoke(_path);
            TrayStatusChanged?.Invoke();
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
    public object? TakeBottomBar() { RootGrid.Children.Remove(BottomBar); return BottomBar; }
    public void AttachDriveStrip(object strip) => DriveStripHost.Content = strip as UIElement;
    public void ShowDriveStrip(bool show)
    {
        DriveStripHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }
    public TrayStatus GetTrayStatus() => _preview is null ? TrayStatus.Idle("DOC") : TrayStatus.Open(_preview.Format, "TXT");
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
