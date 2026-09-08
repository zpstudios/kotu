namespace KOTU.DocumentModel;

/// <summary>본문 스냅샷별 논리 줄 색인. UI 소유 스레드에서 장식과 커서 표시가 공유한다.</summary>
public sealed class TextLineIndex
{
    private string? _source;
    private int[] _starts = [0];

    public int Count => _starts.Length;
    public int GetStart(int lineIndex) => _starts[lineIndex];

    /// <summary>같은 스냅샷은 재사용한다. CRLF는 한 개행이며 끝 개행 뒤 빈 줄도 센다.</summary>
    public bool Update(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (ReferenceEquals(_source, text)) return false;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            starts.Add(i + 1);
        }
        _starts = [.. starts];
        _source = text;
        return true;
    }

    /// <summary>문자 위치가 속한 논리 줄(0부터). 스크롤·선택 변경에서는 이진 탐색만 한다.</summary>
    public int GetLineIndex(int characterIndex)
    {
        var index = Math.Clamp(characterIndex, 0, _source?.Length ?? 0);
        var position = Array.BinarySearch(_starts, index);
        return position >= 0 ? position : ~position - 1;
    }
}
