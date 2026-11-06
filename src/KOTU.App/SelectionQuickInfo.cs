using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.App;

/// <summary>
/// 썸네일뷰 **선택** 파일(열지 않음)의 우측 정보 패널 행 빌더 (A200) — 모듈 뷰를 경유할 수 없는
/// 선택 경로용 셸 조회기. ContentInfoOverlay.ShowForSelection이 오버레이 전용 워커에서 돌린다.
/// A329부터 <b>담당 모듈이 있는 종류는 전부</b> 열린 콘텐츠와 같은 단일 빌더를 그대로 쓴다
/// (ImageQuickInfo · A327의 AudioQuickInfo · A328의 VideoQuickInfo · A329의 DocumentQuickInfo·
/// ArchiveQuickInfo — 두 경로 표시 불일치 금지). 종전의 "종류별 조각 한 행"(zip 압축률 ·
/// PDF 페이지 수 · 텍스트 인코딩 — A155·A199 상세 줄과 같은 소스)은 그 빌더들 안으로 흡수됐다.
/// 탐색기 상세 줄(ExplorerPane.InfoKindOf/FetchDetailInfo) 자체는 무접촉이다.
/// 셸이 모듈 public static을 직접 참조하는 선례 =
/// ArchiveQuickInfo·DocumentQuickInfo·AudioModule.Extensions.
/// 동기 메서드 — 워커 전용(A42: WinRT 비동기 동기 대기). UI 스레드 호출 금지.
/// </summary>
internal static class SelectionQuickInfo
{
    /// <summary>Worker-only: snapshot gate plus fresh attributes, then the existing module builder. Failures are safe rows.</summary>
    public static IReadOnlyList<ContentInfoItem> Build(ExplorerListing.Entry entry, CancellationToken cancellation) =>
        BrowseInfo.BuildVerified(entry, File.GetAttributes, BuildRichRows, BuildSafeRows, cancellation);

    private static IReadOnlyList<ContentInfoItem> BuildRichRows(string path)
    {
        var name = Path.GetFileName(path);
        if (ExplorerListing.MatchesExtension(name, KOTU.Module.Image.ImageFolderNavigator.SupportedExtensions))
            return KOTU.Module.Image.ImageQuickInfo.BuildRows(path); // 단일 빌더 — 열린 콘텐츠와 동일 출력
        if (ExplorerListing.MatchesExtension(name, KOTU.Module.Audio.AudioModule.Extensions))
            return KOTU.Module.Audio.AudioQuickInfo.BuildRows(path); // A327 — 오디오도 단일 빌더(같은 규칙)
        if (ExplorerListing.MatchesExtension(name, KOTU.Module.Video.VideoModule.Extensions))
            return KOTU.Module.Video.VideoQuickInfo.BuildRows(path); // A328 — 영상도 단일 빌더(같은 규칙)
        if (ExplorerListing.MatchesExtension(name, KOTU.Module.Document.DocumentModule.Extensions))
            return KOTU.Module.Document.DocumentQuickInfo.BuildRows(path); // A329 — 문서(PDF·텍스트 갈래)
        if (ExplorerListing.MatchesExtension(name, KOTU.Module.Archive.ArchiveModule.Extensions))
            return KOTU.Module.Archive.ArchiveQuickInfo.BuildRows(path);   // A329 — 압축

        // 담당 모듈이 없는 종류 — 파일 기본 정보만(A329에서 종류별 조각 한 행 갈래는 전부
        // 각 모듈의 단일 빌더로 흡수됐다: PDF Pages·텍스트 Encoding·zip Compression).
        return [];
    }

    /// <summary>Pure snapshot rows and existing blank module labels. No filesystem calls, including for folders.</summary>
    internal static IReadOnlyList<ContentInfoItem> BuildSafeRows(ExplorerListing.Entry entry)
    {
        var path = entry.Path;
        var name = entry.Name;
        var rows = new List<ContentInfoItem>(BrowseInfo.BasicRows(entry));
        if (entry.IsFolder) return rows;
        if (ExplorerListing.MatchesExtension(name, KOTU.Module.Image.ImageFolderNavigator.SupportedExtensions))
        {
            rows.Add(ContentInfoItem.Separator); // 파일 정보 / 촬영 정보 그룹 구분 (A150 관례)
            rows.AddRange(KOTU.Module.Image.ImageQuickInfo.BlankExifRows());
        }
        else if (ExplorerListing.MatchesExtension(name, KOTU.Module.Audio.AudioModule.Extensions))
        {
            // A327: 오디오 placeholder도 라벨은 나열한다 — 조회 0회라 하이드레이션이 없다(A175 불변).
            rows.Add(ContentInfoItem.Separator); // 파일 정보 / 오디오 정보 그룹 구분
            rows.AddRange(KOTU.Module.Audio.AudioQuickInfo.BlankPropertyRows());
        }
        else if (ExplorerListing.MatchesExtension(name, KOTU.Module.Video.VideoModule.Extensions))
        {
            // A328: 영상 placeholder도 라벨은 나열한다 — 조회 0회라 하이드레이션이 없다(A175 불변).
            rows.Add(ContentInfoItem.Separator); // 파일 정보 / 영상 정보 그룹 구분
            rows.AddRange(KOTU.Module.Video.VideoQuickInfo.BlankPropertyRows());
        }
        else if (ExplorerListing.MatchesExtension(name, KOTU.Module.Document.DocumentModule.Extensions))
        {
            // A329: 문서 placeholder도 라벨은 나열한다 — 갈래 판정이 확장자뿐이라 조회 0회다.
            rows.Add(ContentInfoItem.Separator); // 파일 정보 / 문서 정보 그룹 구분
            rows.AddRange(KOTU.Module.Document.DocumentQuickInfo.BlankPropertyRows(path));
        }
        else if (ExplorerListing.MatchesExtension(name, KOTU.Module.Archive.ArchiveModule.Extensions))
        {
            // A329: 압축 placeholder도 라벨은 나열한다 — 조회 0회(통계는 전부 빈칸).
            rows.Add(ContentInfoItem.Separator); // 파일 정보 / 압축 정보 그룹 구분
            rows.AddRange(KOTU.Module.Archive.ArchiveQuickInfo.BlankPropertyRows());
        }
        return rows;
    }
}
