using System.Globalization;

namespace KOTU.Core.Routing;

/// <summary>Selection-only incremental search. Time is supplied by a monotonic UI clock.</summary>
public sealed class ExplorerTypeAhead
{
    public const long TimeoutMilliseconds = 1000;
    private string _prefix = string.Empty;
    private long _lastInput;
    private char? _highSurrogate;
    public string Prefix => _prefix;

    public void Reset()
    {
        _prefix = string.Empty;
        _highSurrogate = null;
    }

    public int Find(string text, long now, IReadOnlyList<ExplorerListing.Entry> entries, int selected)
    {
        if (now - _lastInput >= TimeoutMilliseconds || now < _lastInput) Reset();
        if (text.Length == 0) return -1;
        // CharacterReceived carries UTF-16 code units. A lone surrogate must never match a filename.
        var valid = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsHighSurrogate(ch)) { _highSurrogate = ch; continue; }
            if (char.IsLowSurrogate(ch))
            {
                if (_highSurrogate is { } high) valid.Append(high).Append(ch);
                _highSurrogate = null;
                continue;
            }
            _highSurrogate = null;
            if (!char.IsControl(ch)) valid.Append(ch);
        }
        _lastInput = now;
        if (valid.Length == 0) return -1;
        _prefix += valid.ToString();
        var elements = StringInfo.GetTextElementEnumerator(_prefix);
        elements.MoveNext();
        var first = elements.GetTextElement();
        var repeated = true;
        while (elements.MoveNext())
            if (!string.Equals(first, elements.GetTextElement(), StringComparison.OrdinalIgnoreCase)) repeated = false;
        var prefix = repeated ? first : _prefix;
        var start = repeated && selected >= 0 && selected < entries.Count ? selected + 1 : 0;
        for (var offset = 0; offset < entries.Count; offset++)
        {
            var index = (start + offset) % entries.Count;
            if (entries[index].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1; // Explicit no-match, never an implicit selection of item zero.
    }
}
