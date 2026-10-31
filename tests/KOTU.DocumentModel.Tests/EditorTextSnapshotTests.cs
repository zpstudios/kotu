using Xunit;

namespace KOTU.DocumentModel.Tests;

// 네이티브 이벤트 순서를 단정하지 않고 가능한 순서를 모델로 검증한다.
// 무효화 위치는 포함된 제품 소스에서 읽고, 줄 색인과 검색 유효성 검사는 실제 모델을 쓴다.
public sealed class EditorTextSnapshotTests
{
    private static readonly string View = Source("DocumentViewSource");
    private static readonly string Search = Source("DocumentSearchSource");

    [Fact]
    public void NativeWiringKeepsSynchronousRevisionAndExplicitDocumentResets()
    {
        Assert.Contains("TextChanging=\"OnSearchEditorTextChanging\"", Source("DocumentXamlSource"));
        Assert.Contains("TextChanged=\"OnEditorTextChanged\"", Source("DocumentXamlSource"));
        var changing = Body(Search, "private void OnSearchEditorTextChanging(");
        Assert.Contains("_textRevision++;", changing);
        Assert.Contains("_textSnapshot = null;", changing);
        Assert.Contains("InvalidateTextSearch();", changing);
        Assert.Contains("if (_virtualEditor is not null) return;", changing);
        Assert.True(changing.IndexOf("if (_virtualEditor", StringComparison.Ordinal) < changing.IndexOf("_textRevision++", StringComparison.Ordinal));
        Assert.Contains("if (_textSnapshot is { } cached) return cached;", Body(View, "private string EditorText"));
        Assert.Contains("return _textSnapshot = EditorBox.Text;", Body(View, "private string EditorText"));
        Assert.Contains("_textSnapshot = null;", Body(View, "private void ApplyLoadedText("));
        Assert.Contains("_textSnapshot = null;", Body(View, "private void StartUntitled("));
        Assert.Contains("await ReplaceActiveEditorAsync(edit, version, expectedSelection);", Body(Search, "private async Task ReplaceSearchAsync("));
        var replacement = Body(Source("DocumentActiveEditorSource"), "private async Task ReplaceActiveEditorAsync(");
        Assert.Contains("EditorTextChangedCore();", replacement);
        Assert.Contains("EditorBox.SelectedText = edit.Text;", replacement);
        Assert.Contains("await pane.ReplaceAsync", replacement);
        var changed = Body(View, "private void EditorTextChangedCore()");
        Assert.Contains("QueueTextSearch();", changed);
        Assert.Contains("UpdateTextPosition();", changed);
        Assert.Contains("TrackEdit(_changePrevText, text);", changed);
        Assert.Contains("_truncated || _capped", changed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectionAndDecorationShareOneReadAndBuildAcrossChanged(bool selectionFirst)
    {
        var model = new Events();
        model.Changing(new string('a', 999_999) + "\r");
        if (selectionFirst) model.ReadPosition();
        model.Changed();
        var statusSnapshot = model.ReadPosition();
        var decorationSnapshot = model.ReadPosition();
        Assert.Same(statusSnapshot, decorationSnapshot);
        Assert.Equal(1, model.Reads);
        Assert.Equal(1, model.Builds);
        Assert.Equal(2, model.Lines.Count);
        Assert.Equal(1, model.Lines.GetLineIndex(1_000_000));
    }

    [Fact]
    public void DelayedChangedUsesLatestRevisionWithoutRereadingOrAcceptingOldSearch()
    {
        var model = new Events();
        model.Changing("old\rline");
        var stale = TextSearch.Find(model.Snapshot(), "line");
        model.Changing("new\rline\r");
        Assert.False(stale.CanApplyReplacement(model.Snapshot(), "line"));
        model.Changing("latest😀\rline\r\n끝");
        var current = model.ReadPosition();
        var reads = model.Reads;
        var builds = model.Builds;
        model.Changed();
        model.Changed();
        Assert.Same(current, model.ReadPosition());
        Assert.Equal("latest😀\rline\r\n끝", current);
        Assert.Equal(3, model.Lines.Count);
        Assert.Equal(reads, model.Reads);
        Assert.Equal(builds, model.Builds);
        Assert.False(stale.CanApplyReplacement(model.Snapshot(), "line"));
    }

    [Fact]
    public void ReplacementThenDirectAndAsyncChangedShareTheFreshSelectionSnapshot()
    {
        var model = new Events();
        model.Changing("한글 cat\r\ncat😀");
        var result = TextSearch.Find(model.Snapshot(), "cat");
        var replacement = TextSearch.Replace(result, "끝\r\n", 0, true)!;
        var before = model.ReadPosition();
        model.Changing(before.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Text));
        var selectionSnapshot = model.ReadPosition();
        model.Changed();
        model.Changed();
        Assert.Equal("한글 끝\r\n\r\n끝\r\n😀", selectionSnapshot);
        Assert.Same(selectionSnapshot, model.ReadPosition());
        Assert.Equal(2, model.Reads);
        Assert.Equal(2, model.Builds);
        Assert.Equal(4, model.Lines.Count);
    }

    [Fact]
    public void SameLengthUndoRedoAndCompositionRevisionsNeverKeepStaleText()
    {
        var model = new Events();
        string[] revisions = ["a\rb", "a\rc", "a\rb", "a\rc", "ㅎ\rc", "하\rc", "한\rc"];
        foreach (var text in revisions)
        {
            model.Changing(text);
            Assert.Equal(text, model.ReadPosition());
            model.Changed();
            Assert.Equal(text, model.ReadPosition());
            Assert.Equal(2, model.Lines.Count);
        }
        Assert.Equal(revisions.Length, model.Reads);
        Assert.Equal(revisions.Length, model.Builds);
    }

    [Fact]
    public void ResetWithoutNativeChangeDropsOldDocumentSnapshotAndSearch()
    {
        var model = new Events();
        model.Changing("same\rtext");
        var old = TextSearch.Find(model.Snapshot(), "text");
        var before = model.ReadPosition();
        model.Reset("same\rtext");
        Assert.NotSame(before, model.ReadPosition());
        Assert.False(old.CanApplyReplacement(model.Snapshot(), "text"));
        model.Reset("");
        model.Changed();
        Assert.Equal("", model.ReadPosition());
        Assert.Equal(1, model.Lines.Count);
        Assert.Equal(3, model.Reads);
        Assert.Equal(3, model.Builds);
    }

    private sealed class Events
    {
        private string _native = "";
        private string? _snapshot;
        private long _revision;
        private long _generation;
        public int Reads { get; private set; }
        public int Builds { get; private set; }
        public TextLineIndex Lines { get; } = new();

        public void Changing(string text)
        {
            _native = text;
            _revision++;
            InvalidateFrom(Body(Search, "private void OnSearchEditorTextChanging("));
        }

        public void Changed()
        {
            InvalidateFrom(Body(View, "private void EditorTextChangedCore()"));
            ReadPosition();
        }

        public void Reset(string text)
        {
            _native = text;
            _generation++;
            InvalidateFrom(Body(View, text.Length == 0 ? "private void StartUntitled(" : "private void ApplyLoadedText("));
        }

        public string ReadPosition()
        {
            if (_snapshot is null)
            {
                _snapshot = new string(_native.ToCharArray());
                Reads++;
            }
            if (Lines.Update(_snapshot)) Builds++;
            return _snapshot;
        }

        public TextSearchSnapshot Snapshot() => new(ReadPosition(), _generation, _revision, true, true);
        private void InvalidateFrom(string body)
        {
            if (body.Contains("_textSnapshot = null;", StringComparison.Ordinal)) _snapshot = null;
        }
    }

    private static string Source(string name)
    {
        using var stream = typeof(EditorTextSnapshotTests).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Body(string source, string signature)
    {
        var signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureStart >= 0, signature);
        var start = source.IndexOf('{', signatureStart);
        var depth = 1;
        var end = start;
        while (depth > 0 && ++end < source.Length)
        {
            if (source[end] == '{') depth++;
            if (source[end] == '}') depth--;
        }
        Assert.Equal(0, depth);
        return source[(start + 1)..end];
    }
}
