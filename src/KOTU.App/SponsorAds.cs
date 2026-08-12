using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;

namespace KOTU.App;

/// <summary>
/// 광고 1건에 짝지어진 링크 (A67, v0.109.0). url이 비었거나 http/https가 아니면 아예 만들지 않는다 —
/// 즉 이 객체가 있다는 것 자체가 "클릭하면 열리는 광고"라는 뜻이다.
/// </summary>
/// <param name="Url">열 주소. 확인 창 없이 열기 때문에 매핑 단계에서 http/https만 통과시킨다.</param>
/// <param name="Name">표시명(선택). 매핑에 없으면 빈 문자열.</param>
internal sealed record SponsorLink(Uri Url, string Name)
{
    /// <summary>툴팁 문구 — 표시명이 있으면 그대로, 없으면 URL 호스트(A67 확정).</summary>
    public string Tip => Name.Length > 0 ? Name : Url.Host;
}

/// <summary>
/// 광고 이미지 공용 로직(v0.50.0 — 시작 메뉴 카드와 설정 하단 바가 함께 쓴다.
/// 설정 하단 바의 광고는 v0.52.0에 Patreon 문구로 대체돼, 지금 남은 표시 위치는 시작 메뉴 카드 하나다).
/// Assets\sponsor-*.png 중 하나를 1분 단위 시간 시드 랜덤으로 고른다(v0.38.0 규칙:
/// 랜덤하되 1분마다만 바뀌고, 같은 분 안에서는 어디서 보든 같은 이미지).
/// 이미지 ↔ 링크 매핑은 Assets\sponsors.json(A67, v0.109.0) — 파일이 없거나 깨졌으면 링크 없는 그림으로 둔다.
/// </summary>
internal static class SponsorAds
{
    private static readonly string[] Images = Load();

    /// <summary>파일명(대소문자 무시) → 링크. 매핑이 없거나 깨졌으면 비어 있다 (A67).</summary>
    private static readonly Dictionary<string, SponsorLink> Links = LoadLinks();

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

    /// <summary>
    /// Assets\sponsors.json을 읽어 파일명 → 링크 표를 만든다 (A67, v0.109.0).
    /// 스키마: [{ "file": "sponsor-1.png", "name": "", "url": "" }, ...]
    /// url이 비어 있으면 항목을 만들지 않는다 = 링크 없는 광고(커서·클릭 반응 없음).
    /// </summary>
    private static Dictionary<string, SponsorLink> LoadLinks()
    {
        var map = new Dictionary<string, SponsorLink>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "sponsors.json");
            if (!File.Exists(path)) return map;

            // 사람이 손으로 고치는 파일이라 주석·후행 쉼표는 눈감아 준다.
            Entry[] entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) ?? [];

            foreach (var entry in entries)
            {
                if (entry is null) continue;
                if (string.IsNullOrWhiteSpace(entry.File) || string.IsNullOrWhiteSpace(entry.Url)) continue;
                if (!Uri.TryCreate(entry.Url.Trim(), UriKind.Absolute, out var uri)) continue;
                // 확인 창 없이 여는 만큼(미션 문구의 조용한 광고) http/https 외 스킴은 링크로 인정하지 않는다.
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
                map[Path.GetFileName(entry.File.Trim())] =
                    new SponsorLink(uri, entry.Name?.Trim() ?? string.Empty);
            }
        }
        catch
        {
            // 매핑이 깨졌다고 앱이 죽으면 안 된다 — 링크 없는 광고로 취급한다.
        }
        return map;
    }

    /// <summary>sponsors.json 한 항목(A67). 값이 비면 링크 없는 이미지로 본다.</summary>
    private sealed class Entry
    {
        public string? File { get; set; }
        public string? Name { get; set; }
        public string? Url { get; set; }
    }

    public static bool Any => Images.Length > 0;

    /// <summary>현재 분(minute) 시드로 고른 광고 경로. 광고가 없으면 null.</summary>
    public static string? CurrentPath()
    {
        if (Images.Length == 0) return null;
        var minute = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMinutes;
        return Images[new Random((int)(minute % int.MaxValue)).Next(Images.Length)];
    }

    /// <summary>
    /// 지금 보이는 광고의 링크 (A67, v0.109.0). 매핑에 없거나 url이 비었으면 null —
    /// 그때는 지금까지처럼 커서 기본·툴팁 없음·클릭 무반응인 그림으로 둔다.
    /// 매칭은 파일명 기준(대소문자 무시)이고 <see cref="CurrentPath"/>와 같은 분 시드를 쓰므로,
    /// 같은 분 안에서는 화면의 이미지와 항상 같은 항목을 가리킨다.
    /// </summary>
    public static SponsorLink? CurrentLink()
        => CurrentPath() is { } path && Links.TryGetValue(Path.GetFileName(path), out var link)
            ? link
            : null;

    /// <summary>이미지 컨트롤에 현재 광고를 적용한다. 같은 이미지면 다시 로드하지 않는다.</summary>
    public static void Apply(Microsoft.UI.Xaml.Controls.Image target)
    {
        if (CurrentPath() is not { } path) return;
        if (Equals(target.Tag, path)) return;
        target.Tag = path;
        target.Source = new BitmapImage(new Uri(path));
    }
}
