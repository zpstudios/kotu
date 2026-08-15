using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace KOTU.Module.Document;

/// <summary>
/// A115(v0.142.0): 에디터 장식 레이어 — ① 각 줄 폰트 상단·하단 경계의 라인 가이드
/// ② 비가시 문자 표시(줄바꿈 ¶ · 파일 끝 ·EOF). 캔버스는 EditorBox와 같은 칸에 z 위로 겹치고
/// IsHitTestVisible=False라 포커스·입력·편집(A113 더티/저장 흐름)에 일절 관여하지 않는다.
///
/// <b>좌표 출처</b> = TextBox.GetRectFromCharacterIndex(WinAppSDK 공식 API) — 반환 Rect는
/// TextBox 로컬(뷰포트) 좌표로 스크롤을 이미 반영한다. 줄 위치·높이를 전부 이 실측값으로
/// 그리므로 한글·이모지 혼재로 줄 높이가 들쭉날쭉해도 가이드가 어긋나지 않는다 — TextBox에는
/// LineHeight·LineStackingStrategy가 없어(TextBlock 전용) 균일 줄 높이를 강제할 수 없고,
/// 실측이 유일한 정합 경로다(A115 조사 확정).
///
/// <b>실패 안전(설계 의무)</b>: 내부 ScrollViewer(템플릿 ContentElement) 취득은 기본 템플릿
/// 구조 의존이라 WinAppSDK 업데이트에 깨질 수 있는 부류다(v0.113.1 지연 로딩 스타일 사례와
/// 같은 급) — 취득 실패·렌더 중 예외는 장식만 조용히 끄고(Disable) 편집 본기능은 그대로 둔다.
/// 렌더는 뷰포트 안 줄만 걷고(첫 표시 인덱스 이진 탐색 + 줄 단위 지수/이진 전진),
/// TextChanged에서는 플래그만 세운다(A113 디바운스 경로에 무게를 얹지 않는다).
/// </summary>
internal sealed class EditorDecor
{
    // ---- 체감 조정 지점(색·불투명도·글리프 전부 여기 한 곳) ----
    // 가이드는 본문 위에 얹히는 레이어라 아주 옅어야 한다(사양 ③) — 0.08이면 읽기 방해 없음 판정.
    private const double GuideOpacity = 0.08;
    private const double MarkerOpacity = 0.25;
    private const double MarkerFontSize = 12;
    private const string NewlineGlyph = "¶";
    private const string EofGlyph = "·EOF";

    private const int MaxLines = 600;    // 방어: 4K 세로 화면도 수백 줄이 상한 — 그 이상은 비정상
    private const double YEpsilon = 0.5; // 같은 시각적 줄 판정의 y 허용 오차(px)

    private readonly FrameworkElement _themeSource;
    private readonly TextBox _editor;
    private readonly Canvas _canvas;

    private ScrollViewer? _scroll; // 템플릿 내부 ContentElement — 표시 후 지연 취득(PdfPane 선례)
    private bool _disabled;        // 폴백: 한 번 끄면 뷰 수명 동안 유지(예외·크래시 금지)
    private int _hookAttempts;     // 레이아웃이 끝났는데도 ScrollViewer가 없으면 포기하는 카운터
    private bool _pending;         // "다음 레이아웃 반영 후 렌더" 예약 — LayoutUpdated가 소비
    private string? _text;         // Text 게터는 호출마다 문자열을 마샬링한다 — 4MB 재복사 방지 캐시
    private SolidColorBrush _brush;

    // 좌표 기준 자가 확인: 공식 예제상 Rect는 뷰포트 기준이지만, 만에 하나 콘텐츠(문서) 기준으로
    // 나오는 환경이면 세로 오프셋을 빼서 맞춘다. 충분히 스크롤된 첫 순간 1회 판별하고 고정한다.
    private bool _originChecked;
    private bool _contentRelative;
    private double _yShift; // 이번 패스의 y 보정값(뷰포트 기준이면 0)

    private readonly List<Rectangle> _guides = [];  // 수평선 풀 — 패스마다 재사용
    private readonly List<TextBlock> _markers = []; // ¶·EOF 풀
    private int _guidesUsed;
    private int _markersUsed;

    public EditorDecor(FrameworkElement themeSource, TextBox editor, Canvas canvas)
    {
        _themeSource = themeSource;
        _editor = editor;
        _canvas = canvas;
        _brush = MakeBrush();

        _editor.TextChanged += (_, _) => { _text = null; Invalidate(); }; // 플래그만 — 무게 금지(A113)
        _editor.SizeChanged += (_, _) => { UpdateClip(); Invalidate(); };
        // 트리 레이아웃마다 불리지만 플래그 검사뿐이라 상시 구독 비용이 없다. 텍스트·크기 변경은
        // 새 레이아웃이 반영된 뒤에 재야 해서(rect가 그 레이아웃을 읽는다) 이 시점으로 미룬다.
        _editor.LayoutUpdated += (_, _) =>
        {
            if (!_pending) return;
            _pending = false;
            Render();
        };
        // 스크롤은 레이아웃 패스 없이(컴포지션) 움직인다 — ViewChanged 구독은 EnsureScrollHook에서.
        _themeSource.ActualThemeChanged += (_, _) =>
        {
            _brush = MakeBrush();
            foreach (var guide in _guides) guide.Fill = _brush;
            foreach (var marker in _markers) marker.Foreground = _brush;
            Render();
        };
    }

    /// <summary>다크 = 흰 선, 라이트 = 검은 선 — 테마 파생 한 쌍(불투명도는 위 상수가 정한다).</summary>
    private SolidColorBrush MakeBrush() =>
        new(_themeSource.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black);

    /// <summary>텍스트·크기·모드 변경 공용 진입점 — 다음 레이아웃 반영 후 다시 그린다.</summary>
    public void Invalidate()
    {
        if (!_disabled) _pending = true;
    }

    /// <summary>즉시 렌더(스크롤·테마 경로). 어떤 예외든 장식만 끈다 — 에디터 본기능 무영향.</summary>
    private void Render()
    {
        if (_disabled) return;
        try
        {
            RenderCore();
        }
        catch
        {
            Disable(); // 함정 1: 실패해도 그 장식만 안 돈다 — 예외를 밖으로 내보내지 않는다
        }
    }

    private void Disable()
    {
        _disabled = true;
        _pending = false;
        try
        {
            ClearVisual();
        }
        catch
        {
            // 최후 방어 — 더는 아무것도 하지 않는다(장식은 껐고 편집기는 무관하다)
        }
    }

    private void RenderCore()
    {
        if (_editor.Visibility != Visibility.Visible)
        {
            ClearVisual(); // PDF 모드·빈 화면 — 장식 없음(사양)
            return;
        }
        var vw = _editor.ActualWidth;
        var vh = _editor.ActualHeight;
        var text = _text ??= _editor.Text;
        if (text.Length == 0 || vw <= 0 || vh <= 0)
        {
            ClearVisual(); // 0바이트 파일은 잴 문자가 없어 EOF 표지도 못 그린다(한계 — 보고됨)
            return;
        }
        if (!EnsureScrollHook())
        {
            ClearVisual();
            return;
        }

        _yShift = 0;
        if (_scroll is { } scroll)
        {
            if (!_originChecked && scroll.VerticalOffset > 48)
            {
                // 스크롤됐는데 첫 문자 y가 위로 밀려나지 않았다면 콘텐츠 기준 좌표다(자가 확인 1회)
                _contentRelative = _editor.GetRectFromCharacterIndex(0, false).Y > -YEpsilon;
                _originChecked = true;
            }
            if (_contentRelative) _yShift = -scroll.VerticalOffset;
        }

        BeginPass();
        var len = text.Length;
        var pad = _editor.Padding;
        var idx = FirstVisibleIndex(len);
        var prevY = double.NaN; // 직전에 그은 선 y — 밑변과 다음 줄 윗변이 맞닿으면 한 선으로 병합
        for (var lines = 0; idx >= 0 && lines < MaxLines; lines++)
        {
            var rect = RectOf(idx);
            if (rect.Height <= 0 || rect.Height > vh * 2) break; // 이상값 방어 — 이번 패스 중단
            if (rect.Y >= vh) break;                             // 뷰포트 아래 — 끝
            DrawGuide(rect.Y, vw, vh, pad, ref prevY);
            DrawGuide(rect.Y + rect.Height, vw, vh, pad, ref prevY);

            var next = NextLineStart(idx, rect.Y + rect.Height, len);
            if (next < 0)
            {
                DrawEnd(text, len, rect, vw, vh, pad, ref prevY); // 문서 마지막 줄 — 끝 개행 ¶·EOF
                break;
            }
            // 다음 줄 직전 문자가 개행이면 하드 개행(¶), 아니면 자동 줄바꿈(표시 없음 — 실제 바이트가 아니다)
            if (IsNewline(text[next - 1])) DrawNewlineGlyph(RectOf(next - 1), vh);
            idx = next;
        }
        EndPass();
    }

    /// <summary>문서 마지막 시각적 줄: 끝 개행의 ¶ + (개행으로 끝나면) 빈 마지막 줄 가이드 + EOF 표지.</summary>
    private void DrawEnd(string text, int len, Rect lastLine, double vw, double vh, Thickness pad, ref double prevY)
    {
        if (IsNewline(text[len - 1]))
        {
            // 파일이 개행으로 끝난다 — 캐럿이 갈 수 있는 빈 마지막 줄이 하나 더 있다.
            // 빈 줄에는 잴 문자가 없어 개행 문자의 셀 높이로 근사한다(혼재 글꼴 줄이면 수 px 오차 허용).
            var newlineRect = RectOf(len - 1);
            DrawNewlineGlyph(newlineRect, vh);
            var top = lastLine.Y + lastLine.Height;
            var height = newlineRect.Height > 0 ? newlineRect.Height : lastLine.Height;
            DrawGuide(top + height, vw, vh, pad, ref prevY); // 빈 줄 밑변(윗변 = 직전 줄 밑변, 이미 그었다)
            DrawMarker(EofGlyph, pad.Left + 2, top, vh);
        }
        else
        {
            var trailing = RectOfTrailing(len - 1); // 마지막 문자의 뒤쪽 모서리 = 줄 끝
            DrawMarker(EofGlyph, trailing.X + 4, trailing.Y, vh);
        }
    }

    // ---------- 줄 탐색(뷰포트 한정 — 전 문서를 걷지 않는다) ----------

    /// <summary>뷰포트에 조금이라도 보이는 첫 문자 인덱스(이진 탐색 — y는 인덱스에 대해 단조).</summary>
    private int FirstVisibleIndex(int len)
    {
        int lo = 0, hi = len - 1, first = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var rect = RectOf(mid);
            if (rect.Y + rect.Height > 0)
            {
                first = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return first;
    }

    /// <summary>
    /// 다음 시각적 줄의 첫 문자 인덱스(-1 = 문서 끝까지 같은 줄). 기준은 현재 줄 기준 문자의
    /// <b>밑변</b>(refBottom) — 윗변끼리 비교하면 혼재 글꼴 줄에서 같은 줄의 키 작은 글자
    /// (이모지 옆 일반 글자는 셀 윗변이 더 낮다)를 다음 줄로 오판한다. 같은 줄 셀의 윗변은
    /// 모두 줄 박스 밑변보다 위, 다음 줄 셀의 윗변은 모두 그 아래이므로 이 판정은 인덱스에
    /// 대해 단조다. 지수 확장으로 다음 줄 이후 지점을 찾고 (low, high] 구간을 이진 탐색한다 —
    /// 시각적 줄 하나당 rect 호출 십수 회 수준이라 뷰포트 전체도 수백 회에 그친다.
    /// </summary>
    private int NextLineStart(int index, double refBottom, int len)
    {
        if (index >= len - 1) return -1;
        var threshold = refBottom - 1.0; // 균일 줄(다음 윗변 == 현 밑변)이 경계에서 새지 않게 1px 여유
        var low = index; // 아직 현재 줄
        var high = -1;   // 다음 줄 이후
        var step = 32;
        while (high < 0)
        {
            var probe = Math.Min(len - 1, low + step);
            if (TopOf(probe) > threshold)
            {
                high = probe;
                break;
            }
            if (probe == len - 1) return -1; // 끝까지 같은 줄
            low = probe;
            step *= 2;
        }
        while (low + 1 < high) // 처음으로 경계 아래로 내려가는 인덱스 = 다음 줄 시작
        {
            var mid = low + (high - low) / 2;
            if (TopOf(mid) > threshold) high = mid;
            else low = mid;
        }
        return high;
    }

    // ---------- 그리기(요소 풀 재사용) ----------

    private void DrawGuide(double y, double vw, double vh, Thickness pad, ref double prevY)
    {
        if (y < -1 || y > vh + 1) return; // 뷰포트 밖(클립도 있지만 요소 자체를 아낀다)
        var rounded = Math.Round(y);
        if (!double.IsNaN(prevY) && Math.Abs(rounded - prevY) < 0.75) return; // 맞닿은 밑변·윗변 병합
        prevY = rounded;
        var guide = TakeGuide();
        guide.Width = Math.Max(0, vw - pad.Left - pad.Right);
        Canvas.SetLeft(guide, pad.Left);
        Canvas.SetTop(guide, rounded);
    }

    private void DrawNewlineGlyph(Rect rect, double vh)
    {
        if (rect.Height <= 0) return; // rect를 못 얻은 개행은 건너뛴다(어설픈 근사 배치 금지 — 사양 ③)
        DrawMarker(NewlineGlyph, rect.X + 1, rect.Y, vh);
    }

    private void DrawMarker(string glyph, double x, double y, double vh)
    {
        if (y > vh || y < -30) return;
        var marker = TakeMarker();
        marker.Text = glyph;
        Canvas.SetLeft(marker, Math.Round(x));
        Canvas.SetTop(marker, Math.Round(y));
    }

    private Rectangle TakeGuide()
    {
        if (_guidesUsed < _guides.Count)
        {
            var reused = _guides[_guidesUsed++];
            reused.Visibility = Visibility.Visible;
            return reused;
        }
        var made = new Rectangle
        {
            Height = 1,
            Fill = _brush,
            Opacity = GuideOpacity,
            IsHitTestVisible = false,
        };
        _guides.Add(made);
        _canvas.Children.Add(made);
        _guidesUsed++;
        return made;
    }

    private TextBlock TakeMarker()
    {
        if (_markersUsed < _markers.Count)
        {
            var reused = _markers[_markersUsed++];
            reused.Visibility = Visibility.Visible;
            return reused;
        }
        var made = new TextBlock
        {
            FontSize = MarkerFontSize,
            Opacity = MarkerOpacity,
            Foreground = _brush,
            IsHitTestVisible = false,
        };
        _markers.Add(made);
        _canvas.Children.Add(made);
        _markersUsed++;
        return made;
    }

    private void BeginPass()
    {
        _guidesUsed = 0;
        _markersUsed = 0;
    }

    private void EndPass()
    {
        for (var i = _guidesUsed; i < _guides.Count; i++) _guides[i].Visibility = Visibility.Collapsed;
        for (var i = _markersUsed; i < _markers.Count; i++) _markers[i].Visibility = Visibility.Collapsed;
    }

    private void ClearVisual()
    {
        BeginPass();
        EndPass();
    }

    // ---------- 내부 ScrollViewer 훅·클립·rect 헬퍼 ----------

    /// <summary>
    /// EditorBox 템플릿 내부 ScrollViewer(ContentElement)를 얻어 ViewChanged를 건다.
    /// 템플릿 적용 전이면 다음 레이아웃에서 재시도하고, 레이아웃이 끝났는데도 없으면
    /// (WinAppSDK가 템플릿 구조를 바꾼 경우) 장식을 조용히 포기한다 — 함정 1의 폴백.
    /// </summary>
    private bool EnsureScrollHook()
    {
        if (_scroll is not null) return true;
        if (FindDescendant<ScrollViewer>(_editor) is { } found)
        {
            _scroll = found;
            found.ViewChanged += (_, _) => Render(); // 컴포지션 스크롤은 레이아웃 이벤트가 없다 — 즉시 렌더
            return true;
        }
        if (_editor.ActualWidth > 0 && ++_hookAttempts >= 3)
        {
            Disable();
            return false;
        }
        _pending = true; // 아직 템플릿 적용 전일 수 있다 — 다음 레이아웃에서 재시도
        return false;
    }

    /// <summary>장식을 텍스트와 같은 영역(Padding 안쪽)으로 클립 — 스크롤로 잘리는 줄과 일치시킨다.</summary>
    private void UpdateClip()
    {
        var w = _editor.ActualWidth;
        var h = _editor.ActualHeight;
        var pad = _editor.Padding;
        _canvas.Clip = w > 0 && h > 0
            ? new RectangleGeometry
            {
                Rect = new Rect(pad.Left, pad.Top,
                    Math.Max(0, w - pad.Left - pad.Right),
                    Math.Max(0, h - pad.Top - pad.Bottom)),
            }
            : null;
    }

    private Rect RectOf(int index)
    {
        var rect = _editor.GetRectFromCharacterIndex(index, false);
        return new Rect(rect.X, rect.Y + _yShift, rect.Width, rect.Height);
    }

    private Rect RectOfTrailing(int index)
    {
        var rect = _editor.GetRectFromCharacterIndex(index, true);
        return new Rect(rect.X, rect.Y + _yShift, rect.Width, rect.Height);
    }

    private double TopOf(int index) => _editor.GetRectFromCharacterIndex(index, false).Y + _yShift;

    /// <summary>WinUI TextBox는 줄바꿈을 '\r'로 정규화한다(A113 확인) — '\n'은 방어적 겸용.</summary>
    private static bool IsNewline(char c) => c is '\r' or '\n';

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
