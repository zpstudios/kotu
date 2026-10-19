using System.Text;

namespace KOTU.DocumentModel;

public readonly record struct TextSearchSnapshot(string Text, long DocumentGeneration, long TextRevision,
    bool IsComplete, bool CanReplace);

/// <summary>Offsets always refer to the original UTF-16 snapshot, without newline normalization.</summary>
public sealed record TextSearchResult(TextSearchSnapshot Snapshot, string Query, IReadOnlyList<int> Starts)
{
    public bool IsCurrent(TextSearchSnapshot current, string query) =>
        Snapshot.DocumentGeneration == current.DocumentGeneration &&
        Snapshot.TextRevision == current.TextRevision && Snapshot.IsComplete == current.IsComplete &&
        string.Equals(Query, query, StringComparison.Ordinal);

    public bool CanApplyReplacement(TextSearchSnapshot current, string query) =>
        IsCurrent(current, query) && Snapshot.IsComplete && Snapshot.CanReplace && current.CanReplace;

    public int Navigate(int current, bool previous) => Starts.Count == 0 ? -1 :
        previous ? (current <= 0 ? Starts.Count - 1 : current - 1) : (current + 1) % Starts.Count;

    public int AtOrAfter(int position)
    {
        if (Starts.Count == 0) return -1;
        int low = 0, high = Starts.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Starts[middle] < position) low = middle + 1;
            else high = middle;
        }
        return low == Starts.Count ? 0 : low;
    }
}

public sealed record TextReplacement(int Start, int Length, string Text, int Count);

public static class TextSearch
{
    // Bound the result of multiplying a large pasted replacement by many matches.
    public const int MaxReplacementLength = 16 * 1024 * 1024;

    public static TextSearchResult Find(TextSearchSnapshot snapshot, string query, CancellationToken token = default)
    {
        var starts = new List<int>();
        if (query.Length > 0)
        {
            for (var position = 0; position <= snapshot.Text.Length - query.Length;)
            {
                token.ThrowIfCancellationRequested();
                var found = snapshot.Text.IndexOf(query, position, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                starts.Add(found);
                position = found + query.Length;
            }
        }
        token.ThrowIfCancellationRequested();
        return new(snapshot, query, starts.AsReadOnly());
    }

    /// <summary>One contiguous selection edit preserves the native editor undo transaction.</summary>
    public static TextReplacement? Replace(TextSearchResult result, string replacement, int current,
        bool all, CancellationToken token = default)
    {
        if (!result.Snapshot.IsComplete || !result.Snapshot.CanReplace || result.Starts.Count == 0) return null;
        var first = all ? 0 : current;
        if (first < 0 || first >= result.Starts.Count) return null;
        var last = all ? result.Starts.Count - 1 : first;
        var start = result.Starts[first];
        var end = result.Starts[last] + result.Query.Length;
        var count = last - first + 1;
        var length = (long)end - start + (long)count * (replacement.Length - result.Query.Length);
        if (length > MaxReplacementLength || result.Snapshot.Text.Length - (end - start) + length > MaxReplacementLength)
            throw new InvalidOperationException("The replacement would exceed the 16-million-character limit.");
        var builder = new StringBuilder((int)length);
        var position = start;
        for (var i = first; i <= last; i++)
        {
            token.ThrowIfCancellationRequested();
            var match = result.Starts[i];
            builder.Append(result.Snapshot.Text, position, match - position);
            builder.Append(replacement);
            position = match + result.Query.Length;
        }
        return new(start, end - start, builder.ToString(), count);
    }
}
