using KOTU.Core.Routing;

namespace KOTU.Core.Navigation;

/// <summary>
/// 같은 폴더의 재생 대상 파일 목록을 자연 정렬(natural sort)로 유지하며 이전/다음 탐색을
/// 제공하는 순수 로직 클래스 (A11, v0.210.0 — docs/A11-playlist-design.md §1).
/// UI 비의존이라 KOTU.Core(net8.0)에 두고 영상·오디오 두 모듈이 함께 쓴다
/// (ARCHITECTURE.md §11.3 "두 모듈 이상이 쓰는 표면은 공용으로 올린다").
/// 파일 시스템 접근은 전부 생성자에 주입된 델리게이트로 추상화한다 — 단위 테스트 대상.
///
/// 원본은 이미지 모듈의 ImageFolderNavigator다(src/KOTU.Module.Image/ImageFolderNavigator.cs).
/// 설계 §1.2 확정: 이미지 모듈의 전환은 이번 범위 밖이라 <b>중복 잔존을 허용</b>한다
/// (TimeText·PlaybackResumeStore가 이미 Video/Audio 두 벌로 존재하는 선례와 같은 성질).
/// 두 클래스의 <b>정렬 결과는 반드시 같아야 한다</b> — 어긋나면 이미지 좌우 탐색 순서와
/// 영상 목록 진행 순서가 달라진다. 그래서 비교 로직을 그대로 복제했다
/// (<see cref="NaturalFileNameComparer"/> 주석 참조).
///
/// 이미지 원본과 다른 점 2가지:
/// ① 확장자 목록이 클래스 상수가 아니라 <b>호출부 주입</b>이다(영상 14종·오디오 8종을 모듈이 넘긴다).
/// ② 숨김·시스템 파일을 <b>기본으로 제외</b>한다(설계 §1.3 · 부록 B 76 확정 —
///    이미지의 숨김 미필터 낙수를 답습하지 않는다). 판정은 단일 원본
///    <see cref="ExplorerListing.ShouldShow"/>를 거친다.
///
/// 목록은 <b>생성 시점 스냅샷</b>이다(폴더 감시 없음 — 이미지와 동일). 재생 중 사라진 파일은
/// 호출부가 <see cref="Remove"/>로 목록에서 빼 인덱스를 갱신한다.
///
/// <para>
/// <b>A349: 목록을 세우는 경로가 두 가지가 됐다</b>(이미지 <c>ImageFolderNavigator</c>의 A346 구조를
/// 그대로 이식).
/// ① <b>자체 열거(폴백)</b> — 공개 생성자·<see cref="Create"/>. 폴더를 직접 열거해 파일명 자연
///    정렬로 세운다. 탐색기 밖에서 연 파일(명령줄·드래그&amp;드롭·연결 프로그램)처럼 참고할 표시
///    목록이 없을 때만 쓴다.
/// ② <b>탐색기 순서 주입(정본)</b> — <see cref="FromOrdered"/>. 셸이 좌 리스트의 표시 목록
///    (ExplorerListing.Arrange 결과 — 정렬 키·방향·확장자 필터·숨김 표시 반영)을 그대로 넘기고,
///    이 클래스는 <b>순서를 손대지 않는다</b>. 화면에서 보는 순서와 ⏮/⏭·오토 넥스트 순서를 맞추는
///    것이 이 경로의 존재 이유이므로 여기서 다시 정렬하면 안 된다.
/// 만든 뒤의 동작(Move·Peek·Remove)은 두 경로가 완전히 같다.
/// 그래서 위의 "이미지 원본과 정렬식이 한 글자도 달라선 안 된다"는 계약은 <b>① 자체 열거 경로에만</b>
/// 남는다 — ②는 양쪽 모듈이 같은 셸 목록을 그대로 받으므로 정렬식 자체가 관여하지 않는다.
/// </para>
/// </summary>
public sealed class FolderPlaylist
{
    private static readonly NaturalFileNameComparer NameComparer = new();

    private readonly List<string> _files;
    private int _index;

    /// <param name="filePath">처음 연 파일의 경로.</param>
    /// <param name="supportedExtensions">포함할 확장자 목록(점 포함, 대소문자 무시).
    /// 호출부(모듈)가 자기 IModule.SupportedExtensions를 넘긴다.</param>
    /// <param name="enumerateFiles">디렉터리 경로 → 파일 경로 열거. 테스트에서 가짜 주입 가능.</param>
    /// <param name="includeFile">파일 1개를 목록에 넣을지 판정(숨김·시스템 제외용).
    /// null이면 전부 통과 — 파일 시스템을 건드리지 않는 순수 테스트 경로다.
    /// 실사용 생성은 <see cref="Create"/>가 <see cref="IsVisibleOnDisk"/>를 넘겨
    /// 숨김·시스템을 항상 뺀다(설계 §1.3).</param>
    public FolderPlaylist(
        string filePath,
        IReadOnlyCollection<string> supportedExtensions,
        Func<string, IEnumerable<string>> enumerateFiles,
        Func<string, bool>? includeFile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) dir = ".";

        // 필터 순서 주의: 확장자(문자열 비교, 무료)가 먼저이고 숨김 판정(파일당 속성 읽기 1회)이
        // 뒤다 — 폴더에 담당 확장자가 아닌 파일이 많아도 속성 IO가 늘지 않는다(설계 §1.3 비용 주석).
        // 결과 집합은 순서와 무관하게 같다.
        _files = enumerateFiles(dir)
            .Where(f => supportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => includeFile is null || includeFile(f))
            .ToList();

        // 처음 연 파일은 필터에 안 걸렸더라도(확장자 목록 밖이거나 숨김 속성이어도) 목록에 포함한다.
        // 명시적 열기는 의도된 접근이라는 판정(설계 §1.3 — A175의 "명시적 열기는 허용"과 동형).
        if (!_files.Any(f => PathEquals(f, filePath)))
            _files.Add(filePath);

        // 자연 정렬: 파일명 기준, 동일하면 전체 경로로 안정화.
        // ImageFolderNavigator.cs의 정렬식과 한 글자도 다르면 안 된다(위 클래스 주석의 "정렬 결과 동일" 계약).
        _files.Sort((a, b) =>
        {
            var c = NameComparer.Compare(Path.GetFileName(a), Path.GetFileName(b));
            return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        _index = _files.FindIndex(f => PathEquals(f, filePath));
    }

    /// <summary>이미 순서가 정해진 목록을 그대로 받는 내부 생성자(A349 — <see cref="FromOrdered"/> 전용).</summary>
    private FolderPlaylist(List<string> files, int index)
    {
        _files = files;
        _index = index;
    }

    /// <summary>
    /// A349: 셸이 준 <b>탐색기 좌 리스트의 표시 순서</b>를 그대로 재생 목록으로 삼는다
    /// (IBrowseOrderConsumer 주입 경로 — 이미지 <c>ImageFolderNavigator.FromOrdered</c>의 이식).
    /// <b>정렬하지 않는다</b> — 입력 순서가 곧 ⏮/⏭·오토 넥스트 순서다.
    /// 목록에는 다른 종류의 파일이 섞여 올 수 있어(All Readable 모듈) 호출부 모듈이 여는 확장자만
    /// 남긴다(판정은 공개 생성자와 같은 <c>Path.GetExtension</c> + OrdinalIgnoreCase).
    /// <para>
    /// <b>숨김 판정은 하지 않는다</b>(공개 생성자와 갈리는 유일한 지점). 셸이 넘기는 목록은 이미
    /// 탐색기의 숨김 표시 설정을 반영한 표시 목록이라, 여기서 <see cref="IsVisibleOnDisk"/>를 다시
    /// 걸면 ⓐ 파일당 속성 읽기가 UI 스레드에서 되살아나고 ⓑ "숨김 표시 켬"으로 보고 있는 사용자의
    /// 화면과 재생 순서가 갈린다. 표시 정책의 판정 지점은 셸 한 곳이면 충분하다.
    /// </para>
    /// </summary>
    /// <param name="orderedFiles">표시 순서 그대로의 파일 경로 목록(폴더 제외).</param>
    /// <param name="currentPath">지금 재생 중인 파일의 경로.</param>
    /// <param name="supportedExtensions">포함할 확장자 목록(점 포함, 대소문자 무시) — 호출부 모듈이 넘긴다.</param>
    public static FolderPlaylist FromOrdered(
        IReadOnlyList<string> orderedFiles,
        string currentPath,
        IReadOnlyCollection<string> supportedExtensions)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentPath);

        var files = orderedFiles
            .Where(f => supportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        // 현재 파일이 표시 목록에 없으면(필터로 가려졌거나·아직 반영 안 된 새 파일) 끝에 붙인다.
        // 공개 생성자와 달리 여기서는 재정렬을 하지 않으므로 "제자리"를 계산할 근거가 없다 —
        // 정렬 키·방향은 셸 쪽에만 있고 이 클래스에는 순서 정보가 없기 때문이다.
        // 어느 쪽이든 현재 파일이 목록에서 사라지지 않는 것이 우선이다(공개 생성자와 같은 규칙).
        if (!files.Any(f => PathEquals(f, currentPath)))
            files.Add(currentPath);

        return new FolderPlaylist(files, files.FindIndex(f => PathEquals(f, currentPath)));
    }

    /// <summary>
    /// 실제 파일 시스템을 사용하는 기본 생성 헬퍼. 숨김·시스템 파일은 제외된다.
    /// 파일당 속성 읽기가 1회 늘어나므로 <b>UI 스레드에서 부르지 말 것</b>
    /// (설계 §1.3 · ARCHITECTURE.md §11.1 동기 IO 금지 — 모듈은 Worker.Run 경유로 생성한다).
    /// </summary>
    public static FolderPlaylist Create(string filePath, IReadOnlyCollection<string> supportedExtensions) =>
        new(filePath, supportedExtensions, Directory.EnumerateFiles, IsVisibleOnDisk);

    /// <summary>
    /// 목록에 넣을 파일인지 = 숨김·시스템이 아닌지. 판정식은 만들지 않고
    /// 표시 정책의 단일 원본 <see cref="ExplorerListing.ShouldShow"/>에 위임한다(설계 §1.3).
    /// 속성을 못 읽는 파일(경합 삭제·권한 없음)은 목록에서 뺀다 — 어차피 재생할 수 없다.
    /// </summary>
    public static bool IsVisibleOnDisk(string path)
    {
        try
        {
            return ExplorerListing.ShouldShow(File.GetAttributes(path), includeHidden: false);
        }
        catch (IOException)
        {
            return false; // 열거 직후 사라진 파일 등
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>목록의 파일 수.</summary>
    public int Count => _files.Count;

    /// <summary>현재 파일의 0 기반 인덱스. 목록이 비면 -1.</summary>
    public int CurrentIndex => _index;

    /// <summary>현재 파일 경로. 목록이 비면 null.</summary>
    public string? Current => _index >= 0 && _index < _files.Count ? _files[_index] : null;

    public bool HasNext => _index >= 0 && _index < _files.Count - 1;

    public bool HasPrevious => _index > 0;

    /// <summary>다음 파일 경로 — 이동 없이 들여다본다. 없으면 null.</summary>
    public string? PeekNext => HasNext ? _files[_index + 1] : null;

    /// <summary>이전 파일 경로 — 이동 없이 들여다본다. 없으면 null.</summary>
    public string? PeekPrevious => HasPrevious ? _files[_index - 1] : null;

    /// <summary>목록의 첫 파일 경로 — 이동 없이 들여다본다. 목록이 비면 null.</summary>
    public string? PeekFirst => _files.Count > 0 ? _files[0] : null;

    /// <summary>
    /// 목록의 마지막 파일 경로 — 이동 없이 들여다본다. 목록이 비면 null.
    /// A349: 수동 ⏮(목록 처음에서 목록 루프 켬)이 끝으로 되감을 때 쓴다 —
    /// <see cref="PeekFirst"/>의 반대 방향 짝이다.
    /// </summary>
    public string? PeekLast => _files.Count > 0 ? _files[^1] : null;

    /// <summary>다음 파일로 이동. 끝이면 이동하지 않고 false(순환 없음 — 순환 여부는 호출부 정책).</summary>
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
    /// 목록의 첫 파일로 되돌아간다 (A11 설계 §3.3 전이 3 — 목록 끝에서 "목록 루프" 켜짐일 때).
    /// 이동이 일어났을 때만 true: 목록이 비었거나 이미 첫 항목이면 false다
    /// (<see cref="MoveNext"/>·<see cref="MovePrevious"/>와 같은 반환 규약).
    /// </summary>
    public bool MoveFirst()
    {
        if (_index <= 0) return false;
        _index = 0;
        return true;
    }

    /// <summary>
    /// 목록의 마지막 파일로 되감는다 (A349 — 수동 ⏮이 목록 처음에서 목록 루프를 탈 때).
    /// 이동이 일어났을 때만 true: 목록이 비었거나 이미 마지막 항목이면 false다
    /// (<see cref="MoveFirst"/>와 같은 반환 규약).
    /// </summary>
    public bool MoveLast()
    {
        if (_files.Count == 0 || _index == _files.Count - 1) return false;
        _index = _files.Count - 1;
        return true;
    }

    /// <summary>
    /// 목록에서 파일을 제거한다(삭제·소실 후 갱신용). 현재 파일을 제거하면
    /// 다음 파일이 현재가 되고, 마지막이었다면 이전 파일이 현재가 된다.
    /// 목록이 비면 <see cref="CurrentIndex"/>는 -1, <see cref="Current"/>는 null이 된다.
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
///
/// KOTU.Module.Image.NaturalStringComparer의 <b>동작 동일 복제</b>다(A11 설계 §7 배치 1).
/// 이름을 달리한 이유는 두 어셈블리에 같은 단순명이 생기는 혼동을 피하기 위함이고,
/// 비교 알고리즘은 한 글자도 바꾸지 않았다 — 바꾸면 이미지 좌우 탐색 순서와
/// 영상·오디오 목록 순서가 어긋난다(<see cref="FolderPlaylist"/> 주석의 정렬 동일 계약).
/// 이미지 모듈을 이 클래스로 전환하는 날 원본을 지우면 된다.
/// </summary>
public sealed class NaturalFileNameComparer : IComparer<string>
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
