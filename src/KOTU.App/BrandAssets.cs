using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using KOTU.Core.Settings;

namespace KOTU.App;

/// <summary>
/// 브랜드 에셋이 들어가는 자리 (A79, v0.119.0). 지점을 늘릴 땐 여기에 값을 추가하고
/// BrandAssets.MinimumLevel 표에 몇 레벨부터 켜지는지 적는다 —
/// 적용 지점 코드에는 레벨 숫자를 절대 쓰지 않는다.
/// </summary>
internal enum BrandPoint
{
    /// <summary>① 앱·트레이 중립 아이콘의 표식을 "KO/TU" 2줄 대신 발바닥으로.</summary>
    NeutralPaw,

    /// <summary>② 모듈 색 아이콘 우하단 <c>kotu</c> 표식을 작은 발바닥으로.</summary>
    ModulePawMark,

    /// <summary>③ 시작 메뉴 최상단·설정 화면 상단 워드마크.</summary>
    Wordmark,

    /// <summary>⑤ 로딩 인디케이터를 발바닥 스피너로.</summary>
    PawSpinner,

    /// <summary>④ 설치 스플래시·첫 실행 웰컴의 마스코트.</summary>
    Mascot,

    /// <summary>
    /// ⑥ site/ 랜딩 페이지 로고. 정적 HTML이라 이 레벨 값이 자동으로 닿지 않는다 —
    /// 레벨 3 로고 파일(site/assets/logo-mark-brand.png)을 넣어 두고 페이지에서 한 줄로 바꾼다.
    /// 표에 자리를 남겨 두는 이유는 지점 목록의 단일 소스를 깨지 않기 위함.
    /// </summary>
    SiteLogo,
}

/// <summary>
/// 브랜드 에셋 단계형 적용의 <b>단일 매핑</b> (A79, v0.119.0).
///
/// 사용자가 <c>settings.json</c>의 <see cref="LevelSettingKey"/> 하나만 바꾸면 그 레벨에
/// 해당하는 지점이 한꺼번에 켜진다. 각 적용 지점의 코드는 <see cref="IsEnabled"/>만 물어보고
/// 레벨 숫자를 직접 비교하지 않는다(그게 "지점별 하드코딩"이고 이 항목이 없애려는 것이다).
///
/// 레벨 구획(사용자 확정 2026-08-13):
///  · 0 = 현행 무적용(기본값) · 1 = 아이콘 포인트 ①② · 2 = +워드마크·스피너 ③⑤ · 3 = +마스코트·랜딩 ④⑥.
/// 범위 밖 값은 0~3으로 클램프한다. 설정 화면에는 노출하지 않는다(사용자 확정) —
/// A36(v0.109.0)의 "Open settings.json"으로 파일을 직접 열어 바꾼다.
///
/// 레벨은 <b>시작 후 처음 물어볼 때 1회 읽고 캐시</b>한다. 런타임 실시간 반영은 요구가 아니다 —
/// settings.json을 고친 뒤 앱을 다시 켜야 반영된다.
/// 에셋 파일이 없거나 깨졌으면 조용히 레벨 0의 모습으로 떨어진다
/// (<see cref="SponsorAds"/>의 sponsors.json 로드와 같은 규칙 — 장식 때문에 앱이 죽으면 안 된다).
///
/// 색·문구의 단일 소스인 <see cref="Branding"/>과 같은 층에 둔다.
/// 빌드 산출물(.ico·splash.png)은 저장소에 커밋되는 파일이라 이 레벨 값이 닿지 않는다 —
/// 생성 스크립트 쪽 같은 표는 packaging/brand.py다(두 표는 같은 값이어야 한다).
/// </summary>
internal static class BrandAssets
{
    /// <summary>settings.json 키 (int, 기본 0, 유효 0~3).</summary>
    public const string LevelSettingKey = "branding.assetLevel";

    private const int MinLevel = 0;
    private const int MaxLevel = 3;

    /// <summary>지점 → 켜지는 최소 레벨. 이 표 하나가 전 지점의 판정 근거다.</summary>
    private static int MinimumLevel(BrandPoint point) => point switch
    {
        BrandPoint.NeutralPaw or BrandPoint.ModulePawMark => 1,
        BrandPoint.Wordmark or BrandPoint.PawSpinner => 2,
        BrandPoint.Mascot or BrandPoint.SiteLogo => 3,
        _ => int.MaxValue, // 표에 없는 지점은 어떤 레벨에서도 켜지지 않는다
    };

    /// <summary>읽기 성공 시에만 채운다 — DI 준비 전에 물어봤다고 0을 굳혀 버리지 않게.</summary>
    private static int? s_level;

    /// <summary>현재 브랜드 레벨(0~3). 설정을 못 읽으면 0으로 취급한다.</summary>
    public static int Level
    {
        get
        {
            if (s_level is { } cached) return cached;
            if (ReadLevel() is not { } level) return MinLevel;
            s_level = level;
            return level;
        }
    }

    /// <summary>이 지점이 지금 레벨에서 켜지는가. <b>적용 지점 코드는 이것만 물어본다.</b></summary>
    public static bool IsEnabled(BrandPoint point) => Level >= MinimumLevel(point);

    private static int? ReadLevel()
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            return Math.Clamp(settings.Get(LevelSettingKey, MinLevel), MinLevel, MaxLevel);
        }
        catch
        {
            // DI가 아직 준비되지 않았거나(아주 이른 호출) 설정 저장소가 없다 —
            // 캐시하지 않고 0으로 취급해 다음 호출에서 다시 시도한다.
            return null;
        }
    }

    /// <summary>exe 옆 Assets\Brand\ 의 조각 경로. 없으면 null(호출자는 장식을 생략한다).</summary>
    public static string? TryGetAsset(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", fileName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>③ 워드마크 이미지. 꺼져 있거나 에셋이 없으면 null — <b>빈 자리를 만들지 말 것.</b></summary>
    public static FrameworkElement? CreateWordmark(double height)
        => CreateImage(BrandPoint.Wordmark, "wordmark.png", height);

    /// <summary>④ 마스코트 이미지. 꺼져 있거나 에셋이 없으면 null.</summary>
    public static FrameworkElement? CreateMascot(double height)
        => CreateImage(BrandPoint.Mascot, "mascot.png", height);

    private static FrameworkElement? CreateImage(BrandPoint point, string fileName, double height)
    {
        if (!IsEnabled(point)) return null;
        if (TryGetAsset(fileName) is not { } path) return null;
        try
        {
            return new Image
            {
                Source = new BitmapImage(new Uri(path)),
                Height = height,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false, // 순수 장식 — 클릭·툴팁을 가로채지 않는다
            };
        }
        catch
        {
            return null; // 깨진 PNG 등 — 장식 없이 그대로 간다
        }
    }
}
