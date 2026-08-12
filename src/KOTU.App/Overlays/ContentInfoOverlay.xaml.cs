using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.App.Overlays;

/// <summary>
/// 콘텐츠 정보 오버레이 공용 컨트롤 (A57 ②) — 기존 MainWindow의 InfoOverlayRoot(좌측 30%,
/// v0.25.0)를 추출해 우측 30%로 스왑(A57 ①)한 것. 정보 로드 로직(v0.25.0의
/// LoadContentInfoAsync — 파일별 1회 캐시·경쟁 방지 시퀀스·기본 파일 정보 폴백)도 함께 이관.
/// 정보 항목은 모듈이 주입한다: ShowFor()에 넘기는 IContentInfoProvider(모듈 뷰)가 내용을 만들고,
/// 없거나 실패하면 파일 기본 정보로 대체한다. 정보(H/W)·설정 모듈은 셸이 파일 경로가 없어
/// 애초에 ShowFor를 부르지 않는다(현행 동작 유지).
/// 입력(Ctrl 홀드·2연타 고정, A32)은 셸(MainWindow)이 그대로 담당한다 — A58에서 별도 대체 예정.
/// </summary>
public sealed partial class ContentInfoOverlay : UserControl
{
    private int _seq;             // 정보 로드 경쟁 방지 (기존 MainWindow._infoSeq)
    private string? _activePath;  // 마지막으로 요청된 파일 — 늦게 도착한 결과 폐기 판단
    private string? _cachePath;   // 정보 캐시 (파일별 1회 로드 — 기존 _infoPath/_infoText)
    private string? _cacheText;

    /// <summary>오버레이가 화면에 떠 있는지 — 셸의 표시 갱신 판단에 쓴다.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    public ContentInfoOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 모듈 컨텍스트를 주입받아 표시한다: path = 현재 콘텐츠 파일,
    /// provider = 모듈 뷰의 정보 계약(IContentInfoProvider, null이면 파일 기본 정보),
    /// pinned = 2연타 고정 상태(고정했을 때만 스크롤 등 상호작용 허용 + 안내 문구).
    /// </summary>
    public void ShowFor(string path, IContentInfoProvider? provider, bool pinned)
    {
        Visibility = Visibility.Visible;
        OverlayBorder.IsHitTestVisible = pinned;
        PinnedText.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadAsync(path, provider);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        OverlayBorder.IsHitTestVisible = false;
        PinnedText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 파일·모듈이 바뀌었을 때 셸이 부른다 — 캐시를 비우고 진행 중 로드를 폐기해,
    /// 다음 표시에서 새 콘텐츠 기준으로 다시 읽게 한다(같은 경로 재오픈 포함).
    /// </summary>
    public void InvalidateCache()
    {
        _cachePath = null;
        _cacheText = null;
        _activePath = null;
        _seq++;
    }

    /// <summary>모듈 제공 정보(IContentInfoProvider) 우선, 없으면 파일 기본 정보. 파일별 1회 캐시.</summary>
    private async Task LoadAsync(string path, IContentInfoProvider? provider)
    {
        if (_cachePath == path && _cacheText is not null)
        {
            InfoText.Text = _cacheText;
            return;
        }

        var seq = ++_seq;
        _activePath = path;
        InfoText.Text = "Loading info...";

        string? text = null;
        try
        {
            if (provider is not null)
                text = await provider.GetContentInfoAsync();
        }
        catch
        {
            // 모듈 정보 실패 → 아래 파일 기본 정보로 대체
        }
        text ??= BuildBasicFileInfo(path);

        if (seq != _seq || _activePath != path) return; // 그새 파일이 바뀜
        _cachePath = path;
        _cacheText = text;
        InfoText.Text = text;
    }

    private static string BuildBasicFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Name}\n{ExplorerListing.FormatSize(info.Length)}\n"
                 + $"{info.LastWriteTime:yyyy-MM-dd HH:mm}\n{info.DirectoryName}";
        }
        catch (Exception ex)
        {
            return Path.GetFileName(path) + "\nInfo unavailable: " + ex.Message;
        }
    }
}
