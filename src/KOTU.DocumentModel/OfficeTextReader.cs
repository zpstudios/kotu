using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KOTU.DocumentModel;

// 본 제품은 한글과컴퓨터의 한글 문서 파일(.hwp) 공개 문서를 참고하여 개발하였습니다.
// This product was developed by referring to Hancom's public HWP document file specification.
public sealed record OfficeTextPreview(string Format, string Text, bool Truncated);

/// <summary>오피스 파일의 오프라인 본문 미리보기. 페이지 레이아웃 렌더러나 편집기가 아니다.</summary>
public static class OfficeTextReader
{
    public const int MaxFileBytes = 64 * 1024 * 1024;
    internal const int MaxPartBytes = 8 * 1024 * 1024;
    public const int MaxTextCharacters = 200_000;
    public static readonly IReadOnlyList<string> Extensions = Array.AsReadOnly(new[] { ".hwp", ".hwpx", ".odt", ".ods", ".odp" });
    private const string Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string Hp = "http://www.hancom.co.kr/hwpml/2011/paragraph";
    private const string Opf = "http://www.idpf.org/2007/opf/";

    public static bool Supports(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static OfficeTextPreview Read(string path, CancellationToken cancellation = default)
    {
        // 공유 읽기 스냅샷만 소비한다. 소스 파일·AppData·TEMP에는 아무것도 쓰지 않는다.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Read(stream, Path.GetExtension(path), cancellation);
    }

    public static OfficeTextPreview Read(Stream stream, string extension, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        extension = extension.ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new NotSupportedException("Unsupported office document format.");
        var bytes = ReadBounded(stream, MaxFileBytes, cancellation);
        var output = new PreviewText(cancellation);
        try
        {
            if (extension == ".hwp") ReadHwp(bytes, output, cancellation);
            else ReadPackage(bytes, extension, output, cancellation);
        }
        catch (PreviewLimitException) { output.Truncated = true; }
        return new OfficeTextPreview(extension[1..].ToUpperInvariant(), output.ToString().Trim(), output.Truncated);
    }

    private static void ReadPackage(byte[] bytes, string extension, PreviewText output, CancellationToken cancel)
    {
        using var memory = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        if (zip.Entries.Count > 4096) throw Invalid("The document contains too many package entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long total = 0;
        long readTotal = 0;
        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            if (entry.FullName.StartsWith('/') || entry.FullName.Contains('\\') || entry.FullName.Contains(':')
                || entry.FullName.Split('/').Any(part => part is "." or "..") || !entries.TryAdd(entry.FullName, entry))
                throw Invalid("Invalid or duplicate package path.");
            total = checked(total + entry.Length);
            if (total > 256L * 1024 * 1024) throw Invalid("The expanded document exceeds the preview limit.");
        }
        var mime = Encoding.ASCII.GetString(Part("mimetype", 256)).Trim();
        if (extension == ".hwpx")
        {
            if (mime != "application/hwp+zip") throw Invalid("This is not a supported HWPX package.");
            var package = Xml(Part("Contents/content.hpf"), cancel);
            XNamespace opf = Opf;
            // HWPX의 섹션 순서는 숫자 정렬보다 OPF spine이 우선한다.
            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in package.Descendants(opf + "item"))
            {
                var id = (string?)item.Attribute("id");
                var href = (string?)item.Attribute("href");
                if (id is not null && href is not null && !items.TryAdd(id, href)) throw Invalid("Duplicate HWPX manifest ID.");
            }
            var refs = package.Descendants(opf + "itemref").Select(item => (string?)item.Attribute("idref")).ToArray();
            if (refs.Length is 0 or > 128) throw Invalid("Missing or oversized HWPX section list.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sectionCount = 0;
            foreach (var id in refs)
            {
                cancel.ThrowIfCancellationRequested();
                if (id is null || !items.TryGetValue(id, out var href) || !seen.Add(href)) throw Invalid("Invalid HWPX section reference.");
                // 참조는 패키지 안 상대 경로만 허용한다. 파일 시스템이나 네트워크로 해석하지 않는다.
                if (href.Contains(':') || href.Contains('\\') || href.StartsWith('/') || href.Split('/').Any(p => p is "." or ".."))
                    throw Invalid("External HWPX references are not supported.");
                var name = href.StartsWith("Contents/", StringComparison.Ordinal) ? href : "Contents/" + href;
                var section = Xml(Part(name), cancel);
                // 한컴 패키지는 spine에 공통 header도 싣는다. 본문 섹션으로 오인하지 않는다.
                if (section.Root?.Name == XName.Get("head", "http://www.hancom.co.kr/hwpml/2011/head")) continue;
                if (section.Root?.Name != XName.Get("sec", "http://www.hancom.co.kr/hwpml/2011/section"))
                    throw Invalid("Invalid HWPX section.");
                sectionCount++;
                HwpInline(section.Root!, output);
                output.Add("\n");
            }
            if (sectionCount == 0) throw Invalid("Missing HWPX body sections.");
            return;
        }

        var kind = extension switch { ".odt" => "text", ".ods" => "spreadsheet", _ => "presentation" };
        if (mime != "application/vnd.oasis.opendocument." + kind) throw Invalid("The document type does not match its extension.");
        var manifest = Xml(Part("META-INF/manifest.xml"), cancel);
        XNamespace manifestNs = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        if (manifest.Descendants(manifestNs + "encryption-data").Any())
            throw new NotSupportedException("Encrypted OpenDocument files are not supported. Open an unencrypted copy or export to PDF.");
        var content = Xml(Part("content.xml"), cancel);
        var body = content.Root?.Element(XName.Get("body", Office))?.Element(XName.Get(kind, Office));
        if (body is null) throw Invalid("Missing OpenDocument body.");
        WalkOdf(body, output);
        return;

        byte[] Part(string name, int limit = MaxPartBytes)
        {
            if (!entries.TryGetValue(name, out var entry)) throw Invalid("Missing package part: " + name);
            if (entry.Length > limit) throw Invalid("A document part exceeds the preview limit.");
            using var source = entry.Open();
            var data = ReadBounded(source, limit, cancel);
            readTotal += data.Length;
            if (readTotal > MaxFileBytes) throw Invalid("The expanded document text exceeds the preview limit.");
            return data;
        }
    }

    private static XDocument Xml(byte[] bytes, CancellationToken cancel)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaxPartBytes, MaxCharactersFromEntities = 1024 };
        using (var stream = new MemoryStream(bytes, false))
        using (var reader = XmlReader.Create(stream, settings))
        {
            var count = 0;
            while (reader.Read())
            {
                cancel.ThrowIfCancellationRequested();
                if (reader.Depth > 64 || ++count > 200_000) throw Invalid("The XML document is too complex for preview.");
            }
        }
        cancel.ThrowIfCancellationRequested();
        using var input = new MemoryStream(bytes, false);
        using var second = XmlReader.Create(input, settings);
        var document = XDocument.Load(second, LoadOptions.PreserveWhitespace);
        cancel.ThrowIfCancellationRequested();
        return document;
    }

    private static void WalkOdf(XElement element, PreviewText output)
    {
        output.Check();
        var ns = element.Name.NamespaceName;
        var name = element.Name.LocalName;
        if ((ns == Office && name is "scripts" or "annotation") || (ns == TextNs && name is "tracked-changes" or "deletion")) return;
        if (ns == TextNs && name is "p" or "h")
        {
            OdfInline(element, output);
            output.Add("\n");
            return;
        }
        if (ns == Table && name == "table") output.Add("\n[" + ((string?)element.Attribute(XName.Get("name", Table)) ?? "Table") + "]\n");
        if (ns == Draw && name == "page") output.Add("\n[Slide: " + ((string?)element.Attribute(XName.Get("name", Draw)) ?? "Slide") + "]\n");
        var start = output.Length;
        foreach (var child in element.Elements()) WalkOdf(child, output);
        if (ns == Table && name is "table-cell" or "covered-table-cell")
        {
            if (output.Length == start)
                output.Add((string?)element.Attribute(XName.Get("string-value", Office))
                    ?? (string?)element.Attribute(XName.Get("value", Office))
                    ?? (string?)element.Attribute(XName.Get("date-value", Office))
                    ?? (string?)element.Attribute(XName.Get("time-value", Office))
                    ?? (string?)element.Attribute(XName.Get("boolean-value", Office)) ?? "");
            output.Add("\t");
            Repeat(element, "number-columns-repeated", start, output);
        }
        if (ns == Table && name == "table-row")
        {
            output.Add("\n");
            Repeat(element, "number-rows-repeated", start, output);
        }
    }

    private static void Repeat(XElement element, string attribute, int start, PreviewText output)
    {
        var value = (string?)element.Attribute(XName.Get(attribute, Table));
        if (value is null) return;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
            throw Invalid("Invalid repeated table range.");
        var text = output.Slice(start);
        // 빈 스프레드시트 끝의 백만 행을 만들지 않는다. 유효 내용 반복은 전체 문자 상한으로 제한한다.
        if (string.IsNullOrWhiteSpace(text)) return;
        for (var i = 1; i < count; i++) output.Add(text);
    }

    private static void OdfInline(XElement element, PreviewText output)
    {
        foreach (var node in element.Nodes())
        {
            output.Check();
            if (node is XText text) { output.Add(text.Value); continue; }
            if (node is not XElement child) continue;
            if (child.Name.NamespaceName != TextNs) continue;
            switch (child.Name.LocalName)
            {
                case "s":
                    var value = (string?)child.Attribute(XName.Get("c", TextNs));
                    if (value is not null && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1))
                        throw Invalid("Invalid whitespace count.");
                    var spaces = value is null ? 1 : int.Parse(value, CultureInfo.InvariantCulture);
                    output.Add(new string(' ', Math.Min(spaces, MaxTextCharacters + 1)));
                    break;
                case "tab": output.Add("\t"); break;
                case "line-break": output.Add("\n"); break;
                case "note": case "p": case "h": break;
                default: OdfInline(child, output); break;
            }
        }
    }

    private static void HwpInline(XElement element, PreviewText output)
    {
        foreach (var child in element.Elements())
        {
            output.Check();
            if (child.Name.NamespaceName != Hp) continue;
            switch (child.Name.LocalName)
            {
                // 표가 문장 중간에 있을 때도 앞 텍스트, 셀, 뒤 텍스트 순서로 방문한다.
                case "p":
                    HwpInline(child, output);
                    output.Add("\n");
                    break;
                case "tbl":
                    output.Add("\n");
                    HwpInline(child, output);
                    output.Add("\n");
                    break;
                case "tr":
                    HwpInline(child, output);
                    output.Add("\n");
                    break;
                case "tc":
                    HwpInline(child, output);
                    output.Add("\t");
                    break;
                case "pic": case "ole": case "equation": break;
                case "t":
                    foreach (var node in child.Nodes())
                    {
                        if (node is XText text) output.Add(text.Value);
                        else if (node is XElement control && control.Name.NamespaceName == Hp)
                        {
                            if (control.Name.LocalName == "tab") output.Add("\t");
                            else if (control.Name.LocalName == "lineBreak") output.Add("\n");
                        }
                    }
                    break;
                default: HwpInline(child, output); break;
            }
        }
    }

    private static void ReadHwp(byte[] bytes, PreviewText output, CancellationToken cancel)
    {
        var compound = new CompoundDocumentReader(bytes, cancel);
        var header = compound.Read("FileHeader");
        if (header.Length != 256 || !header.AsSpan(0, 17).SequenceEqual("HWP Document File"u8) || header[35] != 5)
            throw Invalid("Only HWP 5.x documents are supported.");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36, 4));
        if ((flags & ((1u << 1) | (1u << 2) | (1u << 4) | (1u << 8) | (1u << 10))) != 0)
            throw new NotSupportedException("Encrypted, DRM-protected and distribution-only HWP files are not supported. Export to PDF in Hancom Office.");
        var sections = compound.StreamNames.Where(name => name.StartsWith("BodyText/Section", StringComparison.Ordinal))
            .Select(name => (Name: name, Index: SectionIndex(name))).OrderBy(item => item.Index).ToArray();
        if (sections.Length is 0 or > 128 || sections.Where((item, index) => item.Index != index).Any())
            throw Invalid("Missing or invalid HWP body sections.");
        long total = 0;
        foreach (var section in sections)
        {
            cancel.ThrowIfCancellationRequested();
            var data = compound.Read(section.Name);
            if ((flags & 1) != 0)
            {
                using var source = new MemoryStream(data, false);
                using var deflate = new DeflateStream(source, CompressionMode.Decompress);
                data = ReadBounded(deflate, MaxPartBytes, cancel);
            }
            total += data.Length;
            if (total > MaxFileBytes) throw Invalid("The expanded HWP body exceeds the preview limit.");
            for (var offset = 0; offset < data.Length;)
            {
                cancel.ThrowIfCancellationRequested();
                if (data.Length - offset < 4) throw Invalid("Truncated HWP record.");
                var record = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                offset += 4;
                var size = record >> 20;
                if (size == 0xfff)
                {
                    if (data.Length - offset < 4) throw Invalid("Truncated HWP record size.");
                    size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                    offset += 4;
                }
                if (size > data.Length - offset) throw Invalid("Truncated HWP record data.");
                if ((record & 0x3ff) == 67) ReadHwpText(data.AsSpan(offset, (int)size), output);
                offset += (int)size;
            }
            output.Add("\n");
        }
    }

    private static int SectionIndex(string path) => int.TryParse(path.AsSpan("BodyText/Section".Length), NumberStyles.None,
        CultureInfo.InvariantCulture, out var value) ? value : -1;

    private static void ReadHwpText(ReadOnlySpan<byte> bytes, PreviewText output)
    {
        if (bytes.Length % 2 != 0) throw Invalid("Invalid HWP text record.");
        var text = new StringBuilder();
        for (var i = 0; i < bytes.Length; i += 2)
        {
            output.Check();
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i, 2));
            if (c >= 32) text.Append(c);
            else if (c is (char)10 or (char)13) text.Append('\n');
            else if (c is (char)30 or (char)31) text.Append(' ');
            else if (c == 24) text.Append('-');
            else if (c is >= (char)1 and <= (char)23)
            {
                if (i + 16 > bytes.Length) throw Invalid("Truncated HWP control character.");
                if (c == 9) text.Append('\t');
                i += 14;
            }
            if (text.Length >= 4096) { output.Add(text.ToString()); text.Clear(); }
        }
        output.Add(text.ToString());
        output.Add("\n");
    }

    private static byte[] ReadBounded(Stream stream, int limit, CancellationToken cancel)
    {
        if (stream.CanSeek && stream.Length - stream.Position > limit) throw Invalid("The document exceeds the preview size limit.");
        using var result = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, Math.Min(buffer.Length, limit - (int)result.Length + 1))) != 0)
        {
            cancel.ThrowIfCancellationRequested();
            if (result.Length + read > limit) throw Invalid("The expanded document part exceeds the preview size limit.");
            result.Write(buffer, 0, read);
        }
        cancel.ThrowIfCancellationRequested();
        return result.ToArray();
    }

    private static InvalidDataException Invalid(string message) => new(message);
    private sealed class PreviewLimitException : Exception;
    private sealed class PreviewText(CancellationToken cancel)
    {
        private readonly StringBuilder _text = new();
        public bool Truncated { get; set; }
        public int Length => _text.Length;
        public void Check() => cancel.ThrowIfCancellationRequested();
        public void Add(string value)
        {
            Check();
            var remaining = MaxTextCharacters - _text.Length;
            var take = Math.Min(remaining, value.Length);
            if (take > 0 && take < value.Length && char.IsHighSurrogate(value[take - 1])) take--;
            _text.Append(value.AsSpan(0, take));
            if (value.Length > remaining) throw new PreviewLimitException();
        }
        public string Slice(int start) => _text.ToString(start, _text.Length - start);
        public override string ToString() => _text.ToString();
    }
}
