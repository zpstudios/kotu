namespace KOTU.DocumentModel;

/// <summary>디스크 작업만 수행한다. 호출자는 UI 밖에서 실행하고 결과를 세션에 반영한다.</summary>
public sealed class DocumentFileStore
{
    public bool HasChanged(string path, DocumentStamp expected)
    {
        try
        {
            var info = new FileInfo(path);
            return !info.Exists || info.LastWriteTimeUtc != expected.WriteTimeUtc
                || info.Length != expected.Length;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// 같은 폴더의 임시 파일에 기록·검증한 후 교체한다. 교체 실패 시 원본 직접 덮어쓰기로
    /// 후퇴하지 않는다. 파일시스템·장치 자체의 전원 장애 내구성까지 보장하는 계약은 아니다.
    /// </summary>
    public DocumentWriteResult WriteAndVerify(string path, byte[] bytes)
    {
        var target = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".kotu-save-{Guid.NewGuid():N}.tmp");
        try
        {
            // 연결 파일을 교체하면 링크 자체가 소실된다. 별도 정책을 정하기 전 안전하게 거절한다.
            if (File.Exists(target))
            {
                var attributes = File.GetAttributes(target);
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("Cannot safely replace a read-only file or a file link. Save to another file.");
            }

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes))
                return new DocumentWriteResult(false, default);

            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target); // 새 파일 경합 시 덮어쓰지 않고 실패한다.

            var verified = File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes);
            var info = new FileInfo(target);
            return new DocumentWriteResult(verified, new DocumentStamp(info.LastWriteTimeUtc, info.Length));
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { /* 원래 실패를 보존한다. 임시 파일은 원본과 별개다. */ }
            catch (UnauthorizedAccessException) { /* 원래 실패를 보존한다. */ }
        }
    }
}

public readonly record struct DocumentWriteResult(bool Verified, DocumentStamp Stamp);
