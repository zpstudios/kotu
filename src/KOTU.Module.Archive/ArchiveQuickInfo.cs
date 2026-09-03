using SharpCompress.Archives.Zip;
using SharpCompress.Readers;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.Module.Archive;

/// <summary>
/// 압축 정보 행(파일 기본 + 내용 통계)의 단일 빌더 (A155 → A329) — 우측 정보 패널의 두 경로,
/// 즉 열린 콘텐츠(ArchiveView.GetContentInfoAsync)와 셸의 썸네일 선택 조회
/// (SelectionQuickInfo)가 둘 다 이 하나를 재사용한다(두 경로 표시 불일치 금지 — A200 확정 원칙).
/// 형틀 = A327의 AudioQuickInfo / A328의 VideoQuickInfo.
/// 종전 실태: <b>열림 축은 아무것도 없었고</b>(ArchiveView가 IContentInfoProvider를 구현하지
/// 않아 셸 폴백 4행뿐), 선택 축만 zip 압축률 조각 한 행을 붙였다 — 이 클래스가 그 불일치를 없앤다.
/// ⚠️ <b>압축 포맷에는 "셸 속성 키 계열"이 없다</b>(오디오의 System.Music.* · 영상의
/// System.Video.*에 해당하는 것이 zip·7z·rar에는 존재하지 않고, 탐색기가 zip 폴더에 붙이는
/// 값은 전부 <b>셸 합성 키</b>라 부록 B 98 ③의 제외 대상이다). 그러므로 이 절은 속성 조회가
/// 아니라 <b>우리가 실제로 계산하는 값</b>으로 채운다 — 항목 수·폴더 수·원본 크기·압축률.
/// 라벨을 지어내지 않는다는 규칙(A329)은 지켜진다: 넷 다 모든 압축 포맷이 실제로 정의하는
/// 값이고, 우리가 이미 계산해 쓰고 있다(탐색기 상세 줄 A155 · 트레이 압축률 A54).
/// 값 채우기 갈래는 둘이다 —
///  · <b>선택 축</b>: zip만 중앙 디렉터리를 값싸게 읽어 채운다(7z·rar는 백엔드(7z.dll) 호출·
///    암호 흐름이 얽혀 선택 즉시 조회로는 무겁다 — A155 사양 승계). 나머지는 빈칸 행.
///  · <b>열림 축</b>: 이미 목록을 읽어 둔 트리(ArchiveEntryTree)에서 통계를 넘겨받아 <b>포맷을
///    가리지 않고</b> 채운다(빈칸 행을 채우는 것뿐이라 행 집합은 두 축이 동일하다 —
///    A328 FillFromPlayer와 같은 장치).
/// ⚠️ A270의 타일·트레이 축은 무접촉이다(부록 B 98 ⑥). 탐색기 상세 줄(A155)도 종전 그대로
/// <see cref="TryGetZipCompressionPercent"/>만 쓴다.
/// 동기 메서드 — 호출자가 뷰 전용 ModuleWorker에서 돌린다(IArchiveBackend와 같은 규칙, A42).
/// UI 스레드 호출 금지.
/// </summary>
public static class ArchiveQuickInfo
{
    /// <summary>
    /// 압축 내용 통계 — 열림 축(ArchiveView)이 이미 읽어 둔 트리에서 만들어 넘긴다.
    /// UncompressedSize = 내부 항목 크기 합(ArchiveEntryTree의 루트 Size와 같은 값).
    /// </summary>
    public sealed record ContentStats(int FileCount, int FolderCount, long UncompressedSize);

    /// <summary>
    /// 정보 패널 행 전체: File·Size·Modified + (구분 행) + 내용 통계 4행.
    /// stats = 열림 축이 이미 아는 통계(null이면 zip 한정으로 직접 읽어 본다 — 그 밖 포맷은 빈칸).
    /// 파일 크기·날짜 실패는 그 행만 생략하고, 통계를 못 구해도 라벨 행은 전부 남는다.
    /// </summary>
    public static IReadOnlyList<ContentInfoItem> BuildRows(string path, ContentStats? stats = null)
    {
        var rows = new List<ContentInfoItem> { new("File", Path.GetFileName(path)) };
        try
        {
            var info = new FileInfo(path);
            // 크기 표기는 셸 폴백(ContentInfoOverlay.BuildBasicFileInfo)과 같은 FormatSize다
            // (DocumentQuickInfo와 같은 판단 — 압축 파일은 KB~GB 범위가 넓다).
            rows.Add(new ContentInfoItem("Size", ExplorerListing.FormatSize(info.Length)));
            rows.Add(new ContentInfoItem("Modified", $"{info.LastWriteTime:yyyy-MM-dd HH:mm}"));
        }
        catch
        {
            // 크기·날짜는 없어도 된다(ImageQuickInfo와 같은 폴백).
        }

        rows.Add(ContentInfoItem.Separator); // 파일 정보 / 압축 정보 그룹 구분 (A150 관례)
        rows.AddRange(BuildRowsFrom(stats ?? ReadZipStats(path), path));
        return rows;
    }

    /// <summary>
    /// 통계 행 전부를 라벨만(값 전부 빈칸) 나열한다 — 조회 자체가 성립하지 않는 갈래
    /// (placeholder = A175 하이드레이션 금지 — 조회 0회 유지)용. 셸(SelectionQuickInfo)이 쓴다.
    /// </summary>
    public static List<ContentInfoItem> BlankPropertyRows() => BuildRowsFrom(null, null);

    /// <summary>
    /// zip 압축률(원본 대비 몇 %인지 = 압축 파일 크기 x 100 / 원본 합, 반올림) — 탐색기 상세
    /// 줄(A155)의 조각 값. 항목 크기는 중앙 디렉터리에서만 읽는다(해제 없음).
    /// 저장 방식(무압축)·오버헤드 때문에 100을 넘을 수 있다 — 그대로 돌려준다.
    /// 읽기 실패(손상·암호 헤더 등)나 원본 합 0(빈 zip)은 -1 = 표시 생략.
    /// </summary>
    public static int TryGetZipCompressionPercent(string archivePath)
    {
        var stats = ReadZipStats(archivePath);
        if (stats is not { UncompressedSize: > 0 }) return -1;
        return CompressionPercent(archivePath, stats.UncompressedSize) ?? -1;
    }

    /// <summary>
    /// 표시 행 조립 — 통계가 없어도(null) 라벨 행 넷은 그대로 나온다(부록 B 98 ①).
    /// path = 압축 파일 경로(압축률 계산에 파일 크기가 필요하다. null이면 압축률만 빈칸).
    /// </summary>
    private static List<ContentInfoItem> BuildRowsFrom(ContentStats? stats, string? path)
    {
        var rows = new List<ContentInfoItem>();
        void Row(string label, string? value) =>
            rows.Add(new ContentInfoItem(label, value ?? string.Empty)); // 빈칸 행 = 라벨만(값 없음)

        Row("Files", stats is null ? null : $"{stats.FileCount:N0}");
        Row("Folders", stats is null ? null : $"{stats.FolderCount:N0}");
        // 원본 합 0(빈 압축·통계 실패)은 크기·압축률 둘 다 의미가 없다 — 빈칸 행으로 둔다.
        var uncompressed = stats is { UncompressedSize: > 0 } ? stats.UncompressedSize : 0;
        Row("Uncompressed", uncompressed > 0 ? ExplorerListing.FormatSize(uncompressed) : null);
        var percent = uncompressed > 0 && path is not null
            ? CompressionPercent(path, uncompressed) : null;
        Row("Compression", percent is { } value ? value + "%" : null);
        return rows;
    }

    /// <summary>압축률 = 압축 파일 크기 x 100 / 원본 합(반올림). 크기를 못 읽으면 null.</summary>
    private static int? CompressionPercent(string archivePath, long uncompressedSize)
    {
        try
        {
            var packed = new FileInfo(archivePath).Length;
            return (int)Math.Round(100.0 * packed / uncompressedSize);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// zip 중앙 디렉터리만 읽어 항목 수·폴더 수·원본 크기 합을 센다(해제 없음 —
    /// Cp949ZipReader.TryList와 같은 경로). zip이 아니거나 읽기 실패(손상·암호 헤더 등)면 null.
    /// 폴더 항목을 아예 기록하지 않는 zip도 있어 Folders가 0으로 나올 수 있다 — 정상이다.
    /// </summary>
    private static ContentStats? ReadZipStats(string archivePath)
    {
        if (!Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return null; // zip 한정(A155 사양 승계) — 나머지는 열림 축이 통계를 넘겨줄 때만 채워진다
        try
        {
            var files = 0;
            var folders = 0;
            long original = 0;
            using var zip = ZipArchive.Open(archivePath, new ReaderOptions());
            foreach (var entry in zip.Entries)
            {
                if (entry.IsDirectory)
                {
                    folders++;
                    continue;
                }
                files++;
                original += entry.Size;
            }
            return new ContentStats(files, folders, original);
        }
        catch
        {
            return null;
        }
    }
}
