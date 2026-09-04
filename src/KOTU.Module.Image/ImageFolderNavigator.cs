namespace KOTU.Module.Image;

/// <summary>
/// 같은 폴더의 이미지 파일 목록을 유지하며 이전/다음 탐색을 제공하는 순수 로직 클래스.
/// UI 비의존 — 단위 테스트 대상. 파일 시스템 접근은 생성자에 주입된 열거 함수로 추상화한다.
/// <para>
/// A346: 목록을 세우는 경로가 <b>두 가지</b>다.
/// ① <b>자체 열거(폴백)</b> — 공개 생성자·<see cref="Create"/>. 폴더를 직접 열거해 파일명
///    자연 정렬(natural sort)로 세운다. 탐색기 밖에서 연 파일(명령줄·드래그&amp;드롭·연결 프로그램)처럼
///    참고할 표시 목록이 없을 때만 쓴다.
/// ② <b>탐색기 순서 주입(정본)</b> — <see cref="FromOrdered"/>. 셸이 좌 리스트의 표시 목록
///    (ExplorerListing.Arrange 결과 — 정렬 키 5종·방향·확장자 필터·숨김 표시 반영)을 그대로 넘기고,
///    이 클래스는 <b>순서를 손대지 않는다</b>. 사용자가 화면에서 보는 순서와 ◀/▶ 순서를 맞추는 것이
///    이 경로의 존재 이유이므로 여기서 다시 정렬하면 안 된다.
/// 두 경로가 만든 뒤의 동작(Move·Peek·Remove)은 완전히 같다.
/// </para>
/// </summary>
public sealed class ImageFolderNavigator
{
    /// <summary>이 모듈이 지원하는 확장자(소문자, 점 포함). psd는 Magick.NET으로 디코드(v0.34.0).</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".psd"];

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

    /// <summary>이미 순서가 정해진 목록을 그대로 받는 내부 생성자(A346 — <see cref="FromOrdered"/> 전용).</summary>
    private ImageFolderNavigator(List<string> files, int index)
    {
        _files = files;
        _index = index;
    }

    /// <summary>실제 파일 시스템(Directory.EnumerateFiles)을 사용하는 기본 생성 헬퍼.</summary>
    public static ImageFolderNavigator Create(string filePath) =>
        new(filePath, SupportedExtensions, Directory.EnumerateFiles);

    /// <summary>
    /// A346: 셸이 준 <b>탐색기 좌 리스트의 표시 순서</b>를 그대로 항해 목록으로 삼는다
    /// (IBrowseOrderConsumer 주입 경로). <b>정렬하지 않는다</b> — 입력 순서가 곧 ◀/▶ 순서다.
    /// 목록에는 다른 종류의 파일이 섞여 올 수 있어(All Readable 모듈) 이 모듈이 여는 확장자만
    /// 남긴다(판정은 공개 생성자와 같은 <c>Path.GetExtension</c> + OrdinalIgnoreCase).
    /// </summary>
    /// <param name="orderedFiles">표시 순서 그대로의 파일 경로 목록(폴더 제외).</param>
    /// <param name="currentPath">지금 열려 있는 파일의 경로.</param>
    public static ImageFolderNavigator FromOrdered(IReadOnlyList<string> orderedFiles, string currentPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentPath);

        var files = orderedFiles
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        // 현재 파일이 표시 목록에 없으면(필터로 가려졌거나·아직 반영 안 된 새 파일) 끝에 붙인다.
        // 공개 생성자와 달리 여기서는 재정렬을 하지 않으므로 "제자리"를 계산할 근거가 없다 —
        // 정렬 키·방향은 셸 쪽에만 있고 이 클래스에는 순서 정보가 없기 때문이다.
        // 어느 쪽이든 현재 파일이 목록에서 사라지지 않는 것이 우선이다(공개 생성자와 같은 규칙).
        if (!files.Any(f => PathEquals(f, currentPath)))
            files.Add(currentPath);

        return new ImageFolderNavigator(files, files.FindIndex(f => PathEquals(f, currentPath)));
    }

    /// <summary>목록의 파일 수.</summary>
    public int Count => _files.Count;

    /// <summary>현재 파일의 0 기반 인덱스. 목록이 비면 -1.</summary>
    public int CurrentIndex => _index;

    /// <summary>현재 파일 경로. 목록이 비면 null.</summary>
    public string? Current => _index >= 0 && _index < _files.Count ? _files[_index] : null;

    public bool HasNext => _index >= 0 && _index < _files.Count - 1;

    public bool HasPrevious => _index > 0;

    /// <summary>다음 파일 경로 — 이동 없이 들여다본다(A194 이웃 선읽기용). 없으면 null.</summary>
    public string? PeekNext => HasNext ? _files[_index + 1] : null;

    /// <summary>이전 파일 경로 — 이동 없이 들여다본다(A194 이웃 선읽기용). 없으면 null.</summary>
    public string? PeekPrevious => HasPrevious ? _files[_index - 1] : null;

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
