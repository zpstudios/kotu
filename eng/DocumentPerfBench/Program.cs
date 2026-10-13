using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using KOTU.DocumentModel;
using KOTU.Module.Document;

// 합성 파일과 UI 비의존 기준선만 만든다. WinUI 레이아웃·스크롤 시간은 측정하지 않는다.
var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/a401/model-baseline");
Directory.CreateDirectory(output);
var fixtures = Path.Combine(output, "fixtures");
Directory.CreateDirectory(fixtures);
var cases = new List<(string Name, string Text)>();
foreach (var size in new[] { 65_536, 262_144, 1_048_575, 1_048_576, 1_048_577 })
{
    cases.Add(($"ascii-80-lf-{size}", Repeat("var value = 123; // synthetic text ".PadRight(79, 'x') + "\n", size)));
    cases.Add(($"single-line-{size}", new string('x', size)));
}
cases.Add(("many-lines-1048576", Repeat("x\n", 1_048_576)));
cases.Add(("crlf-1048576", Repeat(new string('x', 78) + "\r\n", 1_048_576)));
cases.Add(("unicode-1048576", Repeat("합성 문서 가나다 😀 e\u0301\t\n", 1_048_576)));
cases.Add(("above-read-limit-5242880", Repeat(new string('x', 79) + "\n", 5_242_880)));
cases.Add(("markdown-list-262144", Repeat("- **synthetic** item `code`\n", 262_144)));
var manifest = new List<object>();
var rows = new List<object>();
foreach (var (name, text) in cases)
{
    var extension = name.StartsWith("markdown", StringComparison.Ordinal) ? ".md" : ".txt";
    var path = Path.Combine(fixtures, name + extension);
    File.WriteAllText(path, text, new UTF8Encoding(false));
    var index = new TextLineIndex();
    index.Update(text);
    var maxLine = 0;
    var length = 0;
    foreach (var c in text)
    {
        if (c is '\r' or '\n') { maxLine = Math.Max(maxLine, length); length = 0; }
        else length++;
    }
    maxLine = Math.Max(maxLine, length);
    manifest.Add(new { name, characters = text.Length, utf8Bytes = new FileInfo(path).Length,
        logicalLines = index.Count, maxLineCharacters = maxLine });
    Measure(name, "TextLineIndex.Fresh", () => { var next = new TextLineIndex(); next.Update(text); return next.Count; });
    Measure(name, "TextLineIndex.Cached1000", () =>
    {
        var count = 0;
        for (var i = 0; i < 1000; i++)
        {
            index.Update(text);
            count += index.GetLineIndex((int)((long)i * text.Length / 1000));
        }
        return count;
    });
    if (extension == ".md")
        Measure(name, "MarkdownParser.Parse", () => MarkdownParser.Parse(text).Count);
}
File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
{
    capturedUtc = DateTime.UtcNow,
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    processorCount = Environment.ProcessorCount,
    warmups = 5, samples = 21,
    scope = "UI-independent model only; not TextBox marshalling, layout, dispatcher, rendering or scroll latency.",
    rows
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Synthetic fixtures and model-only results written. No input files or settings were read.");

void Measure(string name, string operation, Func<int> action)
{
    for (var i = 0; i < 5; i++) _ = action();
    var milliseconds = new double[21];
    var allocated = new long[21];
    var check = 0;
    for (var i = 0; i < milliseconds.Length; i++)
    {
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        check = action();
        milliseconds[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocated[i] = GC.GetAllocatedBytesForCurrentThread() - allocation;
    }
    Array.Sort(milliseconds);
    Array.Sort(allocated);
    var median = milliseconds[10];
    var p95 = milliseconds[19];
    rows.Add(new { name, operation, medianMs = median, p95Ms = p95,
        medianAllocatedBytes = allocated[10], check });
    Console.WriteLine(string.Join(",", name, operation, median.ToString("F4", CultureInfo.InvariantCulture),
        p95.ToString("F4", CultureInfo.InvariantCulture), allocated[10], check));
}

static string Repeat(string pattern, int length)
{
    var result = new StringBuilder(length);
    while (result.Length < length) result.Append(pattern, 0, Math.Min(pattern.Length, length - result.Length));
    if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result[^1] = 'x';
    return result.ToString();
}
