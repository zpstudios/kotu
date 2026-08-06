namespace WinUtil.Core.Routing;

/// <summary>
/// 하단 바용 드라이브 요약(v0.47.0 — zip·image·docs 모듈, 사용자 요청).
/// 예: "C: Windows (NTFS) · 128.5 GB free of 512 GB". UNC 등 DriveInfo가 못 다루는
/// 경로·접근 실패는 빈 문자열(표시 생략)로 처리한다.
/// </summary>
public static class DriveStatus
{
    public static string Describe(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return string.Empty;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return string.Empty;

            var label = string.IsNullOrEmpty(drive.VolumeLabel) ? "" : $" {drive.VolumeLabel}";
            return $"{drive.Name.TrimEnd('\\')}{label} ({drive.DriveFormat}) · "
                 + $"{ExplorerListing.FormatSize(drive.AvailableFreeSpace)} free of "
                 + $"{ExplorerListing.FormatSize(drive.TotalSize)}";
        }
        catch
        {
            return string.Empty; // 네트워크 경로·권한 문제 등 — 표시만 생략
        }
    }
}
