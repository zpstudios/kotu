namespace WinUtil.Module.Image;

/// <summary>
/// 같은 폴더의 이미지 파일 목록을 자연 정렬(natural sort)로 유지하며
/// 이전/다음 탐색을 제공하는 순수 로직 클래스. UI 비의존 — 단위 테스트 대상.
/// 파일 시스템 접근은 생성자에 주입된 열거 함수로 추상화한다.
/// </summary>
public sealed class ImageFolderNavigator
{
    /// <summary>이 모듈이 지원하는 확장자(소문자, 점 포함).</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico"];

    private static readonly NaturalStringComparer NameComparer = new();

    private readonly List<string> _files;
    private int _index;

    /// <param name="filePath">처음 연 파일의 경로.</param>
    /// <param name="supportedExtensions">포함할 확장자 목록(점 포함, 대소문자 무시).</param>
    /// <param name="enumerateFiles">디렉터리 경로 → 파일 경로 열거. 테스트에서 가짜 주입 가능.</param>
    public ImageFolderNavigator(
        string filePath,
        IReadOnlyCollection<string> supportedExtensions,
        Func<string, IEnumerable<string>> enumerateFiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) dir = ".";

        _files = enumerateFiles(dir)
            .Where(f => supportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        // 처음 연 파일이 필터에 안 걸렸더라도(예: 확장자 목록 외) 목록에 포함시킨다.
        if (!_files.Any(f => PathEquals(f, filePath)))
            _files.Add(filePath);

        // 자연 정렬: 파일명 기준, 동일하면 전체 경로로 안정화.
        _files.Sort((a, b) =>
        {
            var c = NameComparer.Compare(Path.GetFileName(a), Path.GetFileName(b));
            return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        _index = _files.FindIndex(f => PathEquals(f, filePath));
    }

    /// <summary>실제 파일 시스템(Directory.EnumerateFiles)을 사용하는 기본 생성 헬퍼.</summary>
    public static ImageFolderNavigator Create(string filePath) =>
        new(filePath, SupportedExtensions, Directory.EnumerateFiles);

    /// <summary>목록의 파일 수.</summary>
    public int Count => _files.Count;

    /// <summary>현재 파일의 0 기반 인덱스. 목록이 비면 -1.</summary>
    public int CurrentIndex => _index;

    /// <summary>현재 파일 경로. 목록이 비면 null.</summary>
    public string? Current => _index >= 0 && _index < _files.Count ? _files[_index] : null;

    public bool HasNext => _index >= 0 && _index < _files.Count - 1;

    public bool HasPrevious => _index > 0;

    /// <summary>다음 파일로 이동. 끝이면 이동하지 않고 false(순환 없음).</summary>
    public bool MoveNext()
    {
        if (!HasNext) return false;
        _index++;
        return true;
    }

    /// <summary>이전 파일로 이동. 처음이면 이동하지 않고 false(순환 없음).</summary>
    public bool MovePrevious()
    {
        if (!HasPrevious) return false;
        _index--;
        return true;
    }

    /// <summary>
    /// 목록에서 파일을 제거한다(삭제 후 갱신용). 현재 파일을 제거하면
    /// 다음 파일이 현재가 되고, 마지막이었다면 이전 파일이 현재가 된다.
    /// </summary>
    public bool Remove(string filePath)
    {
        var idx = _files.FindIndex(f => PathEquals(f, filePath));
        if (idx < 0) return false;

        _files.RemoveAt(idx);
        if (idx < _index) _index--;                       // 앞쪽이 빠지면 인덱스 보정
        if (_index >= _files.Count) _index = _files.Count - 1; // 끝을 넘으면 마지막으로(비면 -1)
        return true;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 자연 정렬 비교자: 숫자 구간은 수치로 비교한다. 예: "img2" &lt; "img10".
/// 자릿수 제한 없이 동작하도록 숫자 문자열 길이(선행 0 제거 후)로 우선 비교한다.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                // 숫자 구간을 통째로 잘라 수치 비교(선행 0 무시, 오버플로 없음)
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                var nx = x.AsSpan(si, i - si).TrimStart('0');
                var ny = y.AsSpan(sj, j - sj).TrimStart('0');
                if (nx.Length != ny.Length) return nx.Length - ny.Length;

                var c = nx.CompareTo(ny, StringComparison.Ordinal);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (x.Length - i) - (y.Length - j);
    }
}
