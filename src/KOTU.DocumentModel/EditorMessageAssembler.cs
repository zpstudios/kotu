using System.Text;

namespace KOTU.DocumentModel;

/// <summary>큰 붙여넣기/스냅샷은 제한된 프레임으로 전송한다. 문서 크기 상한을 새로 만들지 않는다.</summary>
public sealed class EditorMessageAssembler
{
    public const int FrameChars = 1024 * 1024;
    private readonly string _epoch;
    private StringBuilder? _pending;
    private long _transfer;
    private int _index;
    private int _total;
    public EditorMessageAssembler(string epoch) => _epoch = epoch;

    public string? Accept(string epoch, long transfer, int index, int total, string data)
    {
        if (epoch != _epoch) return null;
        if (total <= FrameChars || total > int.MaxValue - 16 || data.Length == 0 || data.Length > FrameChars)
            throw new InvalidDataException("Invalid editor frame size.");
        if (index == 0)
        {
            if (_pending is not null || transfer <= _transfer) throw new InvalidDataException("Overlapping editor transfer.");
            _pending = new StringBuilder();
            _transfer = transfer;
            _total = total;
            _index = 0;
        }
        if (_pending is null || transfer != _transfer || index != _index || total != _total || data.Length > total - _pending.Length)
            throw new InvalidDataException("Invalid editor frame sequence.");
        _pending.Append(data);
        _index++;
        if (_pending.Length != total) return null;
        var result = _pending.ToString();
        _pending = null;
        return result;
    }

    public void Clear() => _pending = null;
}
