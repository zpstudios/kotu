using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class DocumentSessionTests
{
    [Fact]
    public void EditsDuringSavePreventClose()
    {
        var session = new DocumentSession();
        session.Reset("original");
        var save = session.TryBeginSave()!;
        Assert.True(session.TryCommitSave(save, "snapshot", new DocumentStamp(default, 8)));
        Assert.False(session.CanClose("snapshot")); // 저장 흐름이 끝나기 전에는 닫지 않는다.
        session.EndSave(save);
        Assert.False(session.CanClose("snapshot plus newer input"));
        Assert.True(session.CanClose("snapshot")); // undo로 저장본과 같아지면 닫을 수 있다.
    }

    [Fact]
    public void SaveFailureKeepsOriginalBaselineAndAllowsRetry()
    {
        var session = new DocumentSession();
        session.Reset("original");
        var save = session.TryBeginSave()!;
        Assert.Null(session.TryBeginSave());
        session.EndSave(save); // 실패·취소는 Commit 없이 끝낸다.
        Assert.Equal("original", session.BaselineText);
        Assert.False(session.CanClose("changed"));
        Assert.NotNull(session.TryBeginSave());
    }

    [Fact]
    public void OldSaveCannotChangeReplacementDocumentOrEndItsSave()
    {
        var session = new DocumentSession();
        session.Reset("first document");
        var oldSave = session.TryBeginSave()!;
        session.Reset("replacement");
        var newSave = session.TryBeginSave()!;
        Assert.False(session.TryCommitSave(oldSave, "stale", new DocumentStamp(default, 99)));
        session.EndSave(oldSave);
        Assert.True(session.IsSaving);
        Assert.Equal("replacement", session.BaselineText);
        Assert.Equal(default(DocumentStamp), session.DiskStamp);
        session.EndSave(newSave);
    }

    [Fact]
    public void SaveOperationCannotBeUsedByAnotherWindow()
    {
        var first = new DocumentSession();
        var second = new DocumentSession();
        var save = first.TryBeginSave()!;
        second.TryBeginSave();
        Assert.False(second.TryCommitSave(save, "other window", default));
        second.EndSave(save);
        Assert.True(second.IsSaving);
    }
}
