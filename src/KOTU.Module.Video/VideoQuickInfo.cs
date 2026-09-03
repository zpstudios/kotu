using Windows.Storage;
using KOTU.Core.Contracts;

namespace KOTU.Module.Video;

/// <summary>
/// 영상 정보 행(파일 기본 + 메타데이터 + 비디오/오디오 스트림)의 단일 빌더 (A328) —
/// 우측 정보 패널의 두 경로, 즉 열린 콘텐츠(VideoPlayerView.GetContentInfoAsync)와 셸의
/// 썸네일 선택 조회(SelectionQuickInfo)가 둘 다 이 하나를 재사용한다(두 경로 표시 불일치
/// 금지 — A200 확정 원칙). 형틀 = A327의 AudioQuickInfo(그 형틀은 다시 ImageQuickInfo).
/// 종전 실태: 열림 축은 File·Size·Modified·Duration + 재생 중에만 libvlc 트랙 행,
/// 선택 축은 Length 한 줄뿐이라 두 축이 서로 다른 것을 보여 줬다 — 이 클래스가 그 불일치를 없앤다.
/// 사양(A328): **표시 키 전부를 나열하고 값이 없는 키는 빈칸 행(라벨만)**으로 둔다 —
/// 빈 항목을 숨기는 최적화 금지(사용자 명시·부록 B 97). 어떤 실패에도 라벨은 나온다
/// (일괄 조회가 던지면 키별 개별 조회로 폴백하고, 그래도 전부 실패면 라벨만 — A239 규칙 승계).
/// 키 출처 = Windows 속성 시스템의 영상·미디어 키(System.Video.* · System.Media.* ·
/// System.Audio.* 계열 + 공용 System.*) — 임의 발명 금지(A327과 같은 규칙).
/// ⚠️ A270과의 정합: 저쪽(타일 2줄)은 "값 없는 조각 생략"이 그대로 옳다(타일은 좁고 라벨이
/// 없다). 이 클래스는 **정보 패널 축 전용**이라 반대로 라벨을 남긴다 — 두 축은 서로를 부르지
/// 않으므로 하단 바·타일 동작은 이 변경에 영향받지 않는다.
/// 동기 메서드 — 호출자가 전용 워커에서 돌린다(A42: WinRT 비동기를 동기 대기해도
/// UI 교착이 없는 스레드). UI 스레드 호출 금지.
/// </summary>
public static class VideoQuickInfo
{
    /// <summary>
    /// 조회 키 전부 — 표시 순서와 같다(BuildRowsFrom이 이 순서를 따른다).
    /// 선정 기준 = 파일 포맷(컨테이너 메타데이터 + 비디오/오디오 스트림 헤더)이 정의하는 값을
    /// 셸 속성 핸들러가 노출하는 키. A327이 세운 제외 기준을 그대로 승계했다:
    /// 스트림 배열용 키(System.Video.StreamName/StreamNumber) · 식별자/GUID 값 키
    /// (System.Media.ClassPrimaryID/ClassSecondaryID/CollectionID/CollectionGroupID/ContentID/
    /// DVDID/MCDI/UniqueFileIdentifier/SubscriptionContentId) · 셸·서비스 합성 키
    /// (System.Media.MetadataContentProvider/ProviderStyle/AuthorUrl/PromotionUrl/UserWebUrl/
    /// UserNoAutoInfo · System.Video.TranscodedForSync) · 포맷 정의값이 아닌 사용자/서비스
    /// 평점(System.Rating · System.Media.ProviderRating) · System.GPS.*(부록 B 69 개인정보 예외)는
    /// 제외한다. 방송 녹화 전용 계열(System.RecordedTV.*)도 파일 포맷 정의값이 아니라 제외.
    /// </summary>
    private static readonly string[] PropertyKeys =
    [
        // 메타데이터 절 (컨테이너 태그 — System.Media.* + System.Video.Director + 공용 System.*)
        "System.Title", "System.Media.SubTitle",
        "System.Video.Director", "System.Media.Producer", "System.Media.Writer",
        "System.Media.Publisher", "System.Media.ContentDistributor",
        "System.Media.Year", "System.Media.DateReleased",
        "System.Media.CreatorApplication", "System.Media.CreatorApplicationVersion",
        "System.Media.EncodedBy", "System.Media.EncodingSettings",
        "System.Media.ProtectionType",
        "System.Comment", "System.Copyright",
        // 비디오 스트림 절 (System.Media.Duration/DateEncoded + System.Video.*)
        "System.Media.DateEncoded", "System.Media.Duration",
        "System.Video.FrameWidth", "System.Video.FrameHeight",
        "System.Video.HorizontalAspectRatio", "System.Video.VerticalAspectRatio",
        "System.Video.FrameRate", "System.Video.Orientation",
        "System.Video.EncodingBitrate", "System.Video.TotalBitrate",
        "System.Video.SampleSize", "System.Video.Compression", "System.Video.FourCC",
        "System.Video.IsStereo", "System.Video.IsSpherical",
        // 오디오 스트림 절 (System.Audio.* — 영상 컨테이너의 오디오 트랙도 포맷 정의값이다)
        "System.Audio.EncodingBitrate", "System.Audio.IsVariableBitRate",
        "System.Audio.SampleRate", "System.Audio.SampleSize", "System.Audio.ChannelCount",
    ];

    /// <summary>열림 축(VideoPlayerView)이 libvlc 값으로 뒤늦게 채우는 행의 라벨 — 오타 방지용 상수.</summary>
    public const string DurationLabel = "Duration";

    /// <summary>〃 (프레임 폭·높이·프레임률·코덱 — 셸 핸들러가 못 주는 컨테이너 대비).</summary>
    public const string FrameWidthLabel = "Frame width";

    /// <summary>〃</summary>
    public const string FrameHeightLabel = "Frame height";

    /// <summary>〃</summary>
    public const string FrameRateLabel = "Frame rate";

    /// <summary>〃</summary>
    public const string VideoCodecLabel = "Video codec";

    /// <summary>
    /// 정보 패널 행 전체: File·Size·Modified + (구분 행) + 메타데이터·비디오·오디오 키 전부.
    /// 파일 크기·날짜 실패는 그 행만 생략하고, 속성 조회가 어떤 식으로 실패해도 키 라벨은
    /// 전부 나열한다(값만 빈칸). 값 포맷은 ImageQuickInfo·AudioQuickInfo의 기본 3행과 같다 —
    /// 같은 패널이다.
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
            // 크기·날짜는 없어도 된다(ImageQuickInfo와 같은 폴백).
        }

        rows.Add(ContentInfoItem.Separator); // 파일 정보 / 영상 정보 그룹 구분 (A150 관례)
        rows.AddRange(BuildRowsFrom(RetrieveProperties(path)));
        return rows;
    }

    /// <summary>
    /// 속성 행 전부를 라벨만(값 전부 빈칸) 나열한다 — 조회 자체가 성립하지 않는 갈래
    /// (placeholder = A175 하이드레이션 금지 — 조회 0회 유지)용. 셸(SelectionQuickInfo)이 쓴다.
    /// </summary>
    public static List<ContentInfoItem> BlankPropertyRows() =>
        BuildRowsFrom(new Dictionary<string, object?>());

    /// <summary>
    /// 셸 속성 일괄 조회 — 실패하면 키별 개별 조회로 폴백하고(성공 키만 값), 그래도 전부
    /// 실패하면 빈 사전을 돌려준다(= 라벨만 나열). 파일을 아예 못 여는 경우(삭제 경합·잠김·
    /// 손상·코덱 미지원)도 같은 자리로 떨어진다 — 조용히 빈 값이지 예외를 밖으로 던지지 않는다.
    /// </summary>
    private static Dictionary<string, object?> RetrieveProperties(string path)
    {
        StorageFile file;
        try
        {
            file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            return new Dictionary<string, object?>(); // 파일 자체를 못 열었다 — 라벨만 나열
        }

        try
        {
            // 결과는 자체 사전으로 옮겨 담는다 — WinRT 투영 사전을 그대로 돌려주면 서명이
            // 투영 형 표기(원소 null 허용 여부 포함)에 묶인다(A270 PropNumber 주석과 같은 이유).
            var collected = new Dictionary<string, object?>();
            var props = file.Properties.RetrievePropertiesAsync(PropertyKeys)
                .AsTask().GetAwaiter().GetResult();
            foreach (var pair in props) collected[pair.Key] = pair.Value;
            return collected;
        }
        catch
        {
            return QueryKeysIndividually(file); // 키 하나가 통째로 조회를 깨는 컨테이너 대비
        }
    }

    /// <summary>
    /// 키별 개별 조회 폴백(A239 ① 관용구) — 일괄 조회가 통째로 던질 때 어느 키가 막혔는지
    /// 갈라낸다(성공 키만 수집·실패 키는 빈칸 행으로 남는다). 키당 1회씩이지만 전용 워커에서만
    /// 돌므로(클래스 계약) UI 체감 비용은 없다.
    /// </summary>
    private static Dictionary<string, object?> QueryKeysIndividually(StorageFile file)
    {
        var collected = new Dictionary<string, object?>();
        foreach (var key in PropertyKeys)
        {
            try
            {
                var one = file.Properties.RetrievePropertiesAsync(new[] { key })
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

    /// <summary>
    /// 표시 행 조립 — 조회 결과(부분·빈 사전 포함)에서 키 전부를 고정 순서로 만든다.
    /// 절 구분 행은 메타데이터 / 비디오 스트림 / 오디오 스트림 사이에 하나씩(AudioQuickInfo의
    /// 2절 구분을 3절로 늘린 것뿐 — 새 레이아웃은 없다). 오디오 절의 라벨에는 "Audio"를 붙여
    /// 비디오 절의 같은 뜻 행(비트레이트·샘플 크기)과 눈으로 구분되게 한다.
    /// </summary>
    private static List<ContentInfoItem> BuildRowsFrom(Dictionary<string, object?> props)
    {
        var rows = new List<ContentInfoItem>();
        void Row(string label, string? value) =>
            rows.Add(new ContentInfoItem(label, value ?? string.Empty)); // 빈칸 행 = 라벨만(값 없음)

        Row("Title", Text(Get(props, "System.Title")));
        Row("Subtitle", Text(Get(props, "System.Media.SubTitle")));
        Row("Director", Text(Get(props, "System.Video.Director")));
        Row("Producer", Text(Get(props, "System.Media.Producer")));
        Row("Writer", Text(Get(props, "System.Media.Writer")));
        Row("Publisher", Text(Get(props, "System.Media.Publisher")));
        Row("Distributor", Text(Get(props, "System.Media.ContentDistributor")));
        Row("Year", Number(Get(props, "System.Media.Year")) is { } year && year > 0
            ? $"{year}" : null);
        Row("Released", Text(Get(props, "System.Media.DateReleased")));
        Row("Created by", Text(Get(props, "System.Media.CreatorApplication")));
        Row("Creator version", Text(Get(props, "System.Media.CreatorApplicationVersion")));
        Row("Encoded by", Text(Get(props, "System.Media.EncodedBy")));
        Row("Encoding settings", Text(Get(props, "System.Media.EncodingSettings")));
        Row("Protection", Text(Get(props, "System.Media.ProtectionType")));
        Row("Comment", Text(Get(props, "System.Comment")));
        Row("Copyright", Text(Get(props, "System.Copyright")));

        rows.Add(ContentInfoItem.Separator); // 메타데이터 절 / 비디오 스트림 절 구분

        Row("Encoded", Get(props, "System.Media.DateEncoded") is DateTimeOffset encoded
            ? $"{encoded.LocalDateTime:yyyy-MM-dd HH:mm}" : null);
        // Duration = 100ns 틱(A6·A270과 같은 단위) → 영상 모듈 표기(TimeText)로 통일.
        Row(DurationLabel, Number(Get(props, "System.Media.Duration")) is { } ticks && ticks > 0
            ? TimeText.Format((long)ticks / TimeSpan.TicksPerMillisecond) : null);
        Row(FrameWidthLabel, Number(Get(props, "System.Video.FrameWidth")) is { } width && width > 0
            ? $"{width} px" : null);
        Row(FrameHeightLabel, Number(Get(props, "System.Video.FrameHeight")) is { } height && height > 0
            ? $"{height} px" : null);
        // 화면비는 가로·세로가 별도 키다 — 키 하나에 행 하나 규칙대로 두 행으로 둔다
        // (한쪽만 오는 컨테이너에서도 온 쪽은 값이 보인다).
        Row("Horizontal aspect ratio", Number(Get(props, "System.Video.HorizontalAspectRatio"))
            is { } aspectH && aspectH > 0 ? $"{aspectH}" : null);
        Row("Vertical aspect ratio", Number(Get(props, "System.Video.VerticalAspectRatio"))
            is { } aspectV && aspectV > 0 ? $"{aspectV}" : null);
        // FrameRate 단위 = "1000초당 프레임 수"(속성 시스템 정의: 29970 = 29.97 fps).
        Row(FrameRateLabel, Number(Get(props, "System.Video.FrameRate")) is { } fps && fps > 0
            ? $"{fps / 1000.0:0.##} fps" : null);
        Row("Orientation", Number(Get(props, "System.Video.Orientation")) is { } orientation
            ? $"{orientation}°" : null);
        // 1kbps 미만은 값이 아니라 잡음으로 본다(A270 FetchAudioInfo와 같은 하한).
        Row("Video bit rate", Number(Get(props, "System.Video.EncodingBitrate")) is { } videoRate
            && videoRate >= 1000 ? $"{videoRate / 1000} kbps" : null);
        Row("Total bit rate", Number(Get(props, "System.Video.TotalBitrate")) is { } totalRate
            && totalRate >= 1000 ? $"{totalRate / 1000} kbps" : null);
        Row("Sample size", Number(Get(props, "System.Video.SampleSize")) is { } videoBits
            && videoBits > 0 ? $"{videoBits} bit" : null);
        Row("Compression", Codec(Get(props, "System.Video.Compression")));
        Row(VideoCodecLabel, Codec(Get(props, "System.Video.FourCC")));
        Row("Stereoscopic", YesNo(Get(props, "System.Video.IsStereo")));
        Row("Spherical", YesNo(Get(props, "System.Video.IsSpherical")));

        rows.Add(ContentInfoItem.Separator); // 비디오 스트림 절 / 오디오 스트림 절 구분

        Row("Audio bit rate", Number(Get(props, "System.Audio.EncodingBitrate")) is { } audioRate
            && audioRate >= 1000 ? $"{audioRate / 1000} kbps" : null);
        Row("Audio variable bit rate", YesNo(Get(props, "System.Audio.IsVariableBitRate")));
        Row("Audio sample rate", Number(Get(props, "System.Audio.SampleRate")) is { } rate
            && rate >= 1000 ? $"{rate / 1000.0:0.#} kHz" : null);
        Row("Audio sample size", Number(Get(props, "System.Audio.SampleSize")) is { } bits && bits > 0
            ? $"{bits} bit" : null);
        Row("Audio channels", Number(Get(props, "System.Audio.ChannelCount")) is { } channels
            && channels > 0 ? $"{channels}" : null);
        return rows;
    }

    private static object? Get(Dictionary<string, object?> props, string key) =>
        props.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// 문자열 값 — 셸 속성 핸들러는 같은 뜻의 키를 단일 문자열 또는 문자열 배열(다중값 태그:
    /// Director·Producer·Writer 등)로 준다. 배열은 ", "로 잇고, 빈 값·공백뿐이면 null(빈칸).
    /// </summary>
    private static string? Text(object? value)
    {
        var text = value switch
        {
            string s => s,
            IEnumerable<string> many => string.Join(", ", many.Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// 코덱 표기 값(System.Video.Compression · System.Video.FourCC) — 핸들러가 주는 형이
    /// 컨테이너마다 다르다: FourCC 정수(VT_UI4), 4글자 문자열, 또는 GUID 문자열.
    /// GUID는 사람이 읽는 값이 아니므로 빈칸으로 눕힌다(A327이 System.Audio.Format/Compression을
    /// 키째로 제외한 것과 같은 근거 — 다만 영상 쪽은 읽히는 값이 오는 컨테이너가 있어 키는
    /// 남기고 값만 거른다). 글자로 풀리지 않는 정수도 빈칸.
    /// </summary>
    private static string? Codec(object? value)
    {
        if (value is string s)
        {
            var text = s.Trim();
            if (text.Length == 0 || Guid.TryParse(text, out _)) return null;
            return text;
        }
        if (Number(value) is not { } code || code == 0) return null;
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var c = (char)((code >> (8 * i)) & 0xFF);
            if (!char.IsLetterOrDigit(c) && c != ' ') return null; // FourCC가 아니다 — 빈칸
            chars[i] = c;
        }
        var fourCc = new string(chars).Trim();
        return fourCc.Length == 0 ? null : fourCc;
    }

    /// <summary>
    /// 정수 값 안전 변환 — 속성 핸들러가 주는 실제 형은 키마다 다르다(Duration = UInt64 100ns 틱,
    /// System.Video.*·System.Audio.* = UInt32). 값 없음·그 밖 형은 null(빈칸).
    /// A270 PropNumber와 같은 취지지만 "0"과 "값 없음"을 구분해야 해서 null 반환으로 둔다.
    /// </summary>
    private static ulong? Number(object? value) => value switch
    {
        ulong u => u,
        uint ui => ui,
        ushort us => us,
        long l when l >= 0 => (ulong)l,
        int i when i >= 0 => (ulong)i,
        double d when d >= 0 => (ulong)d,
        _ => null,
    };

    /// <summary>불리언 값 → Yes/No. 값 없음은 null(빈칸) — false와 구분한다.</summary>
    private static string? YesNo(object? value) => value switch
    {
        bool b => b ? "Yes" : "No",
        _ => null,
    };
}
