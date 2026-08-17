using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace KOTU.App.Overlays;

/// <summary>
/// 모듈 고유 좌/우 패널 콘텐츠(ISidePanelProvider — A119, v0.145.0)의 셸 쪽 호스트.
/// 파일 오버레이(FileListOverlay·ContentInfoOverlay)가 담당하는 패널 자리에, 계약을 구현한 뷰
/// (지금은 정보 모듈뿐)의 요소를 대신 얹는다 — 배경(반투명 아크릴/불투명)·홀드 중 클릭 통과·
/// 안내 문구(2.5초 + 페이드, OverlayHints 단일 출처)·내부 별 분할(SetPanelPercent)을 파일
/// 오버레이와 <b>같은 규칙으로 재현</b>한다. 배경·상태 적용이 두 오버레이 컨트롤 내부에 있어
/// 셸 층으로 뽑는 대신 이 호스트에 재현하는 쪽을 택했다(A119 구현 결정 — diff 최소).
/// 입력(F1/F2·2연타·Enter·경계 버튼)은 종전대로 셸(MainWindow) 상태 머신이 담당하고,
/// 이 컨트롤은 ShowContent/Hide/SetState/SetPanelPercent만 받는다.
/// 좌/우 방향(패널이 어느 가장자리인가·문구 키 표기)은 Initialize가 정한다 — XAML에는
/// 속성 없이 선언하고 MainWindow 생성자가 1회 조립한다(코드 전용 컨트롤 — 자체 XAML 없음).
/// 콘텐츠 요소의 소유·갱신은 모듈 뷰 몫이다. 셸은 모듈 전환 시 ClearContent로 반드시 비운다
/// (안 비우면 이전 모듈 패널이 다음 모듈 위에 남는다 — A59급 회귀 방지).
/// </summary>
public sealed partial class SidePanelHost : UserControl
{
    private readonly ColumnDefinition _panelColumn = new() { Width = new GridLength(25, GridUnitType.Star) };
    private readonly ColumnDefinition _restColumn = new() { Width = new GridLength(75, GridUnitType.Star) };

    /// <summary>모듈 콘텐츠 슬롯 — 참조가 바뀔 때만 교체한다(같은 요소 재대입 없음).</summary>
    private readonly ContentControl _content = new()
    {
        IsTabStop = false,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };

    /// <summary>패널 배경·경계선 — 파일 오버레이의 OverlayBorder/PanelBorder에 해당.</summary>
    private readonly Border _border = new() { IsHitTestVisible = false };

    /// <summary>안내 문구 — 파일 오버레이의 PinnedText에 해당(A133부터 판 안에 든다: 문구 대입 전용).</summary>
    private readonly TextBlock _pinnedText = new()
    {
        FontSize = 11,
        // 다크 판 위라 테마와 무관하게 밝은 글씨 고정 — 파일 오버레이 XAML의 Foreground="White"와 같은 값.
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
    };

    /// <summary>
    /// 안내 문구의 다크 반투명 판 — 파일 오버레이 XAML의 PinnedPlate에 해당(A133, v0.155.0).
    /// 규격은 A12 비디오 시작 오버레이 칩과 같은 값(출처 = KOTU.Module.Video/VideoPlayerView.xaml의
    /// StartOverlay): Background #CC202020 · CornerRadius 4 · Padding 10,6. 요소 자체는 불투명
    /// (Opacity = HintOpacity = 1) — 반투명은 배경 브러시 알파(CC)가 담당한다.
    /// 표시·숨김·페이드 대상이 이 판이다(경계 버튼 옆이라는 A108 배치 규칙도 판이 물려받는다).
    /// 좌/우 방향 정렬·Margin·컬럼과 Child 연결은 Initialize가 1회 조립한다 — 필드 초기화자는
    /// 다른 인스턴스 필드(_pinnedText)를 참조할 수 없다(CS0236).
    /// </summary>
    private readonly Border _pinnedPlate = new()
    {
        Visibility = Visibility.Collapsed,
        Opacity = HintOpacity,
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 6, 10, 6),
    };

    private string _key = OverlayHints.ListKey; // 힌트 키 표기 — Initialize가 좌/우로 확정
    private bool _built;

    /// <summary>패널이 화면에 떠 있는지 — 셸의 도크 폭·경계 버튼 계산에 쓴다(오버레이들과 동일 판정).</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// 좌/우 방향 1회 조립(MainWindow 생성자). panelOnRight=false — 패널이 좌측 가장자리
    /// (FileListOverlay 자리), true — 우측 가장자리(ContentInfoOverlay 자리). 문구 위치는 A108
    /// 규칙 그대로: 경계 버튼의 화면 안쪽 옆(좌 = 버튼 오른쪽 / 우 = 버튼 왼쪽), Margin 14 =
    /// 버튼 걸침 10(EdgeButtonOverlap) + 간격 4 — 두 오버레이의 XAML 값과 같아야 한다.
    /// </summary>
    public void Initialize(bool panelOnRight)
    {
        if (_built) return;
        _built = true;
        _key = panelOnRight ? OverlayHints.InfoKey : OverlayHints.ListKey;

        var root = new Grid();
        root.ColumnDefinitions.Add(panelOnRight ? _restColumn : _panelColumn);
        root.ColumnDefinitions.Add(panelOnRight ? _panelColumn : _restColumn);

        // 배경·경계선은 파일 오버레이의 XAML 기본값과 동일 구성 — 브러시 키 조회는
        // ContentInfoOverlay.SetState·MainWindow.Divider와 같은 저장소 관용구다.
        _border.Background = (Brush)Application.Current.Resources["OverlayAcrylicBrush"];
        _border.BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        _border.BorderThickness = panelOnRight ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        _border.Child = _content;
        Grid.SetColumn(_border, panelOnRight ? 1 : 0);
        root.Children.Add(_border);

        // A133: 위치 규칙(정렬·Margin·컬럼)은 판이 그대로 물려받는다 — Margin 14는 판의 바깥
        // 모서리 기준이고 글자는 칩 패딩만큼 안쪽에 앉는다(두 오버레이 XAML과 같은 배치).
        _pinnedPlate.Child = _pinnedText;
        _pinnedPlate.HorizontalAlignment = panelOnRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _pinnedPlate.Margin = panelOnRight ? new Thickness(0, 0, 14, 0) : new Thickness(14, 0, 0, 0);
        Grid.SetColumn(_pinnedPlate, panelOnRight ? 0 : 1);
        root.Children.Add(_pinnedPlate);

        Content = root;
    }

    /// <summary>
    /// 모듈 콘텐츠를 얹고 표시한다. 같은 참조면 슬롯을 건드리지 않는다(뷰가 매번 같은 인스턴스를
    /// 돌려주는 계약 전제 — 재대입 자체가 없어 reparent류 문제가 원천적으로 없다).
    /// content가 null이면(그 쪽 패널을 안 내주는 뷰) 띄우지 않는다.
    /// </summary>
    public void ShowContent(UIElement? content)
    {
        if (content is null)
        {
            Hide();
            return;
        }
        if (!ReferenceEquals(_content.Content, content)) _content.Content = content;
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _border.IsHitTestVisible = false;
        HideHint(); // 다시 열릴 때 안내가 처음부터 다시 보이게 상태를 비운다(A92 — 오버레이들과 동일)
    }

    /// <summary>
    /// 콘텐츠 참조를 끊고 내린다 — 모듈 전환 시 셸(ClearModulePanels)이 부른다. 요소가 트리에서
    /// 빠져야 이전 뷰의 패널이 잔존하지 않고, 뷰·요소가 함께 수거된다(수명은 뷰 소유).
    /// </summary>
    public void ClearContent()
    {
        _content.Content = null;
        Hide();
    }

    /// <summary>
    /// 패널 폭(전폭 대비 %) 지정 — 셸이 전 상태 공통 SidebarPercent(25, A116)를 넘긴다.
    /// 내부 별 분할이 셸 도크 컬럼과 같은 비율이어야 사이드바에서 픽셀 단위로 정렬된다
    /// (FileListOverlay·ContentInfoOverlay의 같은 이름 메서드와 동일 규칙).
    /// </summary>
    public void SetPanelPercent(double percent)
    {
        _panelColumn.Width = new GridLength(percent, GridUnitType.Star);
        _restColumn.Width = new GridLength(100 - percent, GridUnitType.Star);
    }

    /// <summary>
    /// 표시 모드·고정 안내 반영 — ContentInfoOverlay.SetState와 같은 규칙(A58/A108):
    /// TranslucentOver = 오버레이(아크릴 반투명, 홀드 중 문구 없음·클릭 통과, pinned면 안내) /
    /// OpaqueDocked = 사이드바(불투명 배경 + 안내). 실제 폭 차지는 셸 도크 컬럼 담당.
    /// </summary>
    public void SetState(OverlayMode mode, bool pinned)
    {
        var docked = mode == OverlayMode.OpaqueDocked;
        _border.Background = (Brush)Application.Current.Resources[
            docked ? "SolidBackgroundFillColorBaseBrush" : "OverlayAcrylicBrush"];
        _border.IsHitTestVisible = IsOpen && (docked || pinned);
        if (IsOpen && (docked || pinned))
            ShowHint(docked ? OverlayHints.Docked(_key) : OverlayHints.Pinned(_key));
        else
            HideHint();
    }

    // ---------- 안내 문구 일시 표시 (A92 — 문구·키 표기는 OverlayHints 단일 출처) ----------
    // ⚠️ FileListOverlay·ContentInfoOverlay에 같은 타이밍 장치가 있다(A119부터 세 벌) —
    // 한쪽을 고치면 반드시 나머지도 맞출 것. 상수·흐름은 두 오버레이와 동일해야 한다.
    // A133(v0.155.0)부터는 **판(다크 반투명 Border) 규격**도 세 벌 공통이다:
    // Background #CC202020 · CornerRadius 4 · Padding 10,6 · 글씨 White · 요소 Opacity 1
    // (A12 칩과 같은 값 — 출처 VideoPlayerView.xaml StartOverlay). 표시·숨김·페이드 대상은
    // 문구가 아니라 판(_pinnedPlate)이고, _pinnedText는 문구 대입만 받는다. Opacity 애니메이션은
    // UIElement 공통 속성이라 대상이 TextBlock이든 Border든 같은 경로("Opacity")로 성립한다
    // (실기기 확인 포인트 — CI 검증 불가). 판을 되돌려야 하면 _pinnedPlate 참조를 _pinnedText로
    // 되돌리고 판 필드를 지우면 A92 원형 동작이다 — 타이밍 장치는 손대지 않는다.

    private const double HintOpacity = 1; // _pinnedPlate 초기값과 같아야 한다(페이드 후 되돌릴 값 — A133에서 0.6 → 1)
    private static readonly TimeSpan HintHoldFor = TimeSpan.FromSeconds(2.5);      // 표시 시간(A92)
    private static readonly TimeSpan HintFadeFor = TimeSpan.FromMilliseconds(300); // 페이드아웃 시간

    private DispatcherTimer? _hintTimer;
    private Storyboard? _hintFade;
    private bool _hintVisible;    // 지금 "보여야 하는 상태"인가 — 매 SetState마다 되감지 않기 위한 기억
    private string? _hintText;    // 마지막으로 띄운 문구 — 내용이 바뀔 때만 다시 띄운다

    /// <summary>안내를 잠깐 띄운다: 2.5초 표시 → 300ms 페이드아웃 → Collapsed (오버레이들과 동일 규칙).</summary>
    private void ShowHint(string text)
    {
        if (_hintVisible && _hintText == text) return; // 이미 이 안내를 낸 뒤 — 그대로 둔다(사라진 채라도)
        _hintVisible = true;
        _hintText = text;

        StopHint(); // 돌던 타이머·페이드를 먼저 정리해야 아래 Opacity 대입이 애니메이션에 눌리지 않는다
        _pinnedText.Text = text;                // 문구는 판 안의 TextBlock이 받는다(A133)
        _pinnedPlate.Opacity = HintOpacity;     // 직전 페이드로 0이 된 채 남아 있을 수 있다
        _pinnedPlate.Visibility = Visibility.Visible;

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
        _pinnedPlate.Visibility = Visibility.Collapsed; // 판째로 감춘다(A133)
    }

    private DispatcherTimer CreateHintTimer()
    {
        var timer = new DispatcherTimer { Interval = HintHoldFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // 반복 타이머라 Tick 안에서 반드시 멈춘다(MainWindow.MakePinTimer 관용구)
            FadeOutHint();
        };
        return timer;
    }

    /// <summary>
    /// Storyboard + DoubleAnimation(Opacity) — 두 오버레이·DriveStrip 마퀴와 같은 관용구.
    /// A133: 대상이 판(_pinnedPlate)이라 문구와 배경이 한 덩어리로 사라진다(같은 "Opacity" 경로).
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
        Storyboard.SetTarget(animation, _pinnedPlate);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var fade = new Storyboard();
        fade.Children.Add(animation);
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_hintFade, fade)) return; // 그새 다시 띄워졌다 — 감추면 안 된다
            _pinnedPlate.Visibility = Visibility.Collapsed;
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
}
