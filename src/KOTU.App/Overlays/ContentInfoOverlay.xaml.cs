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
    private IReadOnlyList<ContentInfoItem>? _cacheItems; // A150: 문자열 → 라벨·값 행 목록

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
        ShowMessage("No file open\nDrop a file here to open it");
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
    /// A129(v0.156.0): overSwapChain = 중앙이 스왑체인(비디오·오디오) — 반투명이 아크릴 대신
    /// 반투명 단색 폴백이 된다(선택은 OverlayBackdrop.Pick 한 곳). 셸이 ApplyOverlayStates에서 매번 민다.
    /// </summary>
    public void SetState(OverlayMode mode, bool pinned, bool overSwapChain)
    {
        var docked = mode == OverlayMode.OpaqueDocked;
        OverlayBorder.Background = OverlayBackdrop.Pick(docked, overSwapChain);
        OverlayBorder.IsHitTestVisible = IsOpen && (docked || pinned);
        if (IsOpen && (docked || pinned))
            ShowHint(docked
                ? OverlayHints.Docked(OverlayHints.InfoKey)
                : OverlayHints.Pinned(OverlayHints.InfoKey));
        else
            HideHint();
    }

    // ---------- 안내 문구 일시 표시 (A92, v0.115.0 — 문구·키 표기는 A107부터 OverlayHints가 단일 출처) ----------
    // ⚠️ FileListOverlay·SidePanelHost(A119)에 같은 상수·필드·메서드(표시 타이밍 장치)가 한 벌씩
    // 더 있다. 문구 문자열은 A107에서 OverlayHints로 모았지만 타이밍 장치는 세 벌 —
    // 한쪽을 고치면 반드시 나머지도 맞출 것. A133(v0.155.0)부터는 **판(다크 반투명 Border) 규격**도
    // 세 벌 공통이다: Background #CC202020 · CornerRadius 4 · Padding 10,6 · 글씨 White ·
    // 요소 Opacity 1(A12 칩과 같은 값 — 출처 VideoPlayerView.xaml StartOverlay).
    // A108(v0.135.0): 표시 위치가 패널 안 → 경계 버튼 옆(세로 중앙)으로 이동 — XAML만 바뀌었고
    // 타이밍 장치는 그대로다.
    // A133: 표시·숨김·페이드의 대상 요소가 PinnedText → PinnedPlate(감싼 판)로 올라갔다.
    // 문구 대입만 PinnedText가 받는다. Opacity 애니메이션은 UIElement 공통 속성이라 대상이
    // TextBlock이든 Border든 같은 경로("Opacity")로 성립한다(실기기 확인 포인트 — CI 검증 불가).

    private const double HintOpacity = 1; // XAML PinnedPlate.Opacity와 같아야 한다(페이드 후 되돌릴 값 — A133에서 0.6 → 1)
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
        PinnedText.Text = text;                 // 문구는 판 안의 TextBlock이 받는다(A133)
        PinnedPlate.Opacity = HintOpacity;      // 직전 페이드로 0이 된 채 남아 있을 수 있다
        PinnedPlate.Visibility = Visibility.Visible;

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
        PinnedPlate.Visibility = Visibility.Collapsed; // 판째로 감춘다(A133)
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

    /// <summary>
    /// Storyboard + DoubleAnimation(Opacity) — DriveStrip 마퀴와 같은 관용구.
    /// A133: 대상이 판(PinnedPlate)이라 문구와 배경이 한 덩어리로 사라진다(같은 "Opacity" 경로).
    /// </summary>
    private void FadeOutHint()
    {
        var animation = new DoubleAnimation
        {
            From = HintOpacity,
            To = 0,
            Duration = new Duration(HintFadeFor),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, PinnedPlate);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var fade = new Storyboard();
        fade.Children.Add(animation);
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_hintFade, fade)) return; // 그새 다시 띄워졌다 — 감추면 안 된다
            PinnedPlate.Visibility = Visibility.Collapsed;
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
        _cacheItems = null;
        _activePath = null;
        _seq++;
    }

    /// <summary>모듈 제공 정보(IContentInfoProvider) 우선, 없으면 파일 기본 정보. 파일별 1회 캐시.</summary>
    private async Task LoadAsync(string path, IContentInfoProvider? provider)
    {
        if (_cachePath == path && _cacheItems is not null)
        {
            RenderItems(_cacheItems);
            return;
        }

        var seq = ++_seq;
        _activePath = path;
        ShowMessage("Loading info...");

        IReadOnlyList<ContentInfoItem>? items = null;
        try
        {
            if (provider is not null)
                items = await provider.GetContentInfoAsync();
        }
        catch
        {
            // 모듈 정보 실패 → 아래 파일 기본 정보로 대체
        }
        items ??= BuildBasicFileInfo(path);

        if (seq != _seq || _activePath != path) return; // 그새 파일이 바뀜
        _cachePath = path;
        _cacheItems = items;
        RenderItems(items);
    }

    /// <summary>
    /// 셸 폴백(문서·압축 등 미구현 모듈·모듈 정보 실패) — A150에서 개행 문자열을 라벨·값 행으로
    /// 이식했다. 표시 항목(이름·크기·수정일·폴더)과 값 포맷은 종전 그대로다.
    /// </summary>
    private static IReadOnlyList<ContentInfoItem> BuildBasicFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new[]
            {
                new ContentInfoItem("File", info.Name),
                new ContentInfoItem("Size", ExplorerListing.FormatSize(info.Length)),
                new ContentInfoItem("Modified", $"{info.LastWriteTime:yyyy-MM-dd HH:mm}"),
                new ContentInfoItem("Folder", info.DirectoryName ?? string.Empty),
            };
        }
        catch (Exception ex)
        {
            return new[]
            {
                new ContentInfoItem("File", Path.GetFileName(path)),
                new ContentInfoItem("Info", "Unavailable: " + ex.Message),
            };
        }
    }

    // ---------- 정보 행 렌더 (A150 — 하드웨어 우 패널 라벨·값 2열 관용구 준용) ----------

    // 치수 출처 = HardwareView의 A172(v0.165.0) 상수(SpecLabelFontSize·SpecValueFontSize·
    // SpecLabelWidth) — 같은 25% 우측 구획이라 같은 값을 쓴다. 저쪽이 바뀌면 여기도 맞출 것.
    // 그룹 제목 행(하드웨어의 섹션 Title)은 두지 않는다 — 정보 패널은 행이 십수 개뿐이라
    // Separator(빈 행)의 여백만으로 그룹이 구분된다(계약 주석의 A150 구현 시 결정).
    private const double ItemLabelFontSize = 11;
    private const double ItemValueFontSize = 11;
    private const double ItemLabelWidth = 96;
    private const double SeparatorHeight = 8; // 그룹 사이 빈 행 높이(구현 시 결정)

    /// <summary>문구 전용 표시(플레이스홀더·Loading·실패 안내) — 행 목록과 배타 토글.</summary>
    private void ShowMessage(string text)
    {
        InfoText.Text = text;
        InfoText.Visibility = Visibility.Visible;
        InfoRows.Visibility = Visibility.Collapsed;
    }

    /// <summary>라벨·값 행 목록 표시 — 문구와 배타 토글. 행 Grid는 코드가 만든다(하드웨어 Render 관용구).</summary>
    private void RenderItems(IReadOnlyList<ContentInfoItem> items)
    {
        InfoRows.Children.Clear();
        foreach (var item in items)
            InfoRows.Children.Add(item.IsSeparator
                ? new Grid { Height = SeparatorHeight }
                : MakeItemRow(item));
        InfoText.Visibility = Visibility.Collapsed;
        InfoRows.Visibility = Visibility.Visible;
    }

    /// <summary>라벨(고정폭·흐리게) + 값(줄바꿈·선택 가능) 한 줄 — HardwareView.MakeItemRow와 같은 꼴.</summary>
    private static Grid MakeItemRow(ContentInfoItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ItemLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = ItemLabelFontSize,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 12, 2),
        };
        var value = new TextBlock
        {
            Text = item.Value,
            FontSize = ItemValueFontSize,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }
}
