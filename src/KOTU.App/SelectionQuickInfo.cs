using Windows.Storage;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.App;

/// <summary>
/// 썸네일뷰 **선택** 파일(열지 않음)의 우측 정보 패널 행 빌더 (A200) — 모듈 뷰를 경유할 수 없는
/// 선택 경로용 셸 조회기. ContentInfoOverlay.ShowForSelection이 오버레이 전용 워커에서 돌린다.
/// 종류 판정·조각 취득은 탐색기 상세 줄(ExplorerPane.InfoKindOf/FetchDetailInfo — A155·A199)과
/// 같은 소스·같은 규칙이고, 이미지는 열린 콘텐츠와 같은 단일 빌더(ImageQuickInfo — 두 경로
/// 표시 불일치 금지)를 그대로 쓴다. 셸이 모듈 public static을 직접 참조하는 선례 =
/// ArchiveQuickInfo·DocumentQuickInfo·AudioModule.Extensions.
/// 동기 메서드 — 워커 전용(A42: WinRT 비동기 동기 대기). UI 스레드 호출 금지.
/// </summary>
internal static class SelectionQuickInfo
{
    /// <summary>
    /// 선택 파일의 정보 행: 이미지 = ImageQuickInfo.BuildRows 전체(파일 기본 + EXIF 키 전부),
    /// 그 외 = 파일 기본 정보(ContentInfoOverlay.BuildBasicFileInfo) + 종류별 조각 한 행
    /// (zip 압축률 · PDF 페이지 수 · 텍스트 인코딩 · 영상/오디오 재생시간 — A199 상세 줄과 동일 소스).
    /// A175 방어선: 호출부(ContentInfoOverlay)가 placeholder를 걸러 오지만, 속성이 클라우드 전용으로
    /// 판정되면(IsCloudPlaceholder) 여기서도 내용 조회를 생략한다 — 어떤 경로로도 하이드레이션 금지.
    /// </summary>
    public static IReadOnlyList<ContentInfoItem> Build(string path)
    {
        try
        {
            if (ExplorerListing.IsCloudPlaceholder(File.GetAttributes(path)))
                return Overlays.ContentInfoOverlay.BuildBasicFileInfo(path);
        }
        catch
        {
            // 속성을 못 읽으면(삭제 경합 등) 아래 일반 경로가 각 단계에서 다시 실패 처리한다.
        }

        var name = Path.GetFileName(path);
        if (ExplorerListing.MatchesExtension(name, KOTU.Module.Image.ImageFolderNavigator.SupportedExtensions))
            return KOTU.Module.Image.ImageQuickInfo.BuildRows(path); // 단일 빌더 — 열린 콘텐츠와 동일 출력

        var rows = new List<ContentInfoItem>(Overlays.ContentInfoOverlay.BuildBasicFileInfo(path));
        if (BuildFragment(path, name) is { } fragment)
        {
            rows.Add(ContentInfoItem.Separator); // 파일 정보 / 종류별 조각 그룹 구분 (A150 관례)
            rows.Add(fragment);
        }
        return rows;
    }

    /// <summary>
    /// 종류별 조각 한 행 — 판정 순서는 ExplorerPane.InfoKindOf와 동일(.pdf가 문서 목록보다 먼저).
    /// 취득 실패·값 없음은 null = 조각 생략(상세 줄과 같은 폴백).
    /// </summary>
    private static ContentInfoItem? BuildFragment(string path, string name)
    {
        try
        {
            if (ExplorerListing.MatchesExtension(name, KOTU.Module.Video.VideoModule.Extensions) ||
                ExplorerListing.MatchesExtension(name, KOTU.Module.Audio.AudioModule.Extensions))
            {
                // 재생시간 — 셸 미디어 속성(System.Media.Duration, 100ns 틱) 조회.
                // ExplorerPane.FetchDurationTicks와 같은 관용구(A6 계보).
                var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
                var props = file.Properties.RetrievePropertiesAsync(["System.Media.Duration"])
                    .AsTask().GetAwaiter().GetResult();
                var ticks = props.TryGetValue("System.Media.Duration", out var d) && d is ulong u
                    ? (long)u : 0L;
                if (ticks <= 0) return null;
                var duration = ExplorerListing.FormatDuration(TimeSpan.FromTicks(ticks));
                return duration.Length == 0 ? null : new ContentInfoItem("Length", duration);
            }
            if (string.Equals(Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // 페이지 수 — 문서를 실제로 여는 비용(암호 PDF는 예외 → 생략)이지만 워커 + 캐시라
                // 수용(ExplorerPane.FetchDetailInfo의 Pdf 갈래와 같은 판단·같은 API).
                var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
                var doc = Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file)
                    .AsTask().GetAwaiter().GetResult();
                if (doc.PageCount == 0) return null;
                return new ContentInfoItem("Pages", doc.PageCount.ToString());
            }
            if (string.Equals(Path.GetExtension(name), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                // 압축률 = 파일 크기 ÷ 원본 합(중앙 디렉터리만 — 해제 없음). zip 한정(A155 사양).
                var percent = KOTU.Module.Archive.ArchiveQuickInfo.TryGetZipCompressionPercent(path);
                if (percent < 0) return null;
                return new ContentInfoItem("Compression", percent + "%");
            }
            if (ExplorerListing.MatchesExtension(name, KOTU.Module.Document.DocumentModule.Extensions))
            {
                // 비PDF 텍스트 — 인코딩 판정(A199 — 앞부분 상한 읽기, ReadTextSmart 규칙 재사용).
                var encoding = KOTU.Module.Document.DocumentQuickInfo.TryGetEncodingName(path);
                return encoding is null ? null : new ContentInfoItem("Encoding", encoding);
            }
        }
        catch
        {
            // 속성·헤더를 못 읽는 파일은 조각 생략 — 기본 정보만으로 충분하다.
        }
        return null;
    }
}
