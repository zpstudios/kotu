using System.Text;
using KOTU.Core.Integration;

namespace KOTU.Core.Diagnostics;

public enum ContentTransitionStage
{
    Begin, DetachBegin, ContractsDetached, BarRemoved, ViewRemoved, Detached,
    CreateBegin, Created, ContractsAttached, ViewAttached, BarAttached, Completed,
    OpenFailed, Unloaded, Failed
}

/// <summary>
/// 전환 중 프로세스 종료 조사용 작은 단계 기록. 파일명과 예외 메시지는 기록하지 않는다.
/// 주기 작업 없이 전환 단계에서만 flush하며 현행·회전본 각각 64 KiB로 제한한다.
/// 마지막 단계는 완료된 경계일 뿐, 종료 원인을 증명하지 않는다.
/// </summary>
public sealed class ContentTransitionJournal : IDisposable
{
    public const int MaxBytes = 64 * 1024;
    public static string LogPath => Path.Combine(Path.GetTempPath(), "KOTU", "content-transitions.log");
    public static ContentTransitionJournal Shared { get; } = new(LogPath);
    private static readonly string Version = typeof(ContentTransitionJournal).Assembly.GetName().Version?.ToString() ?? "?";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _enabled;
    private StreamWriter? _writer;
    private long _bytes;
    private bool _closed;

    public ContentTransitionJournal(string path, bool enabled = true)
    {
        _path = path;
        // 경로 주입·enabled 값으로 Standalone의 디스크 쓰기 금지를 우회할 수 없다.
        _enabled = enabled && !DistributionPolicy.IsStandalone;
    }

    public void Record(long view, long generation, string? module, ContentTransitionStage stage,
        Exception? error = null)
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                var kind = module switch
                {
                    "audio" or "video" or "image" or "document" or "archive" => module,
                    null => "none",
                    _ => "other"
                };
                var failure = error is null ? "" : $" error={error.GetType().Name} hr=0x{error.HResult:X8}";
                var line = $"{DateTime.UtcNow:O} version={Version} pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId} view={view} generation={generation} module={kind} stage={stage}{failure}";
                var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                Open();
                if (_bytes + bytes > MaxBytes)
                {
                    CloseWriter();
                    File.Move(_path, _path + ".1", overwrite: true);
                    Open();
                }
                _writer!.WriteLine(line);
                _writer.Flush();
                _bytes += bytes;
            }
            catch
            {
                // 진단 실패가 전환을 막으면 안 된다. 반복 I/O 실패도 이 인스턴스에서 중단한다.
                _closed = true;
                CloseWriter();
            }
        }
    }

    private void Open()
    {
        if (_writer is not null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _bytes = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); }
        catch { /* 진단 종료도 호출자에게 예외를 내지 않는다. */ }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            CloseWriter();
        }
    }
}
