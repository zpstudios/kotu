using System.Text;

namespace KOTU.DocumentModel;

public readonly record struct EditorDelta(int Offset, int Length, string Text);
public readonly record struct EditorSelection(int Anchor, int Position, int Line, int Column);
public sealed record EditorSnapshot(string Epoch, int Version, string Text, bool Frozen);

/// <summary>웹 편집기와 공유하는 UTF16/LF 모델. UI 소유 스레드에서만 접근한다.</summary>
public sealed class VirtualDocumentBuffer
{
    public const int MaxMessageChars = 8 * 1024 * 1024;
    public const int MaxChanges = 10000;
    private readonly StringBuilder _text;
    private string? _snapshot;

    public VirtualDocumentBuffer(string text, string? epoch = null)
    {
        Epoch = epoch ?? Guid.NewGuid().ToString("N");
        _snapshot = Normalize(text);
        _text = new StringBuilder(_snapshot);
        LineCount = 1 + _snapshot.Count(c => c == '\n');
    }

    public string Epoch { get; }
    public int Version { get; private set; } = 1;
    public int Length => _text.Length;
    public int LineCount { get; private set; }
    public EditorSelection Selection { get; private set; } = new(0, 0, 1, 1);
    public bool IsComposing { get; private set; }
    public string Text => _snapshot ??= _text.ToString();
    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>원본 좌표의 내림차순 델타를 전부 검사한 뒤 한 번에 반영한다.</summary>
    public bool Apply(string epoch, int version, IReadOnlyList<EditorDelta> changes)
    {
        if (epoch != Epoch) return false;
        if (version != Version + 1 || changes.Count == 0 || changes.Count > MaxChanges)
            throw new InvalidDataException("Unexpected editor revision or delta count.");
        long resultLength = Length;
        var previousOffset = Length + 1;
        var lines = LineCount;
        foreach (var change in changes)
        {
            if (change.Text is null || change.Offset < 0 || change.Length < 0 ||
                change.Offset > Length - change.Length || change.Offset >= previousOffset ||
                (long)change.Offset + change.Length > previousOffset || change.Text.Contains('\r'))
                throw new InvalidDataException("Invalid editor delta bounds, ordering or newline.");
            previousOffset = change.Offset;
            resultLength += change.Text.Length - (long)change.Length;
            for (var i = change.Offset; i < change.Offset + change.Length; i++)
                if (_text[i] == '\n') lines--;
            lines += change.Text.Count(c => c == '\n');
        }
        if (resultLength < 0 || resultLength > int.MaxValue - 16)
            throw new InvalidDataException("Editor text limit exceeded.");
        foreach (var change in changes) _text.Remove(change.Offset, change.Length).Insert(change.Offset, change.Text);
        Version = version;
        LineCount = lines;
        _snapshot = null;
        return true;
    }

    public bool SetState(string epoch, int version, EditorSelection selection, bool composing)
    {
        if (epoch != Epoch) return false;
        if (version != Version || selection.Anchor < 0 || selection.Anchor > Length ||
            selection.Position < 0 || selection.Position > Length || selection.Line < 1 ||
            selection.Line > LineCount || selection.Column < 1)
            throw new InvalidDataException("Invalid editor selection revision or bounds.");
        Selection = selection;
        IsComposing = composing;
        return true;
    }

    public EditorSnapshot VerifySnapshot(string epoch, int version, string text, bool frozen)
    {
        if (epoch != Epoch || version != Version || text != Text || text.Contains('\r'))
            throw new InvalidDataException("Editor snapshot disagrees with the document buffer. Keep this document open and retry.");
        return new(Epoch, Version, Text, frozen);
    }
}
