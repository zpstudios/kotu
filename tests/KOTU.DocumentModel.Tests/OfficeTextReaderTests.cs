using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class OfficeTextReaderTests
{
    private const string OdfNamespaces = "xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0' xmlns:table='urn:oasis:names:tc:opendocument:xmlns:table:1.0' xmlns:draw='urn:oasis:names:tc:opendocument:xmlns:drawing:1.0'";
    private const string Manifest = "<manifest:manifest xmlns:manifest='urn:oasis:names:tc:opendocument:xmlns:manifest:1.0'/>";

    [Fact]
    public void OdtPreservesUnicodeWhitespaceAndCellTextWithoutScriptsOrDeletedChanges()
    {
        using var file = Odf("text", "<text:h>Heading</text:h><text:p>한글<text:s text:c='3'/>A<text:tab/>B<text:line-break/>C<text:span>bold</text:span><text:a xmlns:xlink='http://www.w3.org/1999/xlink' xlink:href='https://example.invalid'>label</text:a></text:p><office:scripts>secret</office:scripts><text:tracked-changes><text:p>deleted</text:p></text:tracked-changes><table:table table:name='Data'><table:table-row><table:table-cell><text:p>cell</text:p></table:table-cell></table:table-row></table:table>");
        var result = OfficeTextReader.Read(file, ".odt");
        Assert.Contains("한글   A\tB\nCboldlabel", result.Text);
        Assert.Contains("[Data]", result.Text);
        Assert.Contains("cell", result.Text);
        Assert.DoesNotContain("secret", result.Text);
        Assert.DoesNotContain("deleted", result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void OdsReadsCachedValuesAndBoundedRepeatedRowsWithoutEvaluatingFormulas()
    {
        using var file = Odf("spreadsheet", "<table:table table:name='Sheet'><table:table-row table:number-rows-repeated='2'><table:table-cell office:value='42' table:formula='unsafe()'/><table:table-cell table:number-columns-repeated='2'><text:p>cell</text:p></table:table-cell></table:table-row><table:table-row table:number-rows-repeated='1048576'><table:table-cell table:number-columns-repeated='16384'/></table:table-row></table:table>");
        var result = OfficeTextReader.Read(file, ".ods");
        Assert.Equal(2, result.Text.Split("42").Length - 1);
        Assert.Equal(4, result.Text.Split("cell").Length - 1);
        Assert.DoesNotContain("unsafe", result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void OdpShowsSlideNamesAndTextButNeverExternalObjects()
    {
        using var file = Odf("presentation", "<draw:page draw:name='Title'><draw:frame><draw:text-box><text:p>Slide text</text:p></draw:text-box><draw:object xmlns:xlink='http://www.w3.org/1999/xlink' xlink:href='file:///private'/></draw:frame></draw:page>");
        Assert.Contains("[Slide: Title]\nSlide text", OfficeTextReader.Read(file, ".odp").Text);
    }

    [Fact]
    public void HwpxUsesSpineOrderAndReadsNestedTableParagraphsOnce()
    {
        using var file = Hwpx();
        var result = OfficeTextReader.Read(file, ".hwpx");
        Assert.True(result.Text.IndexOf("Second", StringComparison.Ordinal) < result.Text.IndexOf("First", StringComparison.Ordinal));
        Assert.Equal(1, result.Text.Split("Cell").Length - 1);
        Assert.Contains("한글\ttext", result.Text);
        Assert.True(result.Text.IndexOf("Cell", StringComparison.Ordinal) < result.Text.IndexOf("After table", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hwp5ReadsMiniStreamsAndCompressedOrPlainBody(bool compressed)
    {
        using var file = new MemoryStream(Hwp("한글 A\tB\nC", compressed));
        var result = OfficeTextReader.Read(file, ".hwp");
        Assert.Contains("한글 A\tB\nC", result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Hwp5ReadsRegularSectorsAndExtendedRecordSize()
    {
        using var file = new MemoryStream(Hwp(new string('X', 3000), false));
        Assert.Equal(3000, OfficeTextReader.Read(file, ".hwp").Text.Count(c => c == 'X'));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(16u)]
    [InlineData(256u)]
    [InlineData(1024u)]
    public void HwpProtectedFormatsAreRejected(uint flags)
    {
        using var file = new MemoryStream(Hwp("private", false, flags));
        Assert.Throws<NotSupportedException>(() => OfficeTextReader.Read(file, ".hwp"));
    }

    [Fact]
    public void CompoundDirectoryCyclesAreRejected()
    {
        var bytes = Hwp("body", false);
        // Directory begins at sector 1; FileHeader's right sibling points to itself.
        Put32(bytes, 1024 + 128 + 72, 1);
        using var file = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(file, ".hwp"));
    }

    [Fact]
    public void CompoundFatCyclesAreRejected()
    {
        var bytes = Hwp("body", false);
        Put32(bytes, 512 + 4, 1);
        using var file = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(file, ".hwp"));
    }

    [Fact]
    public void CompoundVersion4SectorSizingReadsMiniStreams()
    {
        var original = Hwp("Version 4 한글", false);
        var bytes = new byte[original.Length / 512 * 4096];
        for (var sector = 0; sector < original.Length / 512; sector++)
            original.AsSpan(sector * 512, 512).CopyTo(bytes.AsSpan(sector * 4096));
        Put16(bytes, 26, 4);
        Put16(bytes, 30, 12);
        Put32(bytes, 40, 1);
        for (var i = 128; i < 1024; i++) Put32(bytes, 4096 + i * 4, 0xffffffff);
        using var file = new MemoryStream(bytes);
        Assert.Equal("Version 4 한글", OfficeTextReader.Read(file, ".hwp").Text);
    }

    [Theory]
    [InlineData(0xfffffffd)]
    [InlineData(1000000)]
    public void CompoundMiniSectorBoundsAreRejected(uint start)
    {
        var bytes = Hwp("body", false);
        Put32(bytes, 1024 + 128 + 116, start);
        using var file = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(file, ".hwp"));
    }

    [Fact]
    public void CompoundMiniFatCyclesAndOverstatedSizesAreRejected()
    {
        var cyclic = Hwp("body", false);
        Put32(cyclic, 1536 + 3 * 4, 0);
        using var cyclicFile = new MemoryStream(cyclic);
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(cyclicFile, ".hwp"));
        var oversized = Hwp("body", false);
        Put32(oversized, 1024 + 128 + 120, uint.MaxValue);
        using var oversizedFile = new MemoryStream(oversized);
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(oversizedFile, ".hwp"));
    }

    [Fact]
    public void HwpExtendedRecordSizeCannotOverflowOrReadPastSection()
    {
        var bytes = Hwp("body", false);
        // Mini stream at sector 3: FileHeader occupies the first four mini sectors.
        Put32(bytes, 2048 + 256, 67u | (0xfffu << 20));
        Put32(bytes, 2048 + 260, uint.MaxValue);
        using var file = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(file, ".hwp"));
    }

    [Fact]
    public void HwpTruncatedControlAndRecordAreRejected()
    {
        using var file = new MemoryStream(Hwp("\u0002", false));
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(file, ".hwp"));
        var bytes = Hwp("body", false);
        Array.Resize(ref bytes, bytes.Length - 1);
        using var truncated = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(truncated, ".hwp"));
    }

    [Fact]
    public void UnsupportedHwp3DoesNotPretendToBeReadable()
    {
        using var file = new MemoryStream(Encoding.ASCII.GetBytes("HWP Document File V3.00"));
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(file, ".hwp"));
    }

    [Fact]
    public void ZipTraversalDuplicatePathsAndOversizedPartsAreRejected()
    {
        using var traversal = Package(("../content.xml", "data"));
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(traversal, ".odt"));
        using var duplicate = Package(("content.xml", "a"), ("content.xml", "b"));
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(duplicate, ".odt"));
        using var oversized = Odf("text", "<text:p>" + new string('a', 8 * 1024 * 1024) + "</text:p>");
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(oversized, ".odt"));
    }

    [Fact]
    public void DtdAndDeepXmlAreRejected()
    {
        using var dtd = Package(("mimetype", "application/vnd.oasis.opendocument.text"), ("META-INF/manifest.xml", Manifest),
            ("content.xml", "<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///private'>]><x>&a;</x>"));
        Assert.Throws<System.Xml.XmlException>(() => OfficeTextReader.Read(dtd, ".odt"));
        using var deep = Odf("text", string.Concat(Enumerable.Repeat("<text:span>", 70)) + "x" + string.Concat(Enumerable.Repeat("</text:span>", 70)));
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(deep, ".odt"));
    }

    [Fact]
    public void EncryptionAndTypeMismatchAreRejected()
    {
        using var encrypted = Package(("mimetype", "application/vnd.oasis.opendocument.text"),
            ("META-INF/manifest.xml", "<manifest:manifest xmlns:manifest='urn:oasis:names:tc:opendocument:xmlns:manifest:1.0'><manifest:encryption-data/></manifest:manifest>"));
        Assert.Throws<NotSupportedException>(() => OfficeTextReader.Read(encrypted, ".odt"));
        using var mismatch = Odf("text", "<text:p>text</text:p>");
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(mismatch, ".ods"));
    }

    [Fact]
    public void HugeRepeatedRangesAreCappedAndReported()
    {
        using var file = Odf("spreadsheet", "<table:table><table:table-row table:number-rows-repeated='2147483647'><table:table-cell><text:p>x</text:p></table:table-cell></table:table-row></table:table>");
        var result = OfficeTextReader.Read(file, ".ods");
        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= OfficeTextReader.MaxTextCharacters);
    }

    [Fact]
    public void CancellationIsObservedAndSourceStreamRemainsOpen()
    {
        using var file = Odf("text", "<text:p>text</text:p>");
        Assert.Throws<OperationCanceledException>(() => OfficeTextReader.Read(file, ".odt", new CancellationToken(true)));
        Assert.True(file.CanRead);
        var result = OfficeTextReader.Read(file, ".odt");
        Assert.Equal("text", result.Text);
        Assert.True(file.CanRead);
    }

    [Fact]
    public void CancellationDuringInputReadingIsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new CancelOnReadStream(cancellation);
        Assert.Throws<OperationCanceledException>(() => OfficeTextReader.Read(source, ".hwp", cancellation.Token));
    }

    [Fact]
    public void CompressedHwpExpansionIsBounded()
    {
        using var file = new MemoryStream(Hwp(new string('X', 5 * 1024 * 1024), true));
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(file, ".hwp"));
    }

    [Fact]
    public void HwpxExternalSpineReferenceIsRejected()
    {
        using var file = Package(("mimetype", "application/hwp+zip"),
            ("Contents/content.hpf", "<opf:package xmlns:opf='http://www.idpf.org/2007/opf/'><opf:manifest><opf:item id='s' href='https://example.invalid/section.xml'/></opf:manifest><opf:spine><opf:itemref idref='s'/></opf:spine></opf:package>"));
        Assert.Throws<InvalidDataException>(() => OfficeTextReader.Read(file, ".hwpx"));
    }

    private sealed class CancelOnReadStream(CancellationTokenSource cancellation) : MemoryStream(new byte[256])
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return read;
        }
    }

    [Fact]
    public void SupportedExtensionsArePreciselyScoped()
    {
        Assert.True(OfficeTextReader.Supports("EXAMPLE.HWPX"));
        Assert.True(OfficeTextReader.Supports("example.odt"));
        Assert.False(OfficeTextReader.Supports("example.docx"));
        Assert.False(OfficeTextReader.Supports("example.pdf"));
        Assert.False(OfficeTextReader.Supports("example.txt"));
    }

    // These are complete, original package/compound fixtures assembled from the public format
    // specifications. No external office installation, private document or downloaded code is used.
    private static MemoryStream Odf(string kind, string body) => Package(
        ("mimetype", "application/vnd.oasis.opendocument." + kind), ("META-INF/manifest.xml", Manifest),
        ("content.xml", $"<office:document-content {OdfNamespaces}><office:body><office:{kind}>{body}</office:{kind}></office:body></office:document-content>"));

    private static MemoryStream Hwpx() => Package(("mimetype", "application/hwp+zip"),
        ("Contents/content.hpf", "<opf:package xmlns:opf='http://www.idpf.org/2007/opf/'><opf:manifest><opf:item id='header' href='Contents/header.xml'/><opf:item id='s1' href='section1.xml'/><opf:item id='s0' href='section0.xml'/></opf:manifest><opf:spine><opf:itemref idref='header'/><opf:itemref idref='s1'/><opf:itemref idref='s0'/></opf:spine></opf:package>"),
        ("Contents/header.xml", "<hh:head xmlns:hh='http://www.hancom.co.kr/hwpml/2011/head'/>"),
        ("Contents/section1.xml", Section("<hp:p><hp:run><hp:t>Second</hp:t></hp:run></hp:p>")),
        ("Contents/section0.xml", Section("<hp:p><hp:run><hp:t>First 한글<hp:tab/>text</hp:t><hp:tbl><hp:tr><hp:tc><hp:subList><hp:p><hp:run><hp:t>Cell</hp:t></hp:run></hp:p></hp:subList></hp:tc></hp:tr></hp:tbl><hp:t>After table</hp:t></hp:run></hp:p>")));

    private static string Section(string body) => "<hs:sec xmlns:hs='http://www.hancom.co.kr/hwpml/2011/section' xmlns:hp='http://www.hancom.co.kr/hwpml/2011/paragraph'>" + body + "</hs:sec>";

    private static MemoryStream Package(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        stream.Position = 0;
        return stream;
    }

    private static byte[] Hwp(string text, bool compressed, uint flags = 0)
    {
        var header = new byte[256];
        "HWP Document File"u8.CopyTo(header);
        header[35] = 5;
        Put32(header, 36, flags | (compressed ? 1u : 0u));
        // HWP tabs occupy eight UTF-16 code units, including a trailing control character.
        var unicode = Encoding.Unicode.GetBytes(text.Replace("\t", "\t\0\0\0\0\0\0\t", StringComparison.Ordinal) + "\r");
        var body = new byte[unicode.Length + (unicode.Length >= 4095 ? 8 : 4)];
        Put32(body, 0, 67u | ((uint)Math.Min(unicode.Length, 4095) << 20));
        if (unicode.Length >= 4095) Put32(body, 4, (uint)unicode.Length);
        unicode.CopyTo(body, body.Length - unicode.Length);
        if (compressed)
        {
            using var target = new MemoryStream();
            using (var deflate = new DeflateStream(target, CompressionLevel.Optimal, true)) deflate.Write(body);
            body = target.ToArray();
        }
        return Compound(header, body);
    }

    private static byte[] Compound(byte[] header, byte[] body)
    {
        const uint end = 0xfffffffe;
        const uint free = 0xffffffff;
        var sectors = new List<byte[]> { new byte[512], new byte[512], new byte[512] };
        var fat = Enumerable.Repeat(free, 128).ToArray();
        fat[0] = 0xfffffffd; fat[1] = end; fat[2] = end;
        var miniFat = Enumerable.Repeat(free, 128).ToArray();
        var minis = new List<byte>();
        uint Store(byte[] data)
        {
            if (data.Length >= 4096) return Regular(data);
            var start = (uint)(minis.Count / 64);
            var count = (data.Length + 63) / 64;
            minis.AddRange(data);
            while (minis.Count % 64 != 0) minis.Add(0);
            for (var i = 0; i < count; i++) miniFat[start + i] = i == count - 1 ? end : start + (uint)i + 1;
            return start;
        }
        uint Regular(byte[] data)
        {
            var start = (uint)sectors.Count;
            for (var offset = 0; offset < data.Length; offset += 512)
            {
                var sector = new byte[512];
                data.AsSpan(offset, Math.Min(512, data.Length - offset)).CopyTo(sector);
                fat[sectors.Count] = offset + 512 >= data.Length ? end : (uint)sectors.Count + 1;
                sectors.Add(sector);
            }
            return start;
        }
        var headerStart = Store(header);
        var bodyStart = Store(body);
        var rootStart = Regular(minis.ToArray());
        for (var i = 0; i < 128; i++) { Put32(sectors[0], i * 4, fat[i]); Put32(sectors[2], i * 4, miniFat[i]); }
        DirectoryEntry(0, "Root Entry", 5, free, 1, rootStart, minis.Count);
        DirectoryEntry(1, "FileHeader", 2, 2, free, headerStart, header.Length);
        DirectoryEntry(2, "BodyText", 1, free, 3, end, 0);
        DirectoryEntry(3, "Section0", 2, free, free, bodyStart, body.Length);
        var result = new byte[(sectors.Count + 1) * 512];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(result, 0);
        Put16(result, 26, 3); Put16(result, 28, 0xfffe); Put16(result, 30, 9); Put16(result, 32, 6);
        Put32(result, 44, 1); Put32(result, 48, 1); Put32(result, 56, 4096); Put32(result, 60, 2); Put32(result, 64, 1); Put32(result, 68, end);
        for (var i = 0; i < 109; i++) Put32(result, 76 + i * 4, i == 0 ? 0u : free);
        for (var i = 0; i < sectors.Count; i++) sectors[i].CopyTo(result, (i + 1) * 512);
        return result;

        void DirectoryEntry(int id, string name, byte type, uint right, uint child, uint start, int length)
        {
            var dir = sectors[1]; var offset = id * 128;
            var nameBytes = Encoding.Unicode.GetBytes(name + "\0"); nameBytes.CopyTo(dir, offset);
            Put16(dir, offset + 64, (ushort)nameBytes.Length); dir[offset + 66] = type; dir[offset + 67] = 1;
            Put32(dir, offset + 68, free); Put32(dir, offset + 72, right); Put32(dir, offset + 76, child);
            Put32(dir, offset + 116, start); Put32(dir, offset + 120, (uint)length);
        }
    }
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
}
