using Windows.Storage;
using KOTU.Core.Contracts;

namespace KOTU.Module.Audio;

/// <summary>
/// 오디오 정보 행(파일 기본 + 태그 + 스트림)의 단일 빌더 (A327) — 우측 정보 패널의 두 경로,
/// 즉 열린 콘텐츠(AudioPlayerView.GetContentInfoAsync)와 셸의 썸네일 선택 조회
/// (SelectionQuickInfo)가 둘 다 이 하나를 재사용한다(두 경로 표시 불일치 금지 — A200 확정
/// 원칙. 이미지 축의 ImageQuickInfo가 그 선례이자 이 클래스의 형틀이다).
/// 사양(A327): **표시 키 전부를 나열하고 값이 없는 키는 빈칸 행(라벨만)**으로 둔다 —
/// 빈 항목을 숨기는 최적화 금지(사용자 명시·부록 B 97). 어떤 실패에도 라벨은 나온다
/// (일괄 조회가 던지면 키별 개별 조회로 폴백하고, 그래도 전부 실패면 라벨만 — A239 규칙 승계).
/// 키 출처 = Windows 속성 시스템의 오디오·음악 키(System.Music.* · System.Audio.* ·
/// System.Media.* 계열) — A270이 이미 쓰는 4종(System.Media.Duration ·
/// System.Audio.EncodingBitrate/SampleRate/ChannelCount)을 포함한 같은 계열에서만 골랐다.
/// ⚠️ A270과의 정합: 저쪽(타일 2줄)은 "값 없는 조각 생략"이 그대로 옳다(타일은 좁고 라벨이
/// 없다). 이 클래스는 **정보 패널 축 전용**이라 반대로 라벨을 남긴다 — 두 축은 서로를 부르지
/// 않으므로 하단 바·타일 동작은 이 변경에 영향받지 않는다.
/// 동기 메서드 — 호출자가 전용 워커에서 돌린다(A42: WinRT 비동기를 동기 대기해도
/// UI 교착이 없는 스레드). UI 스레드 호출 금지.
/// </summary>
public static class AudioQuickInfo
{
    /// <summary>
    /// 조회 키 전부 — 표시 순서와 같다(BuildRowsFrom이 이 순서를 따른다).
    /// 선정 기준 = 파일 포맷(ID3 등 태그 + 오디오 스트림 헤더)이 정의하는 값을 셸 속성 핸들러가
    /// 노출하는 키. 스트림 배열용 키(System.Audio.StreamName/StreamNumber/PeakValue)·GUID 값 키
    /// (System.Audio.Format/Compression)·본문성 키(System.Music.Lyrics)·셸 합성 키
    /// (System.Music.AlbumID)는 제외했다 — 사람이 읽는 한 줄 값이 아니거나 포맷 정의값이 아니다.
    /// </summary>
    private static readonly string[] PropertyKeys =
    [
        // 태그 절 (System.Music.* + 곡 단위 System.Media.* + 공용 System.*)
        "System.Title", "System.Media.SubTitle",
        "System.Music.Artist", "System.Music.AlbumArtist", "System.Music.AlbumTitle",
        "System.Music.TrackNumber", "System.Music.PartOfSet",
        "System.Music.Genre", "System.Media.Year",
        "System.Music.Composer", "System.Music.Conductor",
        "System.Music.ContentGroupDescription", "System.Music.Mood",
        "System.Music.BeatsPerMinute", "System.Music.InitialKey", "System.Music.IsCompilation",
        "System.Media.Publisher", "System.Media.EncodedBy",
        "System.Comment", "System.Copyright",
        // 스트림 절 (System.Media.Duration + System.Audio.*)
        "System.Media.DateEncoded", "System.Media.Duration",
        "System.Audio.EncodingBitrate", "System.Audio.IsVariableBitRate",
        "System.Audio.SampleRate", "System.Audio.SampleSize", "System.Audio.ChannelCount",
    ];

    /// <summary>
    /// 정보 패널 행 전체: File·Size·Modified + (구분 행) + 태그·스트림 키 전부.
    /// 파일 크기·날짜 실패는 그 행만 생략하고, 속성 조회가 어떤 식으로 실패해도 키 라벨은
    /// 전부 나열한다(값만 빈칸). 값 포맷은 ImageQuickInfo의 기본 3행과 같다 — 같은 패널이다.
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

        rows.Add(ContentInfoItem.Separator); // 파일 정보 / 오디오 정보 그룹 구분 (A150 관례)
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
    /// 손상)도 같은 자리로 떨어진다 — 조용히 빈 값이지 예외를 밖으로 던지지 않는다.
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
    /// 태그 절과 스트림 절 사이에만 구분 행을 하나 더 둔다(패널이 길어지므로 — 이미지 축의
    /// "파일 / 촬영" 2절 구분과 같은 장치를 3절로 늘린 것뿐, 새 레이아웃은 없다).
    /// </summary>
    private static List<ContentInfoItem> BuildRowsFrom(Dictionary<string, object?> props)
    {
        var rows = new List<ContentInfoItem>();
        void Row(string label, string? value) =>
            rows.Add(new ContentInfoItem(label, value ?? string.Empty)); // 빈칸 행 = 라벨만(값 없음)

        Row("Title", Text(Get(props, "System.Title")));
        Row("Subtitle", Text(Get(props, "System.Media.SubTitle")));
        Row("Artist", Text(Get(props, "System.Music.Artist")));
        Row("Album artist", Text(Get(props, "System.Music.AlbumArtist")));
        Row("Album", Text(Get(props, "System.Music.AlbumTitle")));
        Row("Track", Number(Get(props, "System.Music.TrackNumber")) is { } track && track > 0
            ? $"{track}" : null);
        Row("Disc", Text(Get(props, "System.Music.PartOfSet")));
        Row("Genre", Text(Get(props, "System.Music.Genre")));
        Row("Year", Number(Get(props, "System.Media.Year")) is { } year && year > 0
            ? $"{year}" : null);
        Row("Composer", Text(Get(props, "System.Music.Composer")));
        Row("Conductor", Text(Get(props, "System.Music.Conductor")));
        Row("Grouping", Text(Get(props, "System.Music.ContentGroupDescription")));
        Row("Mood", Text(Get(props, "System.Music.Mood")));
        Row("Beats per minute", Text(Get(props, "System.Music.BeatsPerMinute")));
        Row("Initial key", Text(Get(props, "System.Music.InitialKey")));
        Row("Compilation", YesNo(Get(props, "System.Music.IsCompilation")));
        Row("Publisher", Text(Get(props, "System.Media.Publisher")));
        Row("Encoded by", Text(Get(props, "System.Media.EncodedBy")));
        Row("Comment", Text(Get(props, "System.Comment")));
        Row("Copyright", Text(Get(props, "System.Copyright")));

        rows.Add(ContentInfoItem.Separator); // 태그 절 / 스트림 절 구분

        Row("Encoded", Get(props, "System.Media.DateEncoded") is DateTimeOffset encoded
            ? $"{encoded.LocalDateTime:yyyy-MM-dd HH:mm}" : null);
        // Duration = 100ns 틱(A6·A270과 같은 단위) → 오디오 모듈 표기(TimeText)로 통일.
        Row("Duration", Number(Get(props, "System.Media.Duration")) is { } ticks && ticks > 0
            ? TimeText.Format((long)ticks / TimeSpan.TicksPerMillisecond) : null);
        // 1kbps 미만·1kHz 미만은 값이 아니라 잡음으로 본다(A270 FetchAudioInfo와 같은 하한).
        Row("Bit rate", Number(Get(props, "System.Audio.EncodingBitrate")) is { } bitrate
            && bitrate >= 1000 ? $"{bitrate / 1000} kbps" : null);
        Row("Variable bit rate", YesNo(Get(props, "System.Audio.IsVariableBitRate")));
        Row("Sample rate", Number(Get(props, "System.Audio.SampleRate")) is { } rate
            && rate >= 1000 ? $"{rate / 1000.0:0.#} kHz" : null);
        Row("Sample size", Number(Get(props, "System.Audio.SampleSize")) is { } bits && bits > 0
            ? $"{bits} bit" : null);
        Row("Channels", Number(Get(props, "System.Audio.ChannelCount")) is { } channels
            && channels > 0 ? $"{channels}" : null);
        return rows;
    }

    private static object? Get(Dictionary<string, object?> props, string key) =>
        props.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// 문자열 값 — 셸 속성 핸들러는 같은 뜻의 키를 단일 문자열 또는 문자열 배열(다중값 태그:
    /// Artist·Genre·Composer·Conductor)로 준다. 배열은 ", "로 잇고, 빈 값·공백뿐이면 null(빈칸).
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
    /// 정수 값 안전 변환 — 속성 핸들러가 주는 실제 형은 키마다 다르다(Duration = UInt64 100ns 틱,
    /// System.Audio.* = UInt32, TrackNumber·Year = UInt32). 값 없음·그 밖 형은 null(빈칸).
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
