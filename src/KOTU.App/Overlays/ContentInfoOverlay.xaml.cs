using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.App.Overlays;

/// <summary>
/// 콘텐츠 정보 패널 공용 컨트롤 (A57 ②) — 기존 MainWindow의 InfoOverlayRoot(좌측,
/// v0.25.0)를 추출해 우측으로 스왑(A57 ①)한 것.
/// 용어(A108): 사이드바 = 불투명(OpaqueDocked) / 오버레이 = 반투명(홀드·고정).
/// 패널 폭은 전 상태 공통 25%(A116 — 종전 "콘텐츠 30% / S1 25%" 2값 폐지,
/// SetPanelPercent). 정보 로드 로직(v0.25.0의
/// LoadContentInfoAsync — 파일별 1회 캐시·경쟁 방지 시퀀스·기본 파일 정보 폴백)도 함께 이관.
/// 정보 항목은 모듈이 주입한다: ShowFor()에 넘기는 IContentInfoProvider(모듈 뷰)가 내용을 만들고,
/// 없거나 실패하면 파일 기본 정보로 대체한다. 정보(H/W)·설정 모듈은 셸이 파일 경로가 없어
/// 애초에 ShowFor를 부르지 않는다(현행 동작 유지).
/// 입력(A86 — A58의 Shift를 X로 대체: X 홀드 = 오버레이 / 2초 = 오버레이 고정 / 2연타 = 사이드바 /
/// 열림 상태에서 X 1회 = 닫기)은 셸(MainWindow)의 상태 머신이 담당한다 —
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

    /// <summary>
    /// 인포 영역에 파일이 드랍됨 (A93) = "그 파일 열기". 셸이 OpenFile로 배선한다 —
    /// 콘텐츠가 없으면 라우터(A59)가 담당 모듈로 전환한 뒤 여는 기존 경로 그대로다.
    /// </summary>
    public event Action<string>? FileDropped;

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
        HideHint(); // A92 — 다시 열릴 때 안내가 처음부터 다시 보이게 상태를 비운다
    }

    /// <summary>
    /// 파일 없는 상태의 플레이스홀더 (A81 — 빈 모듈에서 기본 도크로 뜰 때):
    /// 보여줄 파일 정보가 없으므로 간단한 안내만 표시한다.
    /// A93: 드랍 안내(Drop a file here...)는 인포에 아무것도 표시 중이 아닐 때만 —
    /// 이 플레이스홀더가 정확히 그 상태라 여기서만 문구를 낸다(파일 정보 표시 중에는 없음).
    /// 진행 중이던 로드가 늦게 도착해 문구를 덮지 않게 캐시·시퀀스를 함께 무효화한다.
    /// 모드·안내 문구는 ShowFor와 동일하게 SetState가 별도로 반영한다.
    /// </summary>
    public void ShowPlaceholder()
    {
        InvalidateCache();
        Visibility = Visibility.Visible;
        InfoText.Text = "No file open\nDrop a file here to open it";
    }

    /// <summary>
    /// 패널 폭(전폭 대비 %) 지정 — 셸이 전 상태 공통 SidebarPercent(25, A116)를 넘긴다.
    /// 내부 별 분할이 셸 도크 컬럼과 같은 비율이어야 사이드바에서 픽셀 단위로 정렬되고,
    /// 경계 버튼 옆 안내 문구(A108 — RestColumn 기준 배치)의 x도 이 분할이 정한다.
    /// FileListOverlay에도 같은 메서드가 있다.
    /// </summary>
    public void SetPanelPercent(double percent)
    {
        PanelColumn.Width = new GridLength(percent, GridUnitType.Star);
        RestColumn.Width = new GridLength(100 - percent, GridUnitType.Star);
    }

    /// <summary>
    /// 인포 영역 드랍 = 그 파일 열기 (A93 드랍 규칙). 좌·중(탐색기 영역)의 무동작과 달리
    /// 여기만 실제 동작이 있다. 홀드 반투명 중에는 IsHitTestVisible=false라 여기로 안 온다.
    /// </summary>
    private void OnPanelDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true; // 창 전체 핸들러(콘텐츠 영역 규칙)가 다시 판정하지 않게
    }

    private async void OnPanelDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        var items = await e.DataView.GetStorageItemsAsync();
        var path = items.OfType<Windows.Storage.StorageFile>()
            .Select(f => f.Path)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (path is not null) FileDropped?.Invoke(path);
    }

    /// <summary>
    /// 표시 모드·고정 안내 반영 (A58). TranslucentOver = 오버레이(아크릴 반투명, A33 — A108
    /// 용어): 홀드 중이면 문구 없음, pinned(2초 홀드 고정)면 unpin 안내.
    /// OpaqueDocked = 사이드바(불투명 배경) + close 안내 —
    /// 실제 폭 차지(메인 축소)는 셸의 도크 컬럼이 담당하고 여기서는 시각·문구만 바꾼다.
    /// 상호작용(스크롤)은 고정·사이드바에서만 허용 — 홀드 중에는 아래 콘텐츠 클릭을 막지 않는다
    /// (기존 pinned 규칙 유지).
    /// A92(v0.115.0): 문구는 상시 표시가 아니라 잠깐 보였다 사라진다(아래 안내 문구 절 참고).
    /// A108(v0.135.0): 문구 위치는 패널 안이 아니라 경계 버튼 옆(XAML PinnedText 배치 참고).
    /// </summary>
    public void SetState(OverlayMode mode, bool pinned)
    {
        var docked = mode == OverlayMode.OpaqueDocked;
        OverlayBorder.Background = (Brush)Application.Current.Resources[
            docked ? "SolidBackgroundFillColorBaseBrush" : "OverlayAcrylicBrush"];
        OverlayBorder.IsHitTestVisible = IsOpen && (docked || pinned);
        if (IsOpen && (docked || pinned))
            ShowHint(docked
                ? OverlayHints.Docked(OverlayHints.InfoKey)
                : OverlayHints.Pinned(OverlayHints.InfoKey));
        else
            HideHint();
    }

    // ---------- 안내 문구 일시 표시 (A92, v0.115.0 — 문구·키 표기는 A107부터 OverlayHints가 단일 출처) ----------
    // ⚠️ FileListOverlay에 같은 이름의 상수·필드·메서드(표시 타이밍 장치)가 한 벌 더 있다.
    // 문구 문자열은 A107에서 OverlayHints로 모았지만 타이밍 장치는 여전히 두 벌 —
    // 한쪽을 고치면 반드시 다른 쪽도 맞출 것.
    // A108(v0.135.0): 표시 위치가 패널 안 → 경계 버튼 옆(세로 중앙)으로 이동 — XAML만 바뀌었고
    // 타이밍 장치는 그대로다.

    private const double HintOpacity = 0.6; // XAML PinnedText.Opacity와 같아야 한다(페이드 후 되돌릴 값)
    private static readonly TimeSpan HintHoldFor = TimeSpan.FromSeconds(2.5);      // 표시 시간(구현 시 결정)
    private static readonly TimeSpan HintFadeFor = TimeSpan.FromMilliseconds(300); // 페이드아웃 시간

    private DispatcherTimer? _hintTimer; // UI 스레드 타이머 (MainWindow.MakePinTimer·DriveStrip과 같은 방식)
    private Storyboard? _hintFade;
    private bool _hintVisible;    // 지금 "보여야 하는 상태"인가 — 매 SetState마다 되감지 않기 위한 기억
    private string? _hintText;    // 마지막으로 띄운 문구 — 내용이 바뀔 때만 다시 띄운다

    /// <summary>
    /// 안내를 잠깐 띄운다: 2.5초 표시 → 300ms 페이드아웃 → Collapsed.
    /// SetState는 상태 머신이 움직일 때마다 여러 번 불리므로, **표시 상태로 새로 진입했거나
    /// 문구가 바뀐 경우에만** 다시 띄우고 타이머를 되감는다(매번 재시작하면 영영 안 사라진다).
    /// </summary>
    private void ShowHint(string text)
    {
        if (_hintVisible && _hintText == text) return; // 이미 이 안내를 낸 뒤 — 그대로 둔다(사라진 채라도)
        _hintVisible = true;
        _hintText = text;

        StopHint(); // 돌던 타이머·페이드를 먼저 정리해야 아래 Opacity 대입이 애니메이션에 눌리지 않는다
        PinnedText.Text = text;
        PinnedText.Opacity = HintOpacity; // 직전 페이드로 0이 된 채 남아 있을 수 있다
        PinnedText.Visibility = Visibility.Visible;

        _hintTimer ??= CreateHintTimer();
        _hintTimer.Stop();  // DispatcherTimer는 반복 타이머 — Stop 후 Start로 확실히 되감는다
        _hintTimer.Start();
    }

    /// <summary>숨겨야 하는 상태(닫힘·도크도 고정도 아님) — 타이머·페이드를 즉시 멈추고 감춘다.</summary>
    private void HideHint()
    {
        _hintVisible = false;
        _hintText = null;
        StopHint();
        PinnedText.Visibility = Visibility.Collapsed;
    }

    private DispatcherTimer CreateHintTimer()
    {
        var timer = new DispatcherTimer { Interval = HintHoldFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // 반복 타이머라 Tick 안에서 반드시 멈춘다(MainWindow.MakePinTimer와 같은 관용구)
            FadeOutHint();
        };
        return timer;
    }

    /// <summary>Storyboard + DoubleAnimation(Opacity) — DriveStrip 마퀴와 같은 관용구.</summary>
    private void FadeOutHint()
    {
        var animation = new DoubleAnimation
        {
            From = HintOpacity,
            To = 0,
            Duration = new Duration(HintFadeFor),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, PinnedText);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var fade = new Storyboard();
        fade.Children.Add(animation);
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_hintFade, fade)) return; // 그새 다시 띄워졌다 — 감추면 안 된다
            PinnedText.Visibility = Visibility.Collapsed;
        };
        _hintFade = fade;
        fade.Begin();
    }

    private void StopHint()
    {
        _hintTimer?.Stop();
        _hintFade?.Stop(); // Stop은 Completed를 부르지 않는다 — 보류 중인 Collapsed도 함께 사라진다
        _hintFade = null;
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
