using Windows.Graphics.Imaging;
using Windows.Storage;
using KOTU.Core.Contracts;

namespace KOTU.Module.Image;

/// <summary>
/// 이미지 정보 행(파일 기본 + EXIF)의 단일 빌더 (A200) — 우측 정보 패널의 두 경로,
/// 즉 열린 콘텐츠(ImageViewerView.GetContentInfoAsync)와 셸의 썸네일 선택 조회
/// (SelectionQuickInfo)가 둘 다 이 하나를 재사용한다(두 경로 표시 불일치 금지 — 확정 원칙).
/// 셸이 모듈의 public static을 직접 참조하는 선례 = ArchiveQuickInfo·DocumentQuickInfo·
/// AudioModule.Extensions.
/// A200 ②: EXIF는 A150의 "값 없으면 행 생략"을 반전해 **표시 키 전부를 나열하고 값 없는
/// 키는 빈칸 행(라벨만)**으로 둔다(빈칸 표기 = 빈 문자열 — 대시 금지, 구현 시 결정).
/// A239: A200의 "키를 물을 수 없는 포맷(BMP/GIF 등)은 EXIF 절 생략" 예외를 폐지 — 일괄
/// 조회가 던지면 키별 개별 조회로 폴백하고(성공 키만 값), 그래도 전부 실패면 **라벨만
/// 나열**한다(어떤 실패에도 16키 라벨은 나온다 — 규칙 단순화).
/// ⚠️ GPS 키(System.GPS.*)는 수집 자체를 하지 않는다 — 부록 B 69(개인정보 기본 숨김),
/// IContentInfoProvider 계약 주석의 금지 서술과 동일. 이 반전에서도 예외다.
/// 동기 메서드 — 호출자가 전용 워커에서 돌린다(A42: WinRT 비동기를 동기 대기해도
/// UI 교착이 없는 스레드). UI 스레드 호출 금지.
/// </summary>
public static class ImageQuickInfo
{
    /// <summary>
    /// 정보 패널 행 전체: File·Size·Modified·Dimensions + (구분 행) + EXIF 키 전부.
    /// 파일 크기·날짜 실패는 그 행만 생략, WIC 디코드 실패(psd 등 코덱 밖 포맷)는
    /// 셸 속성 핸들러(System.Image.HorizontalSize/VerticalSize — ExplorerPane.FetchImageSize
    /// 관용구)로 해상도만 폴백하고 EXIF는 **라벨만 나열**한다(A239 ③ — 종전 "절 생략" 폐지).
    /// 파일 자체를 못 연 경우(삭제 경합 등)만 기본 행으로 끝난다(A239 갈래 3종 밖 — 존치).
    /// </summary>
    public static IReadOnlyList<ContentInfoItem> BuildRows(string path)
    {
        var rows = new List<ContentInfoItem> { new("File", Path.GetFileName(path)) };
        try
        {
            var info = new FileInfo(path);
            rows.Add(new ContentInfoItem("Size", $"{info.Length / 1024.0 / 1024.0:0.##} MB"));
            rows.Add(new ContentInfoItem("Modified", $"{info.LastWriteTime:yyyy-MM-dd HH:mm}"));
        }
        catch
        {
            // 크기·날짜는 없어도 된다 (기존 BuildContentInfo와 같은 폴백).
        }

        uint width = 0, height = 0;
        List<ContentInfoItem> exif = [];
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            try
            {
                using var stream = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
                var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
                width = decoder.PixelWidth;
                height = decoder.PixelHeight;
                exif = BuildExifRows(decoder);
            }
            catch
            {
                // WIC 밖 포맷(psd 등)·손상 파일 — A239 ③: EXIF는 라벨만 나열하고(전부 빈칸),
                // 해상도는 셸 속성 핸들러로 폴백한다. 폴백 조회의 실패는 Dimensions 행만 생략 —
                // 안쪽 try로 가둬 라벨 나열이 바깥 catch(파일 자체 못 열음)로 쓸려가지 않게 한다.
                exif = BlankExifRows();
                try
                {
                    var props = file.Properties.RetrievePropertiesAsync(
                            ["System.Image.HorizontalSize", "System.Image.VerticalSize"])
                        .AsTask().GetAwaiter().GetResult();
                    if (props.TryGetValue("System.Image.HorizontalSize", out var w) && w is uint uw)
                        width = uw;
                    if (props.TryGetValue("System.Image.VerticalSize", out var h) && h is uint uh)
                        height = uh;
                }
                catch
                {
                    // 속성 핸들러도 실패 — 해상도 없이 간다(라벨 나열은 유지).
                }
            }
        }
        catch
        {
            // 파일 자체를 못 열었다(삭제 경합 등) — 위에서 모은 기본 행만.
        }
        if (width > 0)
            rows.Add(new ContentInfoItem("Dimensions", $"{width}×{height} px"));

        if (exif.Count > 0)
        {
            rows.Add(ContentInfoItem.Separator); // 파일 정보 / 촬영 정보 그룹 구분 (A150 유지)
            rows.AddRange(exif);
        }
        return rows;
    }

    /// <summary>
    /// EXIF 조회 키 전부 (A239에서 인라인 목록을 상수화 — 일괄 조회와 키별 폴백이 같은 키셋을
    /// 공유해야 한다). 키셋 = A150의 BitmapProperties 13키 + EXIF 스펙 확장 3키(FocalLengthInFilm·
    /// ExposureBias·MaxAperture) + Orientation(ReadExifRotation이 이미 읽던 키의 표시 승격).
    /// ⚠️ GPS 키(System.GPS.*)는 넣지 않는다 — 위치는 개인정보(부록 B 69). A200 반전·A239
    /// 폴백에서도 예외다(어느 갈래에서도 수집 0).
    /// </summary>
    private static readonly string[] ExifKeys =
    [
        "System.Photo.DateTaken", "System.Photo.CameraManufacturer",
        "System.Photo.CameraModel", "System.Photo.LensModel",
        "System.Photo.ExposureTime", "System.Photo.FNumber",
        "System.Photo.ISOSpeed", "System.Photo.FocalLength",
        "System.Photo.FocalLengthInFilm", "System.Photo.ExposureBias",
        "System.Photo.MaxAperture", "System.Photo.ExposureProgram",
        "System.Photo.MeteringMode", "System.Photo.Flash",
        "System.Photo.WhiteBalance", "System.Photo.Orientation", "System.Image.ColorSpace",
    ];

    /// <summary>
    /// A239: EXIF 16행을 라벨만(값 전부 빈칸)으로 나열한다 — 조회 자체가 성립하지 않는 갈래
    /// (WIC 디코드 실패 ③ · placeholder ② — 조회 0회 유지)용. 셸(SelectionQuickInfo)이 재사용한다.
    /// </summary>
    public static List<ContentInfoItem> BlankExifRows() =>
        BuildRowsFrom(new Dictionary<string, BitmapTypedValue>());

    /// <summary>
    /// EXIF 절: 표시 키 전부를 고정 순서로 나열한다 — 값 없는 키는 빈칸(A200 ② 반전).
    /// A239 ①: 일괄 조회(GetPropertiesAsync)가 던지는 파일(EXIF IFD 없는 PNG류 유력)은 키별
    /// 개별 조회로 폴백한다 — 성공한 키만 값이 담기고, 전부 실패(BMP/GIF 등 속성 저장소
    /// 미지원)면 빈 사전 = 라벨만 나열된다(종전 "절 생략" 폐지 — 규칙 단순화).
    /// </summary>
    private static List<ContentInfoItem> BuildExifRows(BitmapDecoder decoder)
    {
        IDictionary<string, BitmapTypedValue> props;
        try
        {
            props = decoder.BitmapProperties.GetPropertiesAsync(ExifKeys)
                .AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            props = QueryKeysIndividually(decoder); // A239 ① — 키별 폴백(전부 실패면 빈 사전)
        }
        return BuildRowsFrom(props);
    }

    /// <summary>
    /// A239 ①: 키별 개별 조회 폴백 — 일괄 조회가 통째로 던질 때 어느 키가 실제로 막혔는지
    /// 갈라낸다(성공 키만 수집·실패 키는 빈칸 행으로 남는다). 키당 1회씩 총 17회 조회지만
    /// 전용 워커에서만 돌므로(클래스 계약) UI 체감 비용은 없다.
    /// </summary>
    private static IDictionary<string, BitmapTypedValue> QueryKeysIndividually(BitmapDecoder decoder)
    {
        var collected = new Dictionary<string, BitmapTypedValue>();
        foreach (var key in ExifKeys)
        {
            try
            {
                var one = decoder.BitmapProperties.GetPropertiesAsync(new[] { key })
                    .AsTask().GetAwaiter().GetResult();
                foreach (var pair in one) collected[pair.Key] = pair.Value;
            }
            catch
            {
                // 이 키는 못 묻는다 — 그 행만 빈칸으로 남는다.
            }
        }
        return collected;
    }

    /// <summary>표시 행 조립 — 조회 결과(부분·빈 사전 포함)에서 16행을 고정 순서로 만든다 (A239 분리).</summary>
    private static List<ContentInfoItem> BuildRowsFrom(IDictionary<string, BitmapTypedValue> props)
    {
        var rows = new List<ContentInfoItem>();
        void Row(string label, string? value) =>
            rows.Add(new ContentInfoItem(label, value ?? string.Empty)); // 빈칸 행 = 라벨만(값 없음)

        Row("Taken", Get(props, "System.Photo.DateTaken") is DateTimeOffset taken
            ? $"{taken.LocalDateTime:yyyy-MM-dd HH:mm}" : null);

        var maker = Get(props, "System.Photo.CameraManufacturer") as string;
        var model = Get(props, "System.Photo.CameraModel") as string;
        var camera = $"{maker} {model}".Trim();
        Row("Camera", camera.Length > 0 ? camera : null);

        Row("Lens", Get(props, "System.Photo.LensModel") is string lens
                    && !string.IsNullOrWhiteSpace(lens) ? lens.Trim() : null);

        // 노출 4요소: A150의 합성 한 행(1/125 s · f/2.8 · ...)을 키별 행으로 분해 —
        // "키 전부 나열 + 값 없으면 빈칸"은 합성 행으로는 표현할 수 없다(어느 조각이 비었는지 소실).
        Row("Exposure time", GetDouble(props, "System.Photo.ExposureTime") is { } sec && sec > 0
            ? (sec >= 1 ? $"{sec:0.#} s" : $"1/{Math.Round(1 / sec)} s") : null);
        Row("Aperture", GetDouble(props, "System.Photo.FNumber") is { } f && f > 0
            ? $"f/{f:0.#}" : null);
        Row("ISO", GetUInt(props, "System.Photo.ISOSpeed") is { } iso ? $"{iso}" : null);
        Row("Focal length", GetDouble(props, "System.Photo.FocalLength") is { } mm && mm > 0
            ? $"{mm:0.#} mm" : null);
        Row("Focal length (35mm)", GetUInt(props, "System.Photo.FocalLengthInFilm") is { } mm35 && mm35 > 0
            ? $"{mm35} mm" : null);
        Row("Exposure bias", GetDouble(props, "System.Photo.ExposureBias") is { } bias
            ? $"{bias:+0.#;-0.#;0} EV" : null);
        Row("Max aperture", GetDouble(props, "System.Photo.MaxAperture") is { } maxF && maxF > 0
            ? $"f/{maxF:0.#}" : null);

        // enum류는 영어 문구 매핑, 미정의 값은 빈칸(A150의 "행 생략"이 A200에서 빈칸으로 반전).
        Row("Program", ExposureProgramText(GetUInt(props, "System.Photo.ExposureProgram")));
        Row("Metering", MeteringModeText(GetUInt(props, "System.Photo.MeteringMode")));
        Row("Flash", GetUInt(props, "System.Photo.Flash") is { } flash
            ? ((flash & 1) != 0 ? "Fired" : "Did not fire") : null);
        Row("White balance", WhiteBalanceText(GetUInt(props, "System.Photo.WhiteBalance")));
        Row("Orientation", OrientationText(GetUInt(props, "System.Photo.Orientation")));
        Row("Color space", ColorSpaceText(GetUInt(props, "System.Image.ColorSpace")));
        return rows;
    }

    private static object? Get(IDictionary<string, BitmapTypedValue> props, string key) =>
        props.TryGetValue(key, out var v) ? v.Value : null;

    /// <summary>
    /// EXIF 정수 값 안전 변환 — WIC이 키에 따라 Byte/UInt16/UInt32 등으로 boxing하는 폭을
    /// 흡수한다(정확한 폭을 못 박으면 포맷·코덱에 따라 값이 통째로 사라진다. A150 원문 유지).
    /// </summary>
    private static uint? GetUInt(IDictionary<string, BitmapTypedValue> props, string key) =>
        Get(props, key) switch
        {
            byte b => b,
            ushort us => us,
            uint u => u,
            short s when s >= 0 => (uint)s,
            int i when i >= 0 => (uint)i,
            _ => null,
        };

    /// <summary>EXIF 유리수 값 안전 변환 — Double/Single boxing 폭 흡수(GetUInt와 같은 취지).</summary>
    private static double? GetDouble(IDictionary<string, BitmapTypedValue> props, string key) =>
        Get(props, key) switch
        {
            double d => d,
            float f => f,
            _ => null,
        };

    /// <summary>EXIF ExposureProgram → 영어 문구. 미정의 값은 null(빈칸).</summary>
    private static string? ExposureProgramText(uint? v) => v switch
    {
        1 => "Manual",
        2 => "Program",
        3 => "Aperture priority",
        4 => "Shutter priority",
        5 => "Creative",
        6 => "Action",
        7 => "Portrait",
        8 => "Landscape",
        _ => null,
    };

    /// <summary>EXIF MeteringMode → 영어 문구. 미정의 값은 null(빈칸).</summary>
    private static string? MeteringModeText(uint? v) => v switch
    {
        1 => "Average",
        2 => "Center-weighted",
        3 => "Spot",
        4 => "Multi-spot",
        5 => "Pattern",
        6 => "Partial",
        _ => null,
    };

    /// <summary>EXIF WhiteBalance → 영어 문구. 미정의 값은 null(빈칸).</summary>
    private static string? WhiteBalanceText(uint? v) => v switch
    {
        0 => "Auto",
        1 => "Manual",
        _ => null,
    };

    /// <summary>EXIF Orientation → 영어 문구 — 회전 각 매핑은 ReadExifRotation(뷰어)과 같은 표.
    /// 미러링 값(2·4·5·7)은 회전만 근사 표기(뷰어 회전 적용과 같은 근사). 미정의 값은 null(빈칸).</summary>
    private static string? OrientationText(uint? v) => v switch
    {
        1 or 2 => "Normal",
        3 or 4 => "Rotated 180°",
        5 or 6 => "Rotated 90°",
        7 or 8 => "Rotated 270°",
        _ => null,
    };

    /// <summary>EXIF ColorSpace → 영어 문구. Uncalibrated(0xFFFF)·미정의 값은 null(빈칸).</summary>
    private static string? ColorSpaceText(uint? v) => v switch
    {
        1 => "sRGB",
        2 => "Adobe RGB",
        _ => null,
    };
}
