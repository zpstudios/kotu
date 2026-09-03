namespace KOTU.Core.Routing;

/// <summary>
/// 내장 탐색기(v0.25.0)의 폴더 목록 로직. UI 비의존 — 폴더는 전부, 파일은 담당 확장자만
/// (사용자 확정: "폴더 + 담당 확장자만"). 숨김/시스템 항목은 기본으로 제외하되,
/// A160(v0.169.0)부터 includeHidden 인자로 포함시킬 수 있다(설정 키 explorer.showHidden —
/// 값을 읽는 곳은 UI 쪽 ExplorerPane.ShowHiddenSettingKey 한 곳이고 여기는 인자만 받는다).
/// </summary>
public static class ExplorerListing
{
    /// <summary>
    /// 탐색기 한 항목. 폴더면 Size는 0.
    /// Created(A117, v0.136.0) = 만든 날짜 — 폴더도 파일과 같은 방식으로 채운다
    /// (DirectoryInfo.CreationTime / FileInfo.CreationTime). Modified와 병존하는 별도 정렬 키의 원본.
    /// IsPlaceholder(A175) = 클라우드 전용(OneDrive placeholder 등) 파일인지 — List가 열거 시점에
    /// 이미 읽는 Attributes로 판정해 담는다(추가 IO 0). 소비처(썸네일·상세 지연 로드)는 이 값이
    /// 참이면 파일 내용을 여는 자동 조회를 생략한다(내용을 여는 순간 클라우드 필터 드라이버가
    /// 전체를 내려받는 하이드레이션이 일어나기 때문). 폴더는 항상 false — 폴더 내용을 여는
    /// 소비처가 없고, 열거 자체는 하이드레이션을 유발하지 않는다.
    /// </summary>
    public sealed record Entry(
        string Path, string Name, bool IsFolder, long Size, DateTime Modified, DateTime Created,
        bool IsPlaceholder = false);

    /// <summary>
    /// 전체 파일 필터 (A196): 담당 확장자라는 개념이 없는 화면(설정·미지원 파일 안내)의 좌 리스트가
    /// 확장자 필터 없이 모든 파일을 보여야 해서 신설한 와일드카드 목록이다. <see cref="MatchesExtension"/>이
    /// "*" 항목을 전부 일치로 해석한다 — 모듈 담당 확장자 목록(IModule.SupportedExtensions)에는
    /// "*"가 없으므로 기존 판정은 전부 종전 그대로다. UI 쪽 A7 필터 메뉴는 이 항목으로 토글을
    /// 만들지 않는다(ExplorerPane.EnsureFilterFlyout — 좁힐 목록 자체가 없다).
    /// </summary>
    public static readonly IReadOnlyList<string> AllFiles = ["*"];

    /// <summary>파일명이 확장자 목록(소문자, 점 포함)에 해당하는지. 대소문자 무시.
    /// "*"가 목록에 있으면(<see cref="AllFiles"/> — A196 전체 파일 필터) 전부 일치.</summary>
    public static bool MatchesExtension(string fileName, IReadOnlyList<string> extensions) =>
        extensions.Contains("*")
        || extensions.Contains(System.IO.Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>
    /// 폴더 내용을 나열한다: 폴더 먼저, 그다음 확장자 일치 파일, 각각 이름순.
    /// maxItems로 초대형 폴더의 UI 폭주를 막는다.
    /// includeHidden(A160) = 숨김·시스템 항목도 포함할지. 기본 false = 종전 동작 그대로.
    /// 호출부(ExplorerPane)가 설정값을 스캔 시작 시점에 스냅샷해 넘긴다 — 여기서 설정을 읽지 않는다
    /// (KOTU.Core는 UI·설정 주입에 비의존).
    /// </summary>
    public static IReadOnlyList<Entry> List(
        string folder, IReadOnlyList<string> extensions, int maxItems = 2000, bool includeHidden = false)
    {
        var info = new DirectoryInfo(folder);
        var result = new List<Entry>();

        foreach (var d in info.EnumerateDirectories()
                     .Where(d => ShouldShow(d.Attributes, includeHidden))
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= maxItems) return result;
            result.Add(new Entry(d.FullName, d.Name, true, 0, d.LastWriteTime, d.CreationTime));
        }

        foreach (var f in info.EnumerateFiles()
                     .Where(f => ShouldShow(f.Attributes, includeHidden) && MatchesExtension(f.Name, extensions))
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= maxItems) return result;
            result.Add(new Entry(f.FullName, f.Name, false, f.Length, f.LastWriteTime, f.CreationTime,
                IsCloudPlaceholder(f.Attributes))); // A175 — Where가 이미 읽은 Attributes 재사용
        }

        return result;
    }

    /// <summary>
    /// 우측 리스트 정렬 키 (A5). 설정 저장 문자열은 소문자 이름과 수동 동기.
    /// Created는 A117(v0.136.0) 신설 — Modified와 같은 방식으로 병존한다(날짜 범위 필터 아님).
    /// Type은 A155 신설 — 확장자 파생 키(Entry 확장 없이 Name에서 파생: 동기 열거를 무겁게 하지 않는다).
    /// </summary>
    public enum SortKey
    {
        Name,
        Size,
        Modified,
        Created,
        Type,
    }

    /// <summary>
    /// 정렬·필터를 적용한 표시용 목록을 만든다 (A5·A7·A155·A204). 폴더 먼저 규칙은 유지.
    /// A204(v0.207.0) — 정렬 안정성: 종전의 고정 이름 2차 키(ThenBy Name)를 없앴다.
    /// 여기의 모든 정렬은 LINQ OrderBy/OrderByDescending = <b>stable sort</b>(동률은 입력 순서
    /// 보존이 문서화된 계약)라, <b>입력 순서가 곧 2차 키다</b>. 호출부가 직전 Arrange 결과
    /// (현재 표시 순서)를 입력으로 다시 부르면 "이름 오름 → 크기" 전환 시 같은 크기끼리
    /// 이름 오름 순서가 유지된다(직전 기준이 동률의 순서로 승계). 스캔 결과(List — 이름순)를
    /// 입력으로 주면 동률이 이름순이 되어 종전 화면과 같다 — 안정성은 세션 내 정렬 조작
    /// 간에만 성립하고 재스캔은 리셋이다(호출부 계약, A204 확정).
    /// A155: 종류별 고정 방향(Size/Modified/Created = 내림 고정)을 descending 인자로 바꿨다 —
    /// 기본 false = 오름차순이고, 종전과 같은 화면을 원하는 호출부는 종류별 기본 방향을 스스로 넘긴다
    /// (UI 쪽 ExplorerPane.DefaultDescending — Core는 방향 정책을 알지 않는다).
    /// 오름차순 기준: Name=이름, Size=작은 것부터, Modified/Created=오래된 것부터,
    /// Type=확장자(점 포함, 대소문자 무시). descending은 1차 키만 뒤집는다 — 뒤집어도 stable이라
    /// 동률의 입력 순서는 그대로다(같은 키 재클릭 방향 토글이 동률 순서를 흔들지 않는 근거).
    /// 폴더: Name/Modified/Created는 파일과 같은 키·방향으로 정렬하고, Size/Type은 폴더에
    /// 그 개념이 없어 <b>무정렬 = 입력 순서 유지</b>다(A204 — 종전 "항상 이름 오름" 강제를 없애
    /// 직전 기준이 폴더에도 살아남는다).
    /// hiddenExtensions(소문자, 점 포함)에 있는 확장자의 파일은 뺀다 — 폴더는 항상 남긴다.
    /// </summary>
    public static IReadOnlyList<Entry> Arrange(
        IReadOnlyList<Entry> entries, SortKey key, bool descending = false,
        IReadOnlyCollection<string>? hiddenExtensions = null)
    {
        // Where는 순서 보존이라 폴더·파일 각각의 입력 순서(=직전 표시 순서)가 그대로 남는다.
        // "폴더 먼저" 병합 순서는 불변 — 입력이 어떤 순서였어도 출력은 늘 폴더가 앞이다.
        var folders = entries.Where(e => e.IsFolder);
        var files = entries.Where(e => !e.IsFolder);
        if (hiddenExtensions is { Count: > 0 })
            files = files.Where(f =>
                !hiddenExtensions.Contains(System.IO.Path.GetExtension(f.Name).ToLowerInvariant()));

        var byName = StringComparer.OrdinalIgnoreCase;
        // A204: 폴더·파일을 각각 target-typed switch로 나눴다 — Size/Type의 폴더 분기가
        // 무정렬(IEnumerable)이 되면서, 종전처럼 튜플 switch로 묶으면 IOrderedEnumerable과의
        // 공통 타입 추론에 기대게 되어 분리했다(각 분기는 IEnumerable로 안전하게 수렴).
        IEnumerable<Entry> sortedFolders = key switch
        {
            // A204: 폴더에 크기·확장자 개념이 없다 — 정렬하지 않고 입력 순서(직전 기준)를 보존.
            SortKey.Size or SortKey.Type => folders,
            SortKey.Modified => OrderByDirection(folders, e => e.Modified, descending),
            // A117: 만든 날짜 — Modified와 같은 모양(폴더도 파일과 같은 키·방향).
            SortKey.Created => OrderByDirection(folders, e => e.Created, descending),
            _ => OrderByDirection(folders, e => e.Name, descending, byName),
        };
        IEnumerable<Entry> sortedFiles = key switch
        {
            SortKey.Size => OrderByDirection(files, e => e.Size, descending),
            // A155: 확장자 정렬 — 키는 Name에서 파생(점 포함, 대소문자 무시).
            SortKey.Type => OrderByDirection(
                files, e => System.IO.Path.GetExtension(e.Name), descending, byName),
            SortKey.Modified => OrderByDirection(files, e => e.Modified, descending),
            SortKey.Created => OrderByDirection(files, e => e.Created, descending),
            _ => OrderByDirection(files, e => e.Name, descending, byName),
        };
        return [.. sortedFolders, .. sortedFiles];
    }

    /// <summary>방향 인자 한 곳 처리 (A155) — Arrange의 분기마다 3항이 반복되는 것을 막는다.
    /// OrderBy/OrderByDescending 어느 쪽이든 stable — A204 "입력 순서 = 2차 키" 계약의 토대.</summary>
    private static IOrderedEnumerable<Entry> OrderByDirection<TKey>(
        IEnumerable<Entry> source, Func<Entry, TKey> selector, bool descending,
        IComparer<TKey>? comparer = null) =>
        descending ? source.OrderByDescending(selector, comparer) : source.OrderBy(selector, comparer);

    /// <summary>재생 길이 표시용 텍스트 (A6): 1시간 미만 m:ss, 이상 h:mm:ss. 1초 미만은 빈 문자열.</summary>
    public static string FormatDuration(TimeSpan duration) => duration.TotalSeconds < 1
        ? string.Empty
        : duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    /// <summary>파일 크기 표시용 텍스트 (B/KB/MB/GB).</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
    };

    /// <summary>
    /// 표시 대상인지 (A160, v0.169.0) — 숨김/시스템 표시 정책의 <b>단일 원본</b>.
    /// 열거 지점 전부가 이 한 줄을 통과한다: 위 List의 폴더·파일 두 곳 +
    /// 좌 패널 폴더 트리(KOTU.App.Overlays.FileListOverlay.LoadChildrenAsync).
    /// 트리에 있던 인라인 복제(속성 마스크 직접 비교)를 없앤 자리다 — 리스트와 트리가 서로
    /// 다른 집합을 보이는 것이 이 항목의 최악 회귀라, 새 열거 지점도 반드시 여기를 거칠 것.
    /// includeHidden = true면 숨김·시스템을 함께 보여 준다(OS 탐색기는 2개 옵션이지만
    /// KOTU는 1단계에서 하나로 묶는다 — 사용자 확정).
    /// A324(2026-09-03)의 예외 1건: 폴더 트리는 <b>지금 보고 있는 폴더로 가는 길목</b>의 폴더
    /// 한 칸만 이 판정을 거치지 않고 노드로 만든다(FileListOverlay.AddPathNode) — %AppData%처럼
    /// 숨김 폴더를 지나는 경로에서 트리가 하단 리스트와 어긋나기 때문이다. 열거가 아니라
    /// 이미 아는 한 경로를 실체화하는 것이라 <b>집합</b>(형제 목록)은 종전 그대로다.
    /// </summary>
    public static bool ShouldShow(FileAttributes attributes, bool includeHidden) =>
        includeHidden || !IsHiddenOrSystem(attributes);

    /// <summary>숨김·시스템 속성 판정. 부르는 곳은 ShouldShow 하나 — 판정식은 여기 한 벌뿐이다.</summary>
    private static bool IsHiddenOrSystem(FileAttributes attributes) =>
        (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    /// <summary>
    /// 클라우드 전용(placeholder) 파일 판정 (A175) — 판정식의 <b>단일 원본</b>.
    /// OneDrive 등 클라우드 필터 드라이버가 온라인 전용 파일에 다는 속성 3축:
    /// Offline(0x1000) · RecallOnDataAccess(0x400000) · RecallOnOpen(0x40000).
    /// 하나라도 켜져 있으면 내용을 여는 순간 전체 다운로드(하이드레이션)가 일어난다.
    /// 속성 읽기·열거 자체는 하이드레이션을 유발하지 않으므로 판정 비용은 0이다.
    /// </summary>
    public static bool IsCloudPlaceholder(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;

    /// <summary>Win32 FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS — .NET 8 BCL FileAttributes에는
    /// 이 이름이 없어(Offline까지만 정의) 캐스트 상수로 둔다. 값은 Windows SDK 헤더 확정치.</summary>
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>Win32 FILE_ATTRIBUTE_RECALL_ON_OPEN — 위와 같은 사유의 캐스트 상수.</summary>
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
}
