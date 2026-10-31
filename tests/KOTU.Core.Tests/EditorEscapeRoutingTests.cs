using KOTU.Core.Content;
using KOTU.Core.Contracts;
using Xunit;

namespace KOTU.Core.Tests;

public class EditorEscapeRoutingTests
{
    private sealed class Source : IEditorEscapeSource
    {
        public event Action? EditorEscapeRequested;
        public void Escape() => EditorEscapeRequested?.Invoke();
    }

    [Fact]
    public void OneBrowserEscapeCrossesCompositeAndSessionOnce()
    {
        var queue = new Queue<Action>();
        var source = new Source();
        var host = new ChildContentHost(queue.Enqueue);
        host.Attach(source, null);
        using var outer = new ContentContractSession(host, queue.Enqueue, () => true);
        var count = 0;
        outer.EditorEscapeRequested += () => count++;
        source.Escape();
        Assert.Equal(0, count);
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Equal(1, count);
    }

    [Fact]
    public void StaleBrowserEscapeCannotCloseReplacementContent()
    {
        var queue = new Queue<Action>();
        var source = new Source();
        var host = new ChildContentHost(queue.Enqueue);
        host.Attach(source, null);
        var count = 0;
        host.EditorEscapeRequested += () => count++;
        source.Escape();
        host.Detach(() => { });
        host.Attach(source, null);
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Equal(0, count);
        source.Escape();
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Equal(1, count);
    }
}
