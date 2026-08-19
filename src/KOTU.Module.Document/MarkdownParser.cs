using System.Text;

namespace KOTU.Module.Document;

/// <summary>블록 종류(A190 — 지원 부분집합 확정본). 표에 없는 문법은 전부 Paragraph(원문 그대로)다.</summary>
internal enum MdBlockKind
{
    Paragraph, // 일반 문단(연속 텍스트 줄 묶음 — 줄 경계는 LineBreak 스팬)
    Heading,   // #·##·### (Level 1~3) — #### 이상은 Paragraph 폴백
    CodeBlock, // ``` 펜스 — 내용은 Literal에 원문 그대로(닫는 펜스가 없으면 EOF까지)
    ListItem,  // "- " 불릿·"숫자. " 순서 목록 — 한 줄 = 한 블록, Level = 들여쓰기 단계
    Quote,     // "> " 인용 — 연속 인용 줄 묶음
    Rule,      // "---"(하이픈 3개 이상 단독 줄) 수평선
}

/// <summary>
/// 인라인 조각(A190). Code·LinkUrl·LineBreak는 서로 배타적으로 쓰이고, Bold·Italic은 중첩
/// 파싱 결과가 플래그로 눌려 온다. LineBreak=true면 Text는 빈 문자열(줄 경계 표지 전용).
/// </summary>
internal sealed record MdSpan(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    string? LinkUrl = null,
    bool LineBreak = false);

/// <summary>파싱된 블록 하나. Literal은 CodeBlock(본문)·ListItem(불릿/번호 표기)만 쓴다(그 외 빈 문자열).</summary>
internal sealed record MdBlock(MdBlockKind Kind, int Level, string Literal, IReadOnlyList<MdSpan> Spans);

/// <summary>
/// A190: 자체 최소 마크다운 파서 — 렌더 뷰(뷰 모드 토글)의 문단 모델을 만든다.
/// UI 비의존 순수 함수라 워커 스레드(A42)에서 돌고, UI(MarkdownRenderer)는 결과 모델만 조립한다.
///
/// <b>지원 부분집합(사양 확정)</b>: 헤딩 1~3단 · 굵게 · 기울임 · 인라인 코드 · 코드 블록 ·
/// 리스트(불릿/숫자·들여쓰기) · 인용 · 수평선 · 링크. <b>그 외 문법은 원문 그대로 출력</b>
/// (조용한 폴백 — 깨지지 않는 게 합격선). 이미지 문법(느낌표+대괄호)은 링크로 오인하지 않고
/// 원문 그대로 둔다.
///
/// <b>실패 안전(함정 3)</b>: 입력은 신뢰할 수 없다 — Parse는 어떤 예외든 삼키고 전체 원문을
/// 문단 하나로 돌려준다(앱 다운 금지). 인라인 재귀는 깊이 상한으로 자른다.
/// </summary>
internal static class MarkdownParser
{
    /// <summary>인라인 중첩 파싱 깊이 상한 — 그 아래는 남은 문자열을 통짜 스팬으로 눌러 담는다.</summary>
    private const int MaxInlineDepth = 4;

    /// <summary>파싱 진입점 — 실패하면 원문 그대로(Fallback). 워커 스레드에서 부른다(A42).</summary>
    public static IReadOnlyList<MdBlock> Parse(string text)
    {
        try
        {
            return ParseCore(text);
        }
        catch
        {
            return Fallback(text); // 함정 3: 어떤 예외든 원문 그대로 — 렌더가 깨질지언정 앱은 산다
        }
    }

    /// <summary>전체 원문을 문단 하나로 — 파싱 실패·조립 실패의 공용 폴백 모델.</summary>
    public static IReadOnlyList<MdBlock> Fallback(string text)
    {
        var spans = new List<MdSpan>();
        var first = true;
        foreach (var line in SplitLines(text))
        {
            if (!first) spans.Add(new MdSpan(string.Empty, LineBreak: true));
            spans.Add(new MdSpan(line));
            first = false;
        }
        return [new MdBlock(MdBlockKind.Paragraph, 0, string.Empty, spans)];
    }

    private enum LineKind { Blank, Fence, Rule, Heading, Quote, List, Text }

    private static List<MdBlock> ParseCore(string text)
    {
        var lines = SplitLines(text);
        var blocks = new List<MdBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var raw = lines[i];
            var indent = IndentWidth(raw, out var contentStart);
            var content = raw[contentStart..];
            switch (Classify(content))
            {
                case LineKind.Blank:
                    i++; // 빈 줄 = 블록 구분자(문단·인용 묶음은 아래 루프들이 여기서 끊긴다)
                    break;

                case LineKind.Fence:
                {
                    // 여는 펜스 뒤 언어 태그는 무시(사양 밖). 닫는 펜스가 없으면 EOF까지가 코드다
                    // (조용한 폴백 — 반 열린 문서도 깨지지 않는다).
                    i++;
                    var code = new List<string>();
                    while (i < lines.Count
                           && !lines[i].TrimStart(' ', '\t').StartsWith("```", StringComparison.Ordinal))
                        code.Add(lines[i++]);
                    if (i < lines.Count) i++; // 닫는 펜스 소비
                    blocks.Add(new MdBlock(MdBlockKind.CodeBlock, 0, string.Join("\n", code), []));
                    break;
                }

                case LineKind.Rule:
                    blocks.Add(new MdBlock(MdBlockKind.Rule, 0, string.Empty, []));
                    i++;
                    break;

                case LineKind.Heading:
                {
                    var level = 0;
                    while (content[level] == '#') level++; // Classify가 1~3 + 공백을 보장했다
                    blocks.Add(new MdBlock(
                        MdBlockKind.Heading, level, string.Empty,
                        ParseInlines(content[(level + 1)..].Trim())));
                    i++;
                    break;
                }

                case LineKind.Quote:
                {
                    // 연속 인용 줄 = 한 블록(옅은 세로선 하나가 묶음 전체에 걸린다).
                    var spans = new List<MdSpan>();
                    var first = true;
                    while (i < lines.Count)
                    {
                        var qc = lines[i].TrimStart(' ', '\t');
                        if (!qc.StartsWith('>')) break;
                        var body = qc.Length > 1 && qc[1] == ' ' ? qc[2..] : qc[1..];
                        if (!first) spans.Add(new MdSpan(string.Empty, LineBreak: true));
                        spans.AddRange(ParseInlines(body.TrimEnd()));
                        first = false;
                        i++;
                    }
                    blocks.Add(new MdBlock(MdBlockKind.Quote, 0, string.Empty, spans));
                    break;
                }

                case LineKind.List:
                {
                    TryParseListItem(content, out var marker, out var body); // Classify가 보장
                    var level = Math.Min(indent / 2, 6); // 공백 2칸 = 한 단계(탭 = 4칸 환산)
                    blocks.Add(new MdBlock(MdBlockKind.ListItem, level, marker, ParseInlines(body.TrimEnd())));
                    i++;
                    break;
                }

                default: // Text — 연속 일반 줄 = 한 문단(줄 경계는 LineBreak 스팬으로 보존)
                {
                    var spans = new List<MdSpan>();
                    var first = true;
                    while (i < lines.Count)
                    {
                        var pc = lines[i].TrimStart(' ', '\t');
                        if (Classify(pc) != LineKind.Text) break;
                        if (!first) spans.Add(new MdSpan(string.Empty, LineBreak: true));
                        spans.AddRange(ParseInlines(pc.TrimEnd()));
                        first = false;
                        i++;
                    }
                    blocks.Add(new MdBlock(MdBlockKind.Paragraph, 0, string.Empty, spans));
                    break;
                }
            }
        }
        return blocks;
    }

    /// <summary>줄 분류 — 앞 공백을 걷어낸 내용 기준. 지원 밖 형태는 전부 Text(원문 그대로)다.</summary>
    private static LineKind Classify(string content)
    {
        if (content.Length == 0) return LineKind.Blank;
        if (content.StartsWith("```", StringComparison.Ordinal)) return LineKind.Fence;
        if (IsRule(content)) return LineKind.Rule;
        if (content[0] == '#')
        {
            var n = 0;
            while (n < content.Length && content[n] == '#') n++;
            // 4단 이상·공백 없는 형태는 지원 밖 — 원문 그대로(Text)
            return n <= 3 && n < content.Length && content[n] == ' ' ? LineKind.Heading : LineKind.Text;
        }
        if (content[0] == '>') return LineKind.Quote;
        return TryParseListItem(content, out _, out _) ? LineKind.List : LineKind.Text;
    }

    /// <summary>하이픈 3개 이상만으로 이루어진 줄 = 수평선(별표·언더스코어 형태는 지원 밖).</summary>
    private static bool IsRule(string content)
    {
        var t = content.TrimEnd();
        if (t.Length < 3) return false;
        foreach (var c in t)
            if (c != '-') return false;
        return true;
    }

    /// <summary>
    /// 리스트 항목 판정 — "- 내용" 또는 "숫자. 내용"(숫자 최대 9자리). marker = 표시할 불릿(중점)
    /// 또는 "3." 같은 번호 원문. 별표·플러스 불릿은 지원 밖(기울임 오인 방지 — 원문 그대로).
    /// </summary>
    private static bool TryParseListItem(string content, out string marker, out string body)
    {
        marker = string.Empty;
        body = string.Empty;
        if (content.StartsWith("- ", StringComparison.Ordinal))
        {
            marker = "•"; // 불릿 중점
            body = content[2..];
            return true;
        }
        var d = 0;
        while (d < content.Length && d < 9 && content[d] >= '0' && content[d] <= '9') d++;
        if (d > 0 && d + 1 < content.Length && content[d] == '.' && content[d + 1] == ' ')
        {
            marker = content[..(d + 1)];
            body = content[(d + 2)..];
            return true;
        }
        return false;
    }

    /// <summary>한 줄 분량의 인라인 파싱(굵게·기울임·인라인 코드·링크). 짝이 안 맞으면 그 문자는 원문 그대로.</summary>
    private static List<MdSpan> ParseInlines(string s)
    {
        var spans = new List<MdSpan>();
        ParseInto(spans, s, bold: false, italic: false, depth: 0);
        return spans;
    }

    /// <summary>
    /// 좌에서 우로 한 패스 스캔 — 백틱(코드 스팬, 내부 재파싱 없음)·별표 2개(굵게, 내부 재귀)·
    /// 별표 1개(기울임 — 여닫이 안쪽이 공백이면 불성립: "* 항목" 오인 방지)·대괄호(링크 —
    /// 바로 앞이 느낌표면 이미지 문법이라 원문 그대로). 닫는 짝이 없으면 여는 문자를 그대로 둔다.
    /// </summary>
    private static void ParseInto(List<MdSpan> output, string s, bool bold, bool italic, int depth)
    {
        if (s.Length == 0) return;
        if (depth > MaxInlineDepth)
        {
            output.Add(new MdSpan(s, bold, italic)); // 재귀 상한 — 남은 건 통짜(조용한 폴백)
            return;
        }

        var sb = new StringBuilder();
        void FlushText()
        {
            if (sb.Length == 0) return;
            output.Add(new MdSpan(sb.ToString(), bold, italic));
            sb.Clear();
        }

        var pos = 0;
        while (pos < s.Length)
        {
            var c = s[pos];
            if (c == '`')
            {
                var close = s.IndexOf('`', pos + 1);
                if (close > pos + 1) // 내용 비지 않은 코드 스팬만
                {
                    FlushText();
                    output.Add(new MdSpan(s[(pos + 1)..close], bold, italic, Code: true));
                    pos = close + 1;
                    continue;
                }
            }
            else if (c == '*')
            {
                if (pos + 1 < s.Length && s[pos + 1] == '*')
                {
                    var close = s.IndexOf("**", pos + 2, StringComparison.Ordinal);
                    if (close > pos + 2) // 내용 비지 않은 굵게만
                    {
                        FlushText();
                        ParseInto(output, s[(pos + 2)..close], bold: true, italic, depth + 1);
                        pos = close + 2;
                        continue;
                    }
                }
                else
                {
                    var close = s.IndexOf('*', pos + 1);
                    if (close > pos + 1)
                    {
                        var inner = s[(pos + 1)..close];
                        if (inner[0] != ' ' && inner[^1] != ' ') // "* 항목"류 오인 방지
                        {
                            FlushText();
                            ParseInto(output, inner, bold, italic: true, depth + 1);
                            pos = close + 1;
                            continue;
                        }
                    }
                }
            }
            else if (c == '[' && (pos == 0 || s[pos - 1] != '!')) // 이미지 문법은 원문 그대로
            {
                var closeBracket = s.IndexOf(']', pos + 1);
                if (closeBracket > pos + 1 && closeBracket + 1 < s.Length && s[closeBracket + 1] == '(')
                {
                    var closeParen = s.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket + 2)
                    {
                        var label = s[(pos + 1)..closeBracket];
                        var url = s[(closeBracket + 2)..closeParen];
                        if (!url.Contains(' '))
                        {
                            FlushText();
                            output.Add(new MdSpan(label, bold, italic, LinkUrl: url));
                            pos = closeParen + 1;
                            continue;
                        }
                    }
                }
            }
            sb.Append(c);
            pos++;
        }
        FlushText();
    }

    /// <summary>CRLF·CR·LF 모두 한 줄바꿈으로 — WinUI TextBox는 CR로 정규화하고 로드 텍스트는 원본
    /// 스타일 그대로라, 파서가 세 형태를 다 받아 준다(DocumentView.NormalizeNewlines와 같은 셈법).</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            lines.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add(text[start..]);
        return lines;
    }

    /// <summary>앞 공백 폭(공백 1·탭 4 환산) — 리스트 들여쓰기 단계 산정용.</summary>
    private static int IndentWidth(string line, out int contentStart)
    {
        var width = 0;
        var i = 0;
        for (; i < line.Length; i++)
        {
            if (line[i] == ' ') width++;
            else if (line[i] == '\t') width += 4;
            else break;
        }
        contentStart = i;
        return width;
    }
}
