using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace KOTU.FileOperations;

internal sealed record EntryStamp(uint Volume, ulong Id, long Length, long Modified, bool IsFolder);

// 경로 검사와 실제 원본 삭제를 분리한다. 원본 삭제는 복사에 사용한 동일 핸들로만 한다.
internal static class PhysicalTransferStore
{
    private const uint Read = 0x80000000, Delete = 0x10000, Attributes = 0x80;
    private const uint OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparse = 0x00200000;

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string NativePath(string path) => path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    internal static bool Equal(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    internal static bool Within(string candidate, string root) => Equal(candidate, root) ||
        Normalize(candidate).StartsWith(Path.TrimEndingDirectorySeparator(Normalize(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static bool HasAncestorIdentity(string candidate, EntryStamp source)
    {
        // 짧은 파일명이나 끝의 점 등 같은 폴더의 다른 표기는 문자열 prefix만으로 잡을 수 없다.
        for (string? current = Normalize(candidate); current is not null; current = Path.GetDirectoryName(current))
        {
            var stamp = Inspect(current);
            if (stamp is not null && stamp.Volume == source.Volume && stamp.Id == source.Id) return true;
        }
        return false;
    }

    internal sealed class Handles : IDisposable
    {
        internal readonly List<SafeFileHandle> Items = new();
        public void Dispose() { foreach (var handle in Items) handle.Dispose(); }
    }

    internal static Handles LockAncestors(string path)
    {
        var result = new Handles();
        try
        {
            var chain = new Stack<string>();
            for (var parent = Path.GetDirectoryName(Normalize(path)); parent is not null; parent = Path.GetDirectoryName(parent)) chain.Push(parent);
            foreach (var directory in chain)
            {
                var handle = Open(directory, Attributes, FileShare.Read | FileShare.Write);
                result.Items.Add(handle);
                if (!Stamp(handle).IsFolder) throw new IOException("A path ancestor is not a folder.");
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static EntryStamp? Inspect(string path)
    {
        using var handle = CreateFileW(NativePath(path), Attributes, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return null;
            Throw(error);
        }
        return Stamp(handle);
    }

    internal static void RequireIdentity(string path, EntryStamp expected)
    {
        var actual = Inspect(path);
        if (actual is null || actual.Volume != expected.Volume || actual.Id != expected.Id || actual.IsFolder != expected.IsFolder)
            throw new IOException("The item changed since the transfer was planned: " + path);
    }

    internal static void RequireUnchanged(string path, EntryStamp? expected)
    {
        if (Inspect(path) != expected) throw new IOException("The item changed while waiting: " + path);
    }

    internal static void CreateFolder(string path)
    {
        using var ancestors = LockAncestors(path);
        if (!CreateDirectoryW(NativePath(path), IntPtr.Zero)) Throw(Marshal.GetLastWin32Error());
    }

    internal static SafeFileHandle LockDirectory(string path, EntryStamp? expected = null)
    {
        var handle = Open(path, Attributes, FileShare.Read | FileShare.Write);
        try
        {
            var stamp = Stamp(handle);
            if (!stamp.IsFolder || (expected is not null && (stamp.Volume != expected.Volume || stamp.Id != expected.Id)))
                throw new IOException("The folder changed since planning: " + path);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void CopyFile(TransferNode source, string destination, bool move, EntryStamp? expectedDestination, CancellationToken cancellation)
    {
        using var sourceAncestors = LockAncestors(source.Path);
        using var destinationAncestors = LockAncestors(destination);
        using var handle = Open(source.Path, Read | (move ? Delete : 0), FileShare.Read);
        if (Stamp(handle) != source.Stamp) throw new IOException("The source changed since the transfer was planned: " + source.Path);
        if ((File.GetAttributes(source.Path) & FileAttributes.Encrypted) != 0)
            throw new IOException("Encrypted files require a native move; staged transfers are not supported.");
        using var namedSources = new NamedStreams(source.Path, allowDelete: move);
        RequireUnchanged(destination, expectedDestination);
        if (expectedDestination is not null && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("The destination is read-only.");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".kotu-transfer-" + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = expectedDestination is null ? null :
            Path.Combine(Path.GetDirectoryName(destination)!, ".kotu-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        var publishStarted = false;
        var completed = false;
        try
        {
            using (var input = new FileStream(handle, FileAccess.Read))
            {
                byte[] digest;
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[128 * 1024];
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, count);
                        hash.AppendData(buffer, 0, count);
                    }
                    output.Flush(true);
                    digest = hash.GetHashAndReset();
                    output.Position = 0;
                    if (!CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(output)))
                        throw new IOException("The copied file failed verification.");
                }
                foreach (var stream in namedSources.Items)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var namedHandle = CreateFileW(NativePath(temporary + stream.Entry.Name), 0xC0000000, FileShare.None,
                        IntPtr.Zero, 1, 0, IntPtr.Zero);
                    if (namedHandle.IsInvalid) Throw(Marshal.GetLastWin32Error());
                    using var output = new FileStream(namedHandle, FileAccess.ReadWrite);
                    stream.Digest = CopyAndVerify(stream.Input, output, cancellation);
                }
                namedSources.RequireSetUnchanged(source.Path);
                namedSources.RequireSetUnchanged(temporary);
                cancellation.ThrowIfCancellationRequested();
                if (Stamp(handle) != source.Stamp) throw new IOException("The source changed during copying.");
                File.SetCreationTimeUtc(temporary, File.GetCreationTimeUtc(source.Path));
                File.SetLastWriteTimeUtc(temporary, DateTime.FromFileTimeUtc(source.Stamp.Modified));
                File.SetAttributes(temporary, File.GetAttributes(source.Path));
                var staged = Inspect(temporary) ?? throw new IOException("The staged file disappeared.");
                RequireUnchanged(destination, expectedDestination);
                publishStarted = true;
                if (expectedDestination is null) File.Move(temporary, destination, false);
                else File.Replace(temporary, destination, backup);
                if (move)
                {
                    // 게시 직후 대상이 바뀌었다면 원본을 남긴다. 삭제 완료까지 대상의 쓰기와 삭제를 막는다.
                    using var publishedHandle = Open(destination, Read, FileShare.Read);
                    var published = Stamp(publishedHandle);
                    if (published.Volume != staged.Volume || published.Id != staged.Id || published.Length != staged.Length)
                        throw new IOException("The destination changed after the copied file was published.");
                    using var publishedStream = new FileStream(publishedHandle, FileAccess.Read);
                    if (!CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(publishedStream)))
                        throw new IOException("The published file failed verification; the source was retained.");
                    using var publishedNames = new NamedStreams(destination, allowDelete: false);
                    // ReplaceFile은 원본 대상에만 있던 스트림을 보존한다. 복사한 모든 스트림은 반드시 일치해야 한다.
                    foreach (var original in namedSources.Items)
                    {
                        var stream = publishedNames.Items.SingleOrDefault(item => item.Entry == original.Entry);
                        if (stream is null || original.Digest is null ||
                            !CryptographicOperations.FixedTimeEquals(original.Digest, SHA256.HashData(stream.Input)))
                            throw new IOException("A published alternate stream failed verification; the source was retained.");
                    }
                    namedSources.RequireSetUnchanged(source.Path);
                    publishedNames.RequireSetUnchanged(destination);
                    var disposition = new FileDisposition { DeleteFile = true };
                    if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) Throw(Marshal.GetLastWin32Error());
                    // 원본의 마지막 핸들을 대상 보호 핸들보다 먼저 닫아 실제 삭제 순서를 보장한다.
                    namedSources.Dispose();
                    input.Dispose();
                }
                completed = true;
            }
        }
        catch (Exception ex) when (publishStarted && ex is IOException or UnauthorizedAccessException)
        {
            // ReplaceFile의 부분 실패에서는 기존 대상이 백업 이름으로 남을 수 있다. 복구본을 삭제하지 않는다.
            throw new IOException($"{ex.Message} Recovery files, if present: {temporary}" +
                (backup is null ? "" : $"; {backup}"), ex.HResult);
        }
        finally
        {
            try
            {
                if ((!publishStarted || completed) && File.Exists(temporary))
                {
                    File.SetAttributes(temporary, FileAttributes.Normal);
                    File.Delete(temporary);
                }
                if (completed && backup is not null && File.Exists(backup))
                {
                    File.SetAttributes(backup, FileAttributes.Normal);
                    File.Delete(backup);
                }
            }
            // 정리 실패가 원래의 복사/게시 실패를 가리지 않게 한다. 원본은 이 정리와 무관하다.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void RejectDirectoryStreams(string path)
    {
        if (EnumerateNamedStreams(path).Length != 0)
            throw new IOException("Folders with alternate data streams are not supported for transfers.");
    }

    private static byte[] CopyAndVerify(FileStream input, FileStream output, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellation.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
        }
        output.Flush(true);
        var digest = hash.GetHashAndReset();
        output.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(output)))
            throw new IOException("A copied alternate stream failed verification.");
        return digest;
    }

    private sealed record StreamEntry(string Name, long Length);
    private sealed class NamedStream(StreamEntry entry, FileStream input)
    {
        internal StreamEntry Entry { get; } = entry;
        internal FileStream Input { get; } = input;
        internal byte[]? Digest { get; set; }
    }

    private sealed class NamedStreams : IDisposable
    {
        internal List<NamedStream> Items { get; } = new();
        internal NamedStreams(string path, bool allowDelete)
        {
            try
            {
                foreach (var entry in EnumerateNamedStreams(path))
                {
                    var handle = Open(path + entry.Name, Read, FileShare.Read | (allowDelete ? FileShare.Delete : 0));
                    FileStream input;
                    try { input = new FileStream(handle, FileAccess.Read); }
                    catch { handle.Dispose(); throw; }
                    Items.Add(new(entry, input));
                    if (input.Length != entry.Length) throw new IOException("An alternate stream changed while opening it.");
                }
                RequireSetUnchanged(path);
            }
            catch { Dispose(); throw; }
        }

        internal void RequireSetUnchanged(string path)
        {
            if (!Items.Select(item => item.Entry).SequenceEqual(EnumerateNamedStreams(path)))
                throw new IOException("The alternate data streams changed during the transfer: " + path);
        }
        public void Dispose() { foreach (var item in Items) item.Input.Dispose(); }
    }

    private static StreamEntry[] EnumerateNamedStreams(string path)
    {
        var result = new List<StreamEntry>();
        var find = FindFirstStreamW(NativePath(path), 0, out var data, 0);
        if (find == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return Array.Empty<StreamEntry>();
            Throw(error); // 열거를 지원하지 않으면 스트림 유실 위험을 감수하지 않는다.
        }
        try
        {
            do
            {
                if (data.Name != "::$DATA")
                {
                    if (!data.Name.StartsWith(':') || !data.Name.EndsWith(":$DATA", StringComparison.Ordinal) || data.Name.Contains('\\') || data.Name.Contains('/'))
                        throw new IOException("An unsupported alternate stream name was found.");
                    result.Add(new(data.Name, data.Size));
                }
            } while (FindNextStreamW(find, out data));
            var error = Marshal.GetLastWin32Error();
            if (error != 38) Throw(error);
        }
        finally { FindClose(find); }
        return result.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
    }

    internal static bool TryRename(TransferNode source, string destination, CancellationToken cancellation)
    {
        using var sourceAncestors = LockAncestors(source.Path);
        using var destinationAncestors = LockAncestors(destination);
        using var sourceHandle = Open(source.Path, Attributes | Delete, FileShare.Read);
        var current = Stamp(sourceHandle);
        if (current.Volume != source.Stamp.Volume || current.Id != source.Stamp.Id || current.IsFolder != source.IsFolder ||
            (!source.IsFolder && current != source.Stamp))
            throw new IOException("The source changed since the transfer was planned: " + source.Path);
        RequireUnchanged(destination, null);
        cancellation.ThrowIfCancellationRequested();
        // FILE_RENAME_INFO의 가변 UTF-16 이름은 바이트 수로 전달한다. RootDirectory는 NULL,
        // ReplaceIfExists는 FALSE이며 운영체제가 충돌 검사와 이름변경을 한 번에 수행한다.
        var name = System.Text.Encoding.Unicode.GetBytes(NativePath(destination));
        var nameOffset = checked((int)Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FirstCharacter)));
        var lengthOffset = checked((int)Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.NameLength)));
        var size = Math.Max(Marshal.SizeOf<FileRenameInformation>(), checked(nameOffset + name.Length + 2));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (SetFileInformationByHandle(sourceHandle, 3, buffer, checked((uint)size))) return true;
            var error = Marshal.GetLastWin32Error();
            if (error == 17) return false; // ERROR_NOT_SAME_DEVICE만 검증 복사/삭제로 전환한다.
            Throw(error);
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void RemoveEmptySource(TransferNode source)
    {
        using var ancestors = LockAncestors(source.Path);
        using var handle = Open(source.Path, Attributes | Delete, FileShare.Read | FileShare.Write);
        var current = Stamp(handle);
        if (current.Volume != source.Stamp.Volume || current.Id != source.Stamp.Id || !current.IsFolder)
            throw new IOException("The source folder changed since planning.");
        RejectDirectoryStreams(source.Path);
        // Windows는 비어 있지 않은 디렉터리의 disposition을 거부한다. 재귀 삭제는 하지 않는다.
        var disposition = new FileDisposition { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) Throw(Marshal.GetLastWin32Error());
    }

    private static SafeFileHandle Open(string path, uint access, FileShare share)
    {
        var handle = CreateFileW(NativePath(path), access, share, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); Throw(error); }
        return handle;
    }

    private static EntryStamp Stamp(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) Throw(Marshal.GetLastWin32Error());
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked files and folders are not supported for transfers.");
        return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            (long)(((ulong)info.SizeHigh << 32) | info.SizeLow), ((long)info.WriteHigh << 32) | info.WriteLow,
            (info.Attributes & (uint)FileAttributes.Directory) != 0);
    }

    private static void Throw(int error)
    {
        if (error == 5) throw new UnauthorizedAccessException(new Win32Exception(error).Message);
        throw new IOException(new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)));
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FindStreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileRenameInformation
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint NameLength;
        public ushort FirstCharacter;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstStreamW(string path, int level, out FindStreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextStreamW(IntPtr find, out FindStreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FindClose(IntPtr find);
}
