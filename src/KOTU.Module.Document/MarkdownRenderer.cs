using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace KOTU.Module.Document;

/// <summary>
/// A190: 파싱된 문단 모델(MarkdownParser)을 WinUI 요소로 조립한다 — UI 스레드 전용
/// (파싱은 워커, 조립은 UI라는 A42 분업의 UI 쪽 절반).
///
/// <b>구성 방식</b>: RichTextBlock 대신 <b>블록당 TextBlock(+Border) 1개를 StackPanel에 쌓는다</b> —
/// 저장소에 선례가 있는 API(코드 생성 TextBlock·Border·FontWeights·FromArgb 브러시 —
/// HardwareView·SettingsView·MainWindow 다수)를 최대한 재사용하고, 선례 없는 문서 API는
/// 인라인(Run·Hyperlink·TextHighlighter)에 한정한다(각각의 위험·복구법은 구현 보고서 참고).
///
/// <b>색·브러시</b>: 글자색은 지정하지 않는다(테마 기본 전경 자동 추종 — EditorDecor처럼 테마
/// 변경 구독을 새로 만들지 않는다). 배경·선은 반투명 중간 회색(FromArgb)이라 라이트/다크 양쪽에서
/// 성립한다. 브러시는 호출마다 새로 만든다 — v0.174.1 Geometry 공유 크래시(부모 1개 제약) 이후
/// 리소스 공유는 하지 않는 방침(브러시는 공유 가능한 부류지만 비용이 미미해 방침을 따른다).
///
/// <b>실패 안전(함정 3)</b>: 조립 중 예외는 소유자(DocumentView.EnterRenderMode)가 잡아
/// 원문 TextBlock 폴백으로 대체한다 — 여기서는 던져도 앱이 죽지 않는다.
/// </summary>
internal static class MarkdownRenderer
{
    // ---- 체감 조정 지점(치수 전부 여기 한 곳 — EditorDecor 상수 배치 관용구) ----
    private const double BodyFontSize = 14;   // 에디터 기본(DocumentView.BaseEditorFontSize)과 동일
    private const double CodeFontSize = 13;
    private const string CodeFontName = "Consolas"; // 에디터(A142 ②)와 같은 Windows 동봉 고정폭
    private const double ListIndentStep = 20; // 리스트 한 단계당 왼쪽 들여쓰기(px)

    /// <summary>헤딩 크기 3단(사양) — 인덱스 = Level - 1.</summary>
    private static readonly double[] HeadingFontSizes = [24, 20, 16];

    /// <summary>블록 목록을 패널에 조립한다(기존 자식은 버린다 — 재렌더는 토글 시점 1회, 사양).</summary>
    public static void Render(Panel target, IReadOnlyList<MdBlock> blocks)
    {
        target.Children.Clear();
        foreach (var block in blocks)
            target.Children.Add(BuildBlock(block));
    }

    private static UIElement BuildBlock(MdBlock block) => block.Kind switch
    {
        MdBlockKind.Heading => BuildHeading(block),
        MdBlockKind.CodeBlock => BuildCodeBlock(block),
        MdBlockKind.ListItem => BuildListItem(block),
        MdBlockKind.Quote => BuildQuote(block),
        MdBlockKind.Rule => BuildRule(),
        _ => BuildParagraph(block),
    };

    private static UIElement BuildParagraph(MdBlock block) =>
        WithMargin(BuildLines(block.Spans, BodyFontSize, semiBold: false), new Thickness(0, 0, 0, 10));

    private static UIElement BuildHeading(MdBlock block)
    {
        var level = Math.Clamp(block.Level, 1, HeadingFontSizes.Length);
        var tb = MakeTextBlock(HeadingFontSizes[level - 1], semiBold: true);
        FillLine(tb, block.Spans); // 헤딩은 한 줄(파서가 줄 단위로 만든다 — LineBreak 없음)
        tb.Margin = new Thickness(0, level == 1 ? 16 : 12, 0, 6);
        return tb;
    }

    private static UIElement BuildCodeBlock(MdBlock block)
    {
        var tb = MakeTextBlock(CodeFontSize, semiBold: false);
        tb.Text = block.Literal;
        tb.FontFamily = new FontFamily(CodeFontName);
        return new Border
        {
            Background = SubtleBrush(0x22),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 2, 0, 10),
            Child = tb,
        };
    }

    private static UIElement BuildListItem(MdBlock block)
    {
        var tb = MakeTextBlock(BodyFontSize, semiBold: false);
        tb.Inlines.Add(new Run { Text = block.Literal + " " }); // 불릿(중점)·번호("3.") — 파서가 정한 표기
        FillLine(tb, block.Spans, startIndex: block.Literal.Length + 1);
        tb.Margin = new Thickness(8 + block.Level * ListIndentStep, 0, 0, 2);
        return tb;
    }

    private static UIElement BuildQuote(MdBlock block) => new Border
    {
        BorderBrush = SubtleBrush(0x60), // 옅은 세로선(사양)
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(12, 2, 0, 2),
        Margin = new Thickness(0, 2, 0, 10),
        Child = WithMargin(BuildLines(block.Spans, BodyFontSize, semiBold: false), default),
    };

    private static UIElement BuildRule() => new Border
    {
        Height = 1,
        Background = SubtleBrush(0x60),
        Margin = new Thickness(0, 10, 0, 10),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>
    /// LineBreak 스팬 경계로 줄을 나눠 줄당 TextBlock 하나씩 쌓는다(여러 줄이면 StackPanel).
    /// LineBreak 인라인 요소를 쓰지 않는 이유: 인라인 코드 배경(TextHighlighter)의 문자 오프셋이
    /// 줄바꿈 요소의 평문 환산 폭에 좌우되는데 그 값이 문서화돼 있지 않다 — 줄당 TextBlock이면
    /// 오프셋이 그 줄 Run 텍스트 합산만으로 확정된다(선례 없는 API를 하나 더 줄이는 효과 겸용).
    /// </summary>
    private static UIElement BuildLines(IReadOnlyList<MdSpan> spans, double fontSize, bool semiBold)
    {
        var lines = new List<List<MdSpan>> { new() };
        foreach (var span in spans)
        {
            if (span.LineBreak) lines.Add([]);
            else lines[^1].Add(span);
        }

        if (lines.Count == 1)
        {
            var tb = MakeTextBlock(fontSize, semiBold);
            FillLine(tb, lines[0]);
            return tb;
        }

        var panel = new StackPanel();
        foreach (var line in lines)
        {
            var tb = MakeTextBlock(fontSize, semiBold);
            if (line.Count == 0) tb.Text = " "; // 빈 줄도 한 줄 높이를 차지하게(인용 내 빈 줄)
            else FillLine(tb, line);
            panel.Children.Add(tb);
        }
        return panel;
    }

    /// <summary>
    /// 한 줄 분량의 스팬을 TextBlock 인라인으로 조립한다. 인라인 코드는 고정폭 Run + 문자 구간
    /// 배경(TextHighlighter — 이 줄 TextBlock의 평문 오프셋 기준, startIndex는 리스트 불릿 등
    /// 앞서 넣은 Run의 문자 수). 링크는 http/https 절대 URI만 Hyperlink(NavigateUri = 기본
    /// 브라우저 열기 — SettingsView의 HyperlinkButton.NavigateUri와 같은 기제)로 만들고,
    /// 그 외 스킴은 원문 그대로 되돌린다(조용한 폴백 — file 등 임의 스킴 실행 방지 겸용).
    /// </summary>
    private static void FillLine(TextBlock tb, IReadOnlyList<MdSpan> spans, int startIndex = 0)
    {
        var index = startIndex;
        List<(int Start, int Length)>? codeRanges = null;
        foreach (var span in spans)
        {
            if (span.LineBreak) continue; // 방어 — 호출자가 줄 단위로 나눠 온다
            if (span.Code)
            {
                tb.Inlines.Add(ApplyEmphasis(new Run
                {
                    Text = span.Text,
                    FontFamily = new FontFamily(CodeFontName),
                    FontSize = CodeFontSize,
                }, span));
                (codeRanges ??= []).Add((index, span.Text.Length));
                index += span.Text.Length;
                continue;
            }
            if (span.LinkUrl is { } url)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    var link = new Hyperlink { NavigateUri = uri };
                    link.Inlines.Add(ApplyEmphasis(new Run { Text = span.Text }, span));
                    tb.Inlines.Add(link);
                    index += span.Text.Length;
                }
                else
                {
                    var literal = $"[{span.Text}]({url})"; // 지원 밖 스킴 — 원문 그대로
                    tb.Inlines.Add(new Run { Text = literal });
                    index += literal.Length;
                }
                continue;
            }
            tb.Inlines.Add(ApplyEmphasis(new Run { Text = span.Text }, span));
            index += span.Text.Length;
        }

        if (codeRanges is null) return;
        var highlighter = new TextHighlighter { Background = SubtleBrush(0x2E) };
        foreach (var (start, length) in codeRanges)
            highlighter.Ranges.Add(new TextRange { StartIndex = start, Length = length });
        tb.TextHighlighters.Add(highlighter);
    }

    private static Run ApplyEmphasis(Run run, MdSpan span)
    {
        if (span.Bold) run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        if (span.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
        return run;
    }

    private static TextBlock MakeTextBlock(double fontSize, bool semiBold)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            IsTextSelectionEnabled = true, // 뷰어이므로 복사 허용(SettingsView 경로 표시 선례)
        };
        if (semiBold) tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return tb;
    }

    private static FrameworkElement WithMargin(UIElement element, Thickness margin)
    {
        var fe = (FrameworkElement)element; // BuildLines는 TextBlock 또는 StackPanel만 만든다
        fe.Margin = margin;
        return fe;
    }

    /// <summary>라이트/다크 공용 반투명 중간 회색 — 알파만 용도별로 다르다(코드 배경·인용선·수평선).</summary>
    private static SolidColorBrush SubtleBrush(byte alpha) =>
        new(Windows.UI.Color.FromArgb(alpha, 0x80, 0x80, 0x80));
}
