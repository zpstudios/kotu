using Microsoft.UI.Xaml.Media.Imaging;

namespace WinUtil.App;

/// <summary>
/// 광고 이미지 공용 로직(v0.50.0 — 시작 메뉴 카드와 설정 하단 바가 함께 쓴다).
/// Assets\sponsor-*.png 중 하나를 1분 단위 시간 시드 랜덤으로 고른다(v0.38.0 규칙:
/// 랜덤하되 1분마다만 바뀌고, 같은 분 안에서는 어디서 보든 같은 이미지).
/// </summary>
internal static class SponsorAds
{
    private static readonly string[] Images = Load();

    private static string[] Load()
    {
        try
        {
            return [.. Directory.GetFiles(
                    Path.Combine(AppContext.BaseDirectory, "Assets"), "sponsor-*.png")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
        }
        catch
        {
            return []; // 광고가 없다고 앱이 죽으면 안 된다
        }
    }

    public static bool Any => Images.Length > 0;

    /// <summary>현재 분(minute) 시드로 고른 광고 경로. 광고가 없으면 null.</summary>
    public static string? CurrentPath()
    {
        if (Images.Length == 0) return null;
        var minute = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMinutes;
        return Images[new Random((int)(minute % int.MaxValue)).Next(Images.Length)];
    }

    /// <summary>이미지 컨트롤에 현재 광고를 적용한다. 같은 이미지면 다시 로드하지 않는다.</summary>
    public static void Apply(Microsoft.UI.Xaml.Controls.Image target)
    {
        if (CurrentPath() is not { } path) return;
        if (Equals(target.Tag, path)) return;
        target.Tag = path;
        target.Source = new BitmapImage(new Uri(path));
    }
}
