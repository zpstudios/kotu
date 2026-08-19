using SharpCompress.Archives.Zip;
using SharpCompress.Readers;

namespace KOTU.Module.Archive;

/// <summary>
/// 압축 파일의 가벼운 메타 조회 (A155) — 탐색기 리스트의 상세 줄(압축률)용.
/// 셸(ExplorerPane)이 모듈의 public static을 직접 참조하는 선례(AudioModule.Extensions —
/// DocumentModule.cs 주석 참고)를 따른다. zip 한정: 이 저장소에서 헤더만 값싸게 읽는 선례가
/// 있는 포맷이 zip(SharpCompress ZipArchive — Cp949ZipReader.TryList)뿐이고, 7z·rar는
/// 백엔드(7z.dll) 호출·암호 흐름이 얽혀 리스트 스캔용으로는 무겁다(생략 — 사양 확정).
/// 동기 메서드 — 호출자가 뷰 전용 ModuleWorker에서 돌린다(IArchiveBackend와 같은 규칙, A42).
/// </summary>
public static class ArchiveQuickInfo
{
    /// <summary>
    /// zip 압축률(원본 대비 몇 %인지 = 압축 파일 크기 x 100 / 원본 합, 반올림).
    /// 항목 크기는 중앙 디렉터리에서만 읽는다(해제 없음 — Cp949ZipReader.TryList와 같은 경로).
    /// 저장 방식(무압축)·오버헤드 때문에 100을 넘을 수 있다 — 그대로 돌려준다.
    /// 읽기 실패(손상·암호 헤더 등)나 원본 합 0(빈 zip)은 -1 = 표시 생략.
    /// </summary>
    public static int TryGetZipCompressionPercent(string archivePath)
    {
        try
        {
            long original = 0;
            using (var zip = ZipArchive.Open(archivePath, new ReaderOptions()))
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.IsDirectory) continue;
                    original += entry.Size;
                }
            }
            if (original <= 0) return -1;
            var packed = new FileInfo(archivePath).Length;
            return (int)Math.Round(100.0 * packed / original);
        }
        catch
        {
            return -1;
        }
    }
}
