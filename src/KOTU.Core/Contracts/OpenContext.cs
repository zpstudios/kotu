namespace KOTU.Core.Contracts;

/// <summary>모듈 뷰를 열 때 전달되는 맥락. 파일 없이(네비게이션으로) 열 수도 있다.</summary>
public sealed record OpenContext
{
    /// <summary>열 파일 경로. 네비게이션으로 진입한 경우 null.</summary>
    public string? FilePath { get; init; }

    /// <summary>추가 인자(커맨드라인 등).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public static OpenContext Empty { get; } = new();

    public static OpenContext ForFile(string path) => new() { FilePath = path };
}
