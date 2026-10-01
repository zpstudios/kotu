using System.Buffers.Binary;
using System.Text;

namespace KOTU.DocumentModel;

// A45: 읽기 전용 MS-CFB 부분 구현. 디스크 추출 없이 필요한 HWP 스트림만 읽는다.
internal sealed class CompoundDocumentReader
{
    private const uint End = 0xfffffffe;
    private const uint Free = 0xffffffff;
    private readonly byte[] _bytes;
    private readonly CancellationToken _cancel;
    private readonly int _sectorSize;
    private readonly int _sectorCount;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;
    private readonly Dictionary<string, Entry> _streams = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Entry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, long Size);

    public CompoundDocumentReader(byte[] bytes, CancellationToken cancel)
    {
        _bytes = bytes;
        _cancel = cancel;
        if (bytes.Length < 512 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
            throw Invalid("Only HWP 5.x compound documents are supported; HWP 3.x is not supported.");
        var major = U16(bytes, 26);
        var shift = U16(bytes, 30);
        if (U16(bytes, 28) != 0xfffe || !((major == 3 && shift == 9) || (major == 4 && shift == 12))
            || U16(bytes, 32) != 6 || U32(bytes, 56) != 4096)
            throw Invalid("Unsupported compound document header.");
        _sectorSize = 1 << shift;
        if (bytes.Length % _sectorSize != 0) throw Invalid("Truncated compound document.");
        _sectorCount = bytes.Length / _sectorSize - 1;
        var fatCount = U32(bytes, 44);
        if (fatCount == 0 || fatCount > (_sectorCount + _sectorSize / 4 - 1) / (_sectorSize / 4))
            throw Invalid("Invalid FAT size.");
        var fatIds = new List<uint>();
        for (var i = 0; i < 109; i++) AddFat(U32(bytes, 76 + i * 4));
        var difat = U32(bytes, 68);
        var difatCount = U32(bytes, 72);
        if (difatCount > _sectorCount) throw Invalid("Invalid DIFAT size.");
        var visited = new HashSet<uint>();
        for (var i = 0; i < difatCount; i++)
        {
            cancel.ThrowIfCancellationRequested();
            if (!visited.Add(difat)) throw Invalid("Cyclic DIFAT.");
            var offset = SectorOffset(difat);
            for (var j = 0; j < _sectorSize / 4 - 1; j++) AddFat(U32(bytes, offset + j * 4));
            difat = U32(bytes, offset + _sectorSize - 4);
        }
        if ((difat != End && difat != Free) || fatIds.Count != fatCount || fatIds.Distinct().Count() != fatIds.Count)
            throw Invalid("Invalid DIFAT chain.");
        _fat = new uint[checked(fatIds.Count * (_sectorSize / 4))];
        for (var i = 0; i < fatIds.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var offset = SectorOffset(fatIds[i]);
            for (var j = 0; j < _sectorSize / 4; j++) _fat[i * (_sectorSize / 4) + j] = U32(bytes, offset + j * 4);
        }
        var directory = ReadChain(U32(bytes, 48), null, false, 8 * 1024 * 1024);
        var entries = new List<Entry>();
        for (var offset = 0; offset < directory.Length; offset += 128)
        {
            cancel.ThrowIfCancellationRequested();
            var type = directory[offset + 66];
            var length = U16(directory, offset + 64);
            if (type == 0) { entries.Add(new Entry("", 0, Free, Free, Free, End, 0)); continue; }
            if (type is not (1 or 2 or 5) || length < 2 || length > 64 || length % 2 != 0 || U16(directory, offset + length - 2) != 0)
                throw Invalid("Invalid directory entry.");
            var name = new UnicodeEncoding(false, false, true).GetString(directory, offset, length - 2);
            if (name.Contains('/') || name.Contains('\\') || name.Contains('\0')) throw Invalid("Invalid stream name.");
            var size = major == 3 ? U32(directory, offset + 120) : checked((long)BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(offset + 120, 8)));
            entries.Add(new Entry(name, type, U32(directory, offset + 68), U32(directory, offset + 72),
                U32(directory, offset + 76), U32(directory, offset + 116), size));
        }
        if (entries.Count == 0 || entries[0].Type != 5) throw Invalid("Missing compound root.");
        var miniCount = U32(bytes, 64);
        if (miniCount > _sectorCount) throw Invalid("Invalid mini FAT size.");
        var miniBytes = ReadChain(U32(bytes, 60), (long)miniCount * _sectorSize, false, OfficeTextReader.MaxFileBytes);
        _miniFat = new uint[miniBytes.Length / 4];
        for (var i = 0; i < _miniFat.Length; i++) _miniFat[i] = U32(miniBytes, i * 4);
        _miniStream = ReadChain(entries[0].Start, entries[0].Size, false, OfficeTextReader.MaxFileBytes);
        var pending = new Stack<(uint Id, string Parent, int Depth)>();
        pending.Push((entries[0].Child, "", 0));
        visited.Clear();
        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (id, parent, depth) = pending.Pop();
            if (id == Free) continue;
            if (id == 0 || id >= entries.Count || !visited.Add(id) || depth > 64) throw Invalid("Invalid directory tree.");
            var entry = entries[(int)id];
            if (entry.Type is not (1 or 2)) throw Invalid("Invalid directory child.");
            pending.Push((entry.Left, parent, depth));
            pending.Push((entry.Right, parent, depth));
            var path = parent + entry.Name;
            if (!_streams.TryAdd(path, entry)) throw Invalid("Duplicate compound stream.");
            if (entry.Type == 1) pending.Push((entry.Child, path + "/", depth + 1));
        }
        return;

        void AddFat(uint id)
        {
            if (id == Free) return;
            if (id >= _sectorCount || fatIds.Count >= fatCount) throw Invalid("Invalid FAT sector.");
            fatIds.Add(id);
        }
    }

    public IEnumerable<string> StreamNames => _streams.Where(pair => pair.Value.Type == 2).Select(pair => pair.Key);

    public byte[] Read(string path)
    {
        if (!_streams.TryGetValue(path, out var entry) || entry.Type != 2) throw Invalid("Missing HWP stream: " + path);
        return ReadChain(entry.Start, entry.Size, entry.Size < 4096, OfficeTextReader.MaxPartBytes);
    }

    private byte[] ReadChain(uint first, long? size, bool mini, int limit)
    {
        if (size < 0 || size > limit) throw Invalid("Compound stream exceeds the preview limit.");
        var unit = mini ? 64 : _sectorSize;
        using var result = new MemoryStream();
        var seen = new HashSet<uint>();
        var id = first;
        while (id != End && !(size == 0 && id == Free))
        {
            _cancel.ThrowIfCancellationRequested();
            if (!seen.Add(id) || result.Length >= limit || (size is { } expected && result.Length >= expected))
                throw Invalid("Invalid or cyclic compound stream.");
            var table = mini ? _miniFat : _fat;
            if (id >= table.Length) throw Invalid("Invalid compound sector index.");
            var source = mini ? _miniStream : _bytes;
            var offset = mini ? checked((long)id * 64) : SectorOffset(id);
            if (offset < 0 || offset + unit > source.Length) throw Invalid("Truncated compound sector.");
            var take = (int)Math.Min(unit, (size ?? limit) - result.Length);
            result.Write(source, (int)offset, take);
            id = table[id];
        }
        if (size is { } required && result.Length != required) throw Invalid("Truncated compound stream.");
        return result.ToArray();
    }

    private int SectorOffset(uint id)
    {
        if (id >= _sectorCount) throw Invalid("Invalid compound sector.");
        return checked(((int)id + 1) * _sectorSize);
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static InvalidDataException Invalid(string message) => new(message);
}
