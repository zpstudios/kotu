using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
/// 입력(A58: Shift 홀드 = 반투명 / 2초 = 고정 / 2연타 = 불투명 밀어내기·해제 —
/// 기존 Ctrl을 대체, 부록 B 26번)은 셸(MainWindow)의 상태 머신이 담당한다 —
/// 이 컨트롤은 ShowFor/Hide/SetState만 받는다.
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
    /// provider = 모듈 뷰의 정보 계약(IContentInfoProvider, null이면 파일 기본 정보).
    /// 모드·고정 안내는 SetState가 별도로 반영한다(A58 — 기존 pinned 인자 대체).
    /// </summary>
    public void ShowFor(string path, IContentInfoProvider? provider)
    {
        Visibility = Visibility.Visible;
        _ = LoadAsync(path, provider);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        OverlayBorder.IsHitTestVisible = false;
        PinnedText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 파일 없는 상태의 플레이스홀더 (A81 — 빈 모듈에서 기본 도크로 뜰 때):
    /// 보여줄 파일 정보가 없으므로 간단한 안내 한 줄만 표시한다.
    /// 진행 중이던 로드가 늦게 도착해 문구를 덮지 않게 캐시·시퀀스를 함께 무효화한다.
    /// 모드·안내 문구는 ShowFor와 동일하게 SetState가 별도로 반영한다.
    /// </summary>
    public void ShowPlaceholder()
    {
        InvalidateCache();
        Visibility = Visibility.Visible;
        InfoText.Text = "No file open";
    }

    /// <summary>
    /// 표시 모드·고정 안내 반영 (A58). TranslucentOver = 아크릴 반투명(A33): 홀드 중이면
    /// 문구 없음, pinned(2초 홀드 고정)면 unpin 안내. OpaqueDocked = 불투명 배경 + close 안내 —
    /// 실제 폭 차지(메인 축소)는 셸의 도크 컬럼이 담당하고 여기서는 시각·문구만 바꾼다.
    /// 상호작용(스크롤)은 고정·불투명에서만 허용 — 홀드 중에는 아래 콘텐츠 클릭을 막지 않는다
    /// (기존 pinned 규칙 유지).
    /// </summary>
    public void SetState(OverlayMode mode, bool pinned)
    {
        var docked = mode == OverlayMode.OpaqueDocked;
        OverlayBorder.Background = (Brush)Application.Current.Resources[
            docked ? "SolidBackgroundFillColorBaseBrush" : "OverlayAcrylicBrush"];
        OverlayBorder.IsHitTestVisible = IsOpen && (docked || pinned);
        PinnedText.Text = docked
            ? "Docked — press Shift twice to close"
            : "Pinned — press Shift twice to unpin";
        PinnedText.Visibility = IsOpen && (docked || pinned)
            ? Visibility.Visible : Visibility.Collapsed;
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
