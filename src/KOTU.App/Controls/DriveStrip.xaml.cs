using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using KOTU.Core.Routing;
using KOTU.Core.Threading;
using KOTU.Module.Hardware;

namespace KOTU.App.Controls;

/// <summary>
/// 하단 바 드라이브 정보 줄 공용 컨트롤 (A22, v0.108.0).
/// 표시 규칙(사용자 확정): 파일이 열려 있지 않을 때만 표시 / 시스템의 모든 드라이브 /
/// "이름 · 종류 · 사용량 of 전체 (사용률%)" + 사용률 막대 / 넘치면 좌측으로 흐르는 무한 루프.
///
/// 스레드(A42): 드라이브 열거(DriveInfo)와 종류 조회(WMI)는 전용 워커에서 돌고 UI 스레드는
/// 결과로 항목을 그리기만 한다. 워커 결과를 UI 스레드에서 await하므로 반영은 자동으로
/// UI 스레드로 복귀한다. 종류(SSD/NVMe/USB)는 프로세스 1회 조회 캐시(PhysicalDiskKinds),
/// 용량·사용률은 30초 주기 갱신.
///
/// 숨겨진 동안에는 30초 타이머와 마퀴 애니메이션을 반드시 멈춘다(보이지 않는데 도는 낭비 금지).
/// </summary>
public sealed partial class DriveStrip : UserControl
{
    private const double ScrollPixelsPerSecond = 30; // 자동 스크롤 속도(사용자 확정)
    private const double LoopGap = 32;               // 사본 사이 간격 = XAML의 Margin 오른쪽 32와 같아야 한다
    private const double BarWidth = 60;              // 막대 규격(사용자 확정): 60 × 6, 반지름 3
    private const double BarHeight = 6;
    private const double BarRadius = 3;
    private const double WarnRatio = 0.9;            // 사용률 90% 이상이면 경고색
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private ModuleWorker? _worker;
    private DispatcherTimer? _timer; // UI 스레드 타이머(다른 뷰들과 같은 방식) — Tick도 UI 스레드
    private Storyboard? _marquee;
    private double _marqueeDistance;
    private bool _active;
    private int _seq; // 늦게 도착한 조회 결과 폐기(빠른 표시 on/off 대비)

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다(다른 뷰들과 같은 관용구).</summary>
    private ModuleWorker Worker =>
        _worker ??= new ModuleWorker("KOTU drive strip worker", ThreadPriority.BelowNormal);

    public DriveStrip()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed; // 셸이 SetActive(true)로 켠다 — 기본은 숨김
        Unloaded += (_, _) =>
        {
            SetActive(false);   // 타이머·애니메이션 정지 (뷰가 내려가도 도는 일이 없게)
            _worker?.Dispose(); // 진행 중 조회는 워커가 마저 끝내고 스레드 종료
            _worker = null;
        };
    }

    /// <summary>
    /// 표시 on/off. 셸이 "파일이 열려 있지 않은가"로 호출한다(A22 — v0.47.0과 반대).
    /// 꺼지면 30초 갱신과 자동 스크롤을 멈추고, 켜지면 즉시 1회 갱신한다.
    /// </summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        if (active)
        {
            _timer ??= CreateTimer();
            _timer.Start();
            Refresh();
        }
        else
        {
            _timer?.Stop();
            StopMarquee();
        }
    }

    private DispatcherTimer CreateTimer()
    {
        // 용량·사용률만 주기 갱신(30초) — 종류(WMI)는 프로세스 1회 캐시라 매번 다시 읽지 않는다.
        var timer = new DispatcherTimer { Interval = RefreshInterval };
        timer.Tick += (_, _) => Refresh();
        return timer;
    }

    /// <summary>드라이브 조회를 워커로 보내고 결과만 UI에 반영한다. 실패는 조용히 무시(부가 표시).</summary>
    private async void Refresh()
    {
        var seq = ++_seq;
        IReadOnlyList<DriveUsage> drives;
        try
        {
            drives = await Worker.Run(_ => DriveStatus.Collect(PhysicalDiskKinds.Lookup));
        }
        catch
        {
            return; // 워커 종료·조회 실패 — 표시는 부가 기능이라 흐름을 막지 않는다
        }

        if (seq != _seq || !_active) return; // 그새 숨겨졌거나 더 최신 조회가 있다
        Build(drives);
    }

    /// <summary>조회 결과로 항목을 다시 그린다(사본 2벌 — 마퀴 이음새용).</summary>
    private void Build(IReadOnlyList<DriveUsage> drives)
    {
        PrimaryItems.Children.Clear();
        LoopItems.Children.Clear();

        for (var i = 0; i < drives.Count; i++)
        {
            if (i > 0)
            {
                PrimaryItems.Children.Add(Divider());
                LoopItems.Children.Add(Divider());
            }
            PrimaryItems.Children.Add(Item(drives[i]));
            LoopItems.Children.Add(Item(drives[i])); // 같은 UIElement는 두 부모를 가질 수 없다 — 사본 생성
        }

        EvaluateMarquee();
    }

    /// <summary>드라이브 한 칸: "C: SSD 412 GB of 931 GB (44%)" + 그 오른쪽 사용률 막대.</summary>
    private static UIElement Item(DriveUsage drive)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 종류를 모르면 칸 자체를 생략한다 — 빈 괄호·빈 자리를 남기지 않는다(A22 폴백 규칙).
        panel.Children.Add(new TextBlock
        {
            Text = drive.Kind is { Length: > 0 } kind
                ? $"{drive.Name} {kind} {drive.Capacity}"
                : $"{drive.Name} {drive.Capacity}",
            FontSize = 12,
            Opacity = 0.6, // v0.47.0 드라이브 텍스트와 같은 톤
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(Bar(drive.Ratio));
        return panel;
    }

    /// <summary>사용률 막대: 60 × 6, 반지름 3. 90% 이상은 테마 경고색, 그 미만은 액센트색.</summary>
    private static UIElement Bar(double ratio)
    {
        var filled = Math.Clamp(ratio, 0, 1);
        var fill = new Border
        {
            Width = Math.Max(2, BarWidth * filled), // 거의 빈 드라이브도 막대가 보이게 최소 2
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarRadius),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeBrush(filled >= WarnRatio
                ? "SystemFillColorCautionBrush"   // 경고색은 테마 리소스 — 색을 코드에 박지 않는다
                : "AccentFillColorDefaultBrush"),
        };
        return new Border
        {
            Width = BarWidth,
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarRadius),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush("DividerStrokeColorDefaultBrush"), // 빈 부분(트랙)
            Child = fill,
        };
    }

    /// <summary>드라이브 사이 구분: 간격 + 옅은 세로선.</summary>
    private static UIElement Divider() => new Border
    {
        Width = 1,
        Height = 16,
        Margin = new Thickness(12, 0, 12, 0),
        Opacity = 0.5,
        VerticalAlignment = VerticalAlignment.Center,
        Background = ThemeBrush("DividerStrokeColorDefaultBrush"),
    };

    /// <summary>
    /// 테마 브러시 조회(다른 뷰들과 같은 인덱서 방식). 키가 없으면 인덱서가 던지므로
    /// 감싸서 투명으로 떨어뜨린다 — 색을 코드에 박아 대체하지 않는다.
    /// </summary>
    private static Brush ThemeBrush(string key)
    {
        try
        {
            if (Application.Current.Resources[key] is Brush brush) return brush;
        }
        catch
        {
            // 키 없음 — 아래 폴백
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    // ---------- 자동 스크롤(마퀴) ----------

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 넘치는 내용이 하단 바 밖으로 새면 안 된다 — 뷰포트 크기로 잘라낸다.
        Viewport.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };
        EvaluateMarquee(); // 창 크기가 바뀌면 넘침 여부를 다시 판정한다(A22)
    }

    private void OnItemsSizeChanged(object sender, SizeChangedEventArgs e) => EvaluateMarquee();

    /// <summary>내용이 표시 영역보다 넓을 때만 스크롤한다. 넘치지 않으면 고정 표시.</summary>
    private void EvaluateMarquee()
    {
        var viewport = Viewport.ActualWidth;
        var content = PrimaryItems.ActualWidth;
        if (!_active || viewport <= 0 || content <= viewport + 0.5)
        {
            StopMarquee();
            return;
        }
        StartMarquee(content + LoopGap); // 사본 사이 간격까지 밀어야 이음새가 맞는다
    }

    private void StartMarquee(double distance)
    {
        // 같은 거리로 이미 돌고 있으면 다시 시작하지 않는다(크기 변화마다 튀지 않게).
        if (_marquee is not null && Math.Abs(_marqueeDistance - distance) < 0.5) return;

        StopMarquee();
        _marqueeDistance = distance;
        LoopItems.Visibility = Visibility.Visible;

        var animation = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = new Duration(TimeSpan.FromSeconds(distance / ScrollPixelsPerSecond)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, TrackShift);
        Storyboard.SetTargetProperty(animation, "X");

        _marquee = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        _marquee.Children.Add(animation);
        _marquee.Begin();
    }

    private void StopMarquee()
    {
        _marquee?.Stop();
        _marquee = null;
        _marqueeDistance = 0;
        LoopItems.Visibility = Visibility.Collapsed;
        TrackShift.X = 0;
    }
}
