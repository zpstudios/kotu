namespace WinUtil.Module.Archive;

/// <summary>"여기에 풀기"의 결과. TargetDirectory = 백엔드에 넘길 해제 대상, ResultPath = 사용자에게 보여줄 결과 경로.</summary>
public sealed record ExtractHerePlan(string TargetDirectory, string ResultPath);

/// <summary>
/// "여기에 풀기" 대상 폴더 결정 규칙. UI 비의존 — 단위 테스트 대상.
/// - 아카이브 루트가 항목 하나뿐이고 그 이름이 비어 있지 않으면(예: 폴더 abc 하나),
///   같은 이름이 없을 때 압축 파일 옆에 바로 푼다 → abc/abc 이중 폴더 방지.
/// - 그 외(항목 여러 개, 또는 이름 충돌)에는 압축 파일 이름의 래퍼 폴더를 만들되,
///   이미 있으면 "이름 (2)", "이름 (3)"… 처럼 빈 이름을 찾는다 → 기존 폴더 덮어쓰기/섞임 방지.
/// </summary>
public static class ExtractHerePlanner
{
    /// <param name="archivePath">압축 파일 경로.</param>
    /// <param name="rootEntryNames">아카이브 최상위 항목 이름들.</param>
    /// <param name="exists">경로 존재 여부(파일·폴더 모두). 테스트에서 가짜 주입.</param>
    public static ExtractHerePlan Plan(
        string archivePath,
        IReadOnlyList<string> rootEntryNames,
        Func<string, bool> exists)
    {
        var parent = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrEmpty(parent)) parent = ".";

        // 단일 루트 항목: 충돌이 없으면 래퍼 없이 그대로 푼다.
        if (rootEntryNames.Count == 1 && !string.IsNullOrEmpty(rootEntryNames[0]))
        {
            var direct = Path.Combine(parent, rootEntryNames[0]);
            if (!exists(direct))
                return new ExtractHerePlan(parent, direct);
        }

        var wrapper = UniquePath(
            Path.Combine(parent, Path.GetFileNameWithoutExtension(archivePath)), exists);
        return new ExtractHerePlan(wrapper, wrapper);
    }

    /// <summary>경로가 이미 있으면 "이름 (2)", "이름 (3)"… 순으로 빈 이름을 찾는다.</summary>
    public static string UniquePath(string path, Func<string, bool> exists)
    {
        if (!exists(path)) return path;
        for (var i = 2; ; i++)
        {
            var candidate = $"{path} ({i})";
            if (!exists(candidate)) return candidate;
        }
    }
}
