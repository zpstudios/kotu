using System.Text;
using Windows.Data.Pdf;
using Windows.Storage;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.Module.Document;

/// <summary>
/// 문서 정보 행(파일 기본 + 갈래별 포맷 정보)의 단일 빌더 (A329) — 우측 정보 패널의 두 경로,
/// 즉 열린 콘텐츠(DocumentView.GetContentInfoAsync)와 셸의 썸네일 선택 조회
/// (SelectionQuickInfo)가 둘 다 이 하나를 재사용한다(두 경로 표시 불일치 금지 — A200 확정 원칙).
/// 형틀 = A327의 AudioQuickInfo / A328의 VideoQuickInfo(그 형틀은 다시 ImageQuickInfo).
/// 종전 실태: <b>열림 축은 아무것도 없었고</b>(DocumentView가 IContentInfoProvider를 구현하지
/// 않아 셸 폴백 4행 — File·Size·Modified·Folder뿐), 선택 축만 조각 한 행(PDF = Pages /
/// 텍스트 = Encoding)을 붙였다 — 이 클래스가 그 불일치를 없앤다.
/// <b>갈래는 둘</b>(A214가 Fit을 PDF/텍스트로 가른 것과 같은 경계):
///  · <b>PDF</b> — 포맷(ISO 32000 문서 정보 사전 + 구조)이 정의하는 속성을 셸 속성 키로 전부
///    나열한다. 값이 없어도 라벨 행을 남긴다(부록 B 98 ①).
///  · <b>텍스트·마크다운·HTML</b> — 포맷이 정의하는 속성이 <b>없다</b>. 라벨을 지어내지 않고
///    (부록 B 98의 역방향 주의 · A329 등재문 명시), <b>우리가 실제로 계산하는 값</b>만 낸다:
///    인코딩(ReadTextSmart 판정) · 줄바꿈 스타일 · 줄 수 · 글자 수.
/// 사양: 어떤 실패에도 라벨은 나온다(일괄 조회가 던지면 키별 개별 조회로 폴백하고, 그래도
/// 전부 실패면 라벨만 — A239 규칙 승계). 값 없음 = 빈칸(대시 금지).
/// ⚠️ A270의 타일·트레이 축은 무접촉이다(좁은 타일은 "값 없는 조각 생략"이 계속 옳다 —
/// 부록 B 98 ⑥). 탐색기 상세 줄(A155·A199)도 종전 그대로 <see cref="TryGetEncodingName"/>만 쓴다.
/// 동기 메서드 — 호출자가 전용 워커에서 돌린다(A42: WinRT 비동기를 동기 대기해도
/// UI 교착이 없는 스레드). UI 스레드 호출 금지.
/// </summary>
public static class DocumentQuickInfo
{
    /// <summary>
    /// 판정에 읽는 앞부분 상한. BOM은 3바이트면 충분하고, BOM 없는 UTF-8/CP949 구별도
    /// 비ASCII가 나오는 첫 지점에서 판가름 나므로 64KB면 실용적으로 넉넉하다
    /// (앞 64KB가 전부 ASCII인 파일은 UTF-8로 분류된다 — ReadTextSmart의 결론과 같은 방향).
    /// </summary>
    private const int DetectBytes = 64 * 1024;

    /// <summary>
    /// 정보 패널의 줄 수·글자 수 계산에 읽는 상한 — DocumentView.MaxBytes(4MB)와 같은 값이다.
    /// 에디터가 4MB까지만 읽어 그 뒤를 보여 주지 않으므로, 정보 패널이 그보다 많이 읽어
    /// "화면에 없는 줄까지 센 값"을 내면 두 표시가 어긋난다. 상한을 넘는 파일은 줄 수·글자 수를
    /// <b>빈칸</b>으로 둔다(잘린 앞부분의 수치를 전체인 척 내지 않는다 — 인코딩·줄바꿈은
    /// 앞부분만으로 성립하므로 그대로 낸다).
    /// </summary>
    private const int CountBytes = 4 * 1024 * 1024;

    // 인코딩 판정 이름 — 표시 값이자 디코드 분기 키라 오타가 곧 결함이다(상수로 고정).
    // ReadTextSmart의 TextEncodingKind 다섯 갈래와 1:1이다.
    private const string Utf8BomName = "UTF-8 BOM";
    private const string Utf16LeName = "UTF-16 LE";
    private const string Utf16BeName = "UTF-16 BE";
    private const string Utf8Name = "UTF-8";
    private const string Cp949Name = "CP949";

    /// <summary>열림 축(DocumentView)이 이미 아는 값으로 채우는 행의 라벨 — 오타 방지용 상수.</summary>
    public const string PageCountLabel = "Pages";

    /// <summary>
    /// PDF 조회 키 전부 — 표시 순서와 같다(BuildPdfRowsFrom이 이 순서를 따른다).
    /// 선정 기준 = <b>PDF 포맷이 정의하는 값</b>(문서 정보 사전 Title·Author·Subject·Keywords·
    /// Producer·CreationDate·ModDate + 구조값 페이지 수·버전)을 셸 속성 핸들러가 노출하는 키.
    /// A327이 세우고 A328이 승계한 제외 기준을 그대로 따른다: 식별자 값 키
    /// (System.Document.DocumentID·ClientID) · 셸·서비스 합성 키 · 포맷 정의값이 아닌 것
    /// (System.Document.WordCount/CharacterCount/LineCount/ParagraphCount·Manager·Division·
    /// Company·LastAuthor·RevisionNumber·TotalEditingTime·Template — 전부 오피스 요약 정보
    /// 계열이지 PDF가 정의하는 속성이 아니다) · 사람이 읽을 값이 아닌 비트마스크
    /// (System.Document.Security) · System.GPS.*(부록 B 69 개인정보 예외).
    /// </summary>
    private static readonly string[] PdfPropertyKeys =
    [
        // 문서 정보 사전 절 (PDF Info dictionary — 공용 System.* 키로 노출된다)
        "System.Title", "System.Author", "System.Subject", "System.Keywords", "System.Comment",
        "System.ApplicationName",
        "System.Document.DateCreated", "System.Document.DateSaved",
        // 구조 절
        "System.Document.PageCount", "System.Document.Version",
    ];

    /// <summary>
    /// 정보 패널 행 전체: File·Size·Modified + (구분 행) + 갈래별 절.
    /// pdfPageCount = 열림 축이 이미 아는 페이지 수(PdfPane.PrintPageCount, 0 = 모름) —
    /// 셸 속성 키가 값을 못 줄 때 <b>빈칸 행을 채우는 데만</b> 쓴다(행 집합은 불변이라 선택 축과
    /// 항목·순서가 어긋나지 않는다 — A328 FillFromPlayer와 같은 장치).
    /// 파일 크기·날짜 실패는 그 행만 생략하고, 속성 조회·본문 읽기가 어떤 식으로 실패해도
    /// 키 라벨은 전부 나열한다(값만 빈칸).
    /// </summary>
    public static IReadOnlyList<ContentInfoItem> BuildRows(string path, int pdfPageCount = 0)
    {
        var rows = new List<ContentInfoItem> { new("File", Path.GetFileName(path)) };
        try
        {
            var info = new FileInfo(path);
            // 크기 표기는 셸 폴백(ContentInfoOverlay.BuildBasicFileInfo)과 같은 FormatSize다 —
            // 문서·압축은 KB 단위 파일이 흔해 이미지·오디오의 MB 고정 표기가 "0 MB"가 된다.
            rows.Add(new ContentInfoItem("Size", ExplorerListing.FormatSize(info.Length)));
            rows.Add(new ContentInfoItem("Modified", $"{info.LastWriteTime:yyyy-MM-dd HH:mm}"));
        }
        catch
        {
            // 크기·날짜는 없어도 된다(ImageQuickInfo와 같은 폴백).
        }

        rows.Add(ContentInfoItem.Separator); // 파일 정보 / 문서 정보 그룹 구분 (A150 관례)
        rows.AddRange(IsPdfPath(path)
            ? BuildPdfRowsFrom(RetrieveProperties(path), path, pdfPageCount)
            : BuildTextRowsFrom(ReadTextStats(path)));
        return rows;
    }

    /// <summary>
    /// 속성 행 전부를 라벨만(값 전부 빈칸) 나열한다 — 조회 자체가 성립하지 않는 갈래
    /// (placeholder = A175 하이드레이션 금지 — 조회 0회 유지)용. 셸(SelectionQuickInfo)이 쓴다.
    /// 갈래 판정은 확장자만 보므로 파일을 건드리지 않는다.
    /// </summary>
    public static List<ContentInfoItem> BlankPropertyRows(string path) =>
        IsPdfPath(path)
            ? BuildPdfRowsFrom(new Dictionary<string, object?>(), fallbackPath: null, pdfPageCount: 0)
            : BuildTextRowsFrom(null);

    /// <summary>PDF 갈래 판정 — 라우팅(DocumentModule.Extensions의 .pdf)과 같은 짝.</summary>
    private static bool IsPdfPath(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    // ---------- PDF 갈래 ----------

    /// <summary>
    /// 셸 속성 일괄 조회 — 실패하면 키별 개별 조회로 폴백하고(성공 키만 값), 그래도 전부
    /// 실패하면 빈 사전을 돌려준다(= 라벨만 나열). 파일을 아예 못 여는 경우(삭제 경합·잠김·
    /// 손상·암호)도 같은 자리로 떨어진다 — 조용히 빈 값이지 예외를 밖으로 던지지 않는다.
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
            // 투영 형 표기에 묶인다(VideoQuickInfo와 같은 이유).
            var collected = new Dictionary<string, object?>();
            var props = file.Properties.RetrievePropertiesAsync(PdfPropertyKeys)
                .AsTask().GetAwaiter().GetResult();
            foreach (var pair in props) collected[pair.Key] = pair.Value;
            return collected;
        }
        catch
        {
            return QueryKeysIndividually(file); // 키 하나가 통째로 조회를 깨는 문서 대비
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
        foreach (var key in PdfPropertyKeys)
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
    /// PDF 표시 행 조립 — 조회 결과(부분·빈 사전 포함)에서 키 전부를 고정 순서로 만든다.
    /// 절 구분 행은 문서 정보 사전 절 / 구조 절 사이에 하나(AudioQuickInfo의 2절 구분과 같은 꼴).
    /// fallbackPath = 페이지 수 최후 폴백으로 문서를 열어도 되는 경로(null이면 열지 않는다 —
    /// placeholder 축은 조회 0회가 계약이다).
    /// </summary>
    private static List<ContentInfoItem> BuildPdfRowsFrom(
        Dictionary<string, object?> props, string? fallbackPath, int pdfPageCount)
    {
        var rows = new List<ContentInfoItem>();
        void Row(string label, string? value) =>
            rows.Add(new ContentInfoItem(label, value ?? string.Empty)); // 빈칸 행 = 라벨만(값 없음)

        Row("Title", TextOf(Get(props, "System.Title")));
        Row("Author", TextOf(Get(props, "System.Author")));
        Row("Subject", TextOf(Get(props, "System.Subject")));
        Row("Keywords", TextOf(Get(props, "System.Keywords")));
        Row("Comment", TextOf(Get(props, "System.Comment")));
        // PDF의 Producer(파일을 실제로 써 낸 응용) = 속성 시스템의 "파일을 만든 응용 이름".
        Row("Producer", TextOf(Get(props, "System.ApplicationName")));
        Row("Created", DateText(Get(props, "System.Document.DateCreated")));
        // 파일 기본 절의 Modified(디스크 수정 시각)와 뜻이 다르다 — 문서가 스스로 적어 둔 값이다.
        Row("Saved", DateText(Get(props, "System.Document.DateSaved")));

        rows.Add(ContentInfoItem.Separator); // 문서 정보 사전 절 / 구조 절 구분

        Row(PageCountLabel, PageCountText(props, fallbackPath, pdfPageCount));
        Row("PDF version", TextOf(Get(props, "System.Document.Version")));
        return rows;
    }

    /// <summary>
    /// 페이지 수 — ① 셸 속성 키 ② 열림 축이 넘긴 값(PdfPane이 이미 연 문서의 PageCount)
    /// ③ 최후로 문서를 직접 열어 읽는다(선택 축의 종전 동작 보존 — SelectionQuickInfo가
    /// 조각 한 행으로 내던 값이 바로 이것이다. 암호 PDF 등 실패는 빈칸).
    /// </summary>
    private static string? PageCountText(
        Dictionary<string, object?> props, string? fallbackPath, int pdfPageCount)
    {
        if (Number(Get(props, "System.Document.PageCount")) is { } pages && pages > 0)
            return $"{pages}";
        if (pdfPageCount > 0) return $"{pdfPageCount}";
        if (fallbackPath is null) return null;
        try
        {
            var file = StorageFile.GetFileFromPathAsync(fallbackPath).AsTask().GetAwaiter().GetResult();
            var doc = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            return doc.PageCount > 0 ? $"{doc.PageCount}" : null;
        }
        catch
        {
            return null; // 암호·손상·삭제 경합 — 빈칸 행
        }
    }

    // ---------- 텍스트·마크다운·HTML 갈래 ----------

    /// <summary>
    /// 텍스트 갈래에서 우리가 실제로 계산하는 값. 전부 null이면 라벨만 나열된다.
    /// 포맷이 정의하는 속성이 아니라 <b>계산값</b>이므로 라벨을 늘리지 않는다(A329 명시).
    /// </summary>
    private sealed record TextStats(string? Encoding, string? NewLine, int? Lines, int? Characters);

    /// <summary>텍스트 표시 행 조립 — 값 없음(읽기 실패·상한 초과)도 라벨 행은 남는다.</summary>
    private static List<ContentInfoItem> BuildTextRowsFrom(TextStats? stats)
    {
        var rows = new List<ContentInfoItem>();
        void Row(string label, string? value) =>
            rows.Add(new ContentInfoItem(label, value ?? string.Empty));

        Row("Encoding", stats?.Encoding);
        Row("Line endings", stats?.NewLine);
        Row("Lines", stats?.Lines is { } lines ? $"{lines:N0}" : null);
        Row("Characters", stats?.Characters is { } chars ? $"{chars:N0}" : null);
        return rows;
    }

    /// <summary>
    /// 인코딩·줄바꿈·줄 수·글자 수를 한 번의 읽기로 계산한다(상한 CountBytes).
    /// 판정 규칙은 ReadTextSmart·<see cref="TryGetEncodingName"/>과 같고, 상한을 넘는 파일은
    /// 줄 수·글자 수만 빈칸으로 둔다(에디터도 앞 4MB만 보여 준다 — 상수 주석 참고).
    /// 읽기·디코드 실패는 null(= 라벨만) — 예외를 밖으로 던지지 않는다.
    /// </summary>
    private static TextStats? ReadTextStats(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            // 빈 파일은 판정할 바이트가 없다 — 인코딩·줄바꿈은 빈칸이지만 "0자 1줄"은 사실이다
            // (에디터가 여는 모습과 같다 — CountLines("") = 1).
            if (stream.Length == 0) return new TextStats(null, null, 1, 0);

            var truncated = stream.Length > CountBytes;
            var bytes = new byte[(int)Math.Min(stream.Length, CountBytes)];
            stream.ReadExactly(bytes);

            var kind = DetectKind(bytes, truncated);
            var text = Decode(bytes, kind);
            if (text is null) return new TextStats(kind, null, null, null);

            // 줄바꿈 스타일: 첫 줄바꿈 기준(ReadTextSmart와 같은 셈법). 줄바꿈이 아예 없으면
            // 관측된 값이 없으므로 빈칸이다 — 저장 시 기본값(CRLF)은 표시 축의 값이 아니다.
            var lf = text.IndexOf('\n');
            var newline = lf > 0 && text[lf - 1] == '\r' ? "CRLF" : lf >= 0 ? "LF" : null;

            return truncated
                ? new TextStats(kind, newline, null, null)
                : new TextStats(kind, newline, CountLines(text), text.Length);
        }
        catch
        {
            return null; // 읽기 실패(잠김·권한·삭제 경합 등) = 라벨만
        }
    }

    /// <summary>
    /// 인코딩 판정의 짧은 표시 이름 — ReadTextSmart의 TextEncodingKind 다섯 갈래와 1:1이다
    /// ("UTF-8" · "UTF-8 BOM" · "UTF-16 LE" · "UTF-16 BE" · "CP949").
    /// 판정 불가·실패(빈 파일·잠김·삭제 경합 등)는 null = 호출부가 조각을 생략한다.
    /// CP949 분류는 ReadTextSmart처럼 "엄격 UTF-8이 아니다"의 소거법이라 실제 디코드가 없다 —
    /// 코드페이지 제공자 등록(Encoding.RegisterProvider) 없이 동작한다.
    /// </summary>
    public static string? TryGetEncodingName(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length == 0) return null; // 빈 파일 — 판정할 바이트가 없다
            var truncated = stream.Length > DetectBytes;
            var bytes = new byte[(int)Math.Min(stream.Length, DetectBytes)];
            stream.ReadExactly(bytes);
            return DetectKind(bytes, truncated);
        }
        catch
        {
            return null; // 읽기 실패(잠김·권한·삭제 경합 등) = 조각 생략
        }
    }

    /// <summary>
    /// 바이트 앞부분에서 인코딩 이름을 판정한다 — BOM 우선, 없으면 엄격 UTF-8 시도,
    /// 깨질 때만 CP949로 분류(ReadTextSmart와 같은 소거법).
    /// truncated = 상한에서 잘렸다는 표지: 꼬리의 불완전한 UTF-8 시퀀스를 떼고 검사해야
    /// 진짜 UTF-8이 CP949로 오판되지 않는다(전체를 읽은 파일의 불완전한 꼬리는 진짜 손상이므로
    /// 그대로 검사에 넣는다 — ReadTextSmart와 같은 결론이 나오는 방향).
    /// </summary>
    private static string DetectKind(byte[] bytes, bool truncated)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Utf8BomName;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Utf16LeName;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Utf16BeName;

        var length = truncated ? TrimIncompleteUtf8Tail(bytes) : bytes.Length;
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, 0, length);
            return Utf8Name;
        }
        catch (DecoderFallbackException)
        {
            return Cp949Name; // 레거시 한글 — ReadTextSmart와 같은 소거법 분류
        }
    }

    /// <summary>
    /// 판정 결과대로 디코드한다(줄 수·글자 수 계산용 — 표시 문자열이 아니다).
    /// CP949는 코드페이지 제공자를 직접 꺼내 쓴다(Cp949ZipReader·SubtitleCharset과 같은 관용구 —
    /// Encoding.RegisterProvider 여부에 기대지 않는다: 이 클래스는 셸에서도 불린다).
    /// 실패·제공자 부재는 null = 줄 수·글자 수만 빈칸.
    /// </summary>
    private static string? Decode(byte[] bytes, string kind)
    {
        try
        {
            return kind switch
            {
                Utf8BomName => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
                Utf16LeName => Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
                Utf16BeName => Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
                Utf8Name => Encoding.UTF8.GetString(bytes),
                _ => CodePagesEncodingProvider.Instance.GetEncoding(949)?.GetString(bytes),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 논리 줄 수 — DocumentView.CountLines·EditorDecor.EnsureLineStarts와 같은 셈법
    /// (CRLF는 한 개행, 끝 개행 뒤의 빈 마지막 줄도 한 줄). 상한(CountBytes) 안에서만 부른다.
    /// </summary>
    private static int CountLines(string text)
    {
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lines++;
        }
        return lines;
    }

    /// <summary>
    /// 버퍼 끝의 불완전한 UTF-8 시퀀스를 제외한 길이. 끝에서 최대 3바이트의 연속 바이트
    /// (상위 2비트 = 0x80)를 거슬러 리드 바이트를 찾고, 그 시퀀스의 기대 길이가 버퍼를 넘으면
    /// 리드 바이트 앞에서 자른다. 리드 바이트가 ASCII거나 형식 불량이면 자르지 않는다 —
    /// 엄격 디코드가 원래 규칙대로 판정한다.
    /// </summary>
    private static int TrimIncompleteUtf8Tail(byte[] bytes)
    {
        for (var i = bytes.Length - 1; i >= 0 && i >= bytes.Length - 4; i--)
        {
            if ((bytes[i] & 0xC0) == 0x80) continue; // 연속 바이트 — 더 거슬러 간다
            var expected = (bytes[i] & 0xE0) == 0xC0 ? 2
                : (bytes[i] & 0xF0) == 0xE0 ? 3
                : (bytes[i] & 0xF8) == 0xF0 ? 4
                : 1; // ASCII 또는 불량 리드 — 불완전으로 보지 않는다
            return bytes.Length - i < expected ? i : bytes.Length;
        }
        return bytes.Length; // 끝 4바이트가 전부 연속 바이트 — 불량이므로 그대로 검사
    }

    // ---------- 값 변환 헬퍼 (VideoQuickInfo와 같은 규칙) ----------

    private static object? Get(Dictionary<string, object?> props, string key) =>
        props.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// 문자열 값 — 셸 속성 핸들러는 같은 뜻의 키를 단일 문자열 또는 문자열 배열
    /// (다중값: Author·Keywords 등)로 준다. 배열은 ", "로 잇고, 빈 값·공백뿐이면 null(빈칸).
    /// </summary>
    private static string? TextOf(object? value)
    {
        var text = value switch
        {
            string s => s,
            IEnumerable<string> many => string.Join(", ", many.Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>날짜 값 → 파일 기본 절의 Modified와 같은 표기. 값 없음·그 밖 형은 null(빈칸).</summary>
    private static string? DateText(object? value) => value switch
    {
        DateTimeOffset offset => $"{offset.LocalDateTime:yyyy-MM-dd HH:mm}",
        DateTime time => $"{time:yyyy-MM-dd HH:mm}",
        _ => null,
    };

    /// <summary>
    /// 정수 값 안전 변환 — 속성 핸들러가 주는 실제 형은 키마다 다르다(PageCount = UInt32).
    /// 값 없음·그 밖 형은 null(빈칸). "0"과 "값 없음"을 구분해야 해서 null 반환으로 둔다.
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
}
