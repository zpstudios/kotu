namespace WinUtil.Core.Cli;

/// <summary>실행 인자가 요청하는 동작.</summary>
public enum LaunchVerb
{
    /// <summary>파일을 확장자 라우팅으로 연다(기본).</summary>
    Open,

    /// <summary>압축 파일을 그 자리에 푼다 (탐색기 우클릭 메뉴).</summary>
    ExtractHere,

    /// <summary>파일/폴더를 새 압축으로 만든다 (탐색기 우클릭 메뉴).</summary>
    Compress,
}

/// <summary>
/// 커맨드라인 → 실행 요청 해석. UI·OS 비의존 — 단위 테스트 대상.
/// 첫 실행(OnLaunched)과 단일 인스턴스 재전달(redirect activation) 양쪽에서 쓴다.
/// </summary>
public sealed record LaunchRequest(LaunchVerb Verb, string? FilePath)
{
    public const string ExtractHereToken = "--extract-here";
    public const string CompressToken = "--compress";

    /// <summary>동사에 대응하는 토큰. Open이면 null.</summary>
    public string? VerbToken => Verb switch
    {
        LaunchVerb.ExtractHere => ExtractHereToken,
        LaunchVerb.Compress => CompressToken,
        _ => null,
    };

    /// <summary>토큰 목록 해석: 알려진 동사 토큰과 첫 번째 비옵션 토큰(파일 경로)을 찾는다.</summary>
    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        string? verbToken = null;
        string? file = null;

        foreach (var arg in args)
        {
            if (arg is ExtractHereToken or CompressToken)
                verbToken ??= arg;
            else if (!arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 0)
                file ??= arg;
        }

        var verb = verbToken switch
        {
            ExtractHereToken => LaunchVerb.ExtractHere,
            CompressToken => LaunchVerb.Compress,
            _ => LaunchVerb.Open,
        };
        return new LaunchRequest(verb, file);
    }

    /// <summary>
    /// 재전달된 원시 커맨드라인 해석. 첫 토큰이 실행 파일(.exe)이면 건너뛴다
    /// (unpackaged 재전달 인자에는 exe 경로가 포함되는 경우가 있다).
    /// </summary>
    public static LaunchRequest ParseCommandLine(string commandLine)
    {
        var tokens = Tokenize(commandLine);
        if (tokens.Count > 0 && tokens[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            tokens = tokens.Skip(1).ToList();
        return Parse(tokens);
    }

    /// <summary>따옴표를 존중하는 커맨드라인 분해. 따옴표 안 공백은 유지되고 따옴표는 제거된다.</summary>
    public static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
