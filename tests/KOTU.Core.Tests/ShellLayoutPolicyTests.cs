using KOTU.Core.Content;
using Xunit;

namespace KOTU.Core.Tests;

public class ShellLayoutPolicyTests
{
    [Fact]
    public void EmptyDefaultFullScreenHidesPanelsAndRestoresThem()
    {
        var policy = new ShellLayoutPolicy();
        Assert.True(policy.EffectiveListVisible && policy.EffectiveInfoVisible);
        policy.SetFullScreen(true);
        Assert.False(policy.EffectiveListVisible || policy.EffectiveInfoVisible);
        policy.SetFullScreen(false);
        Assert.True(policy.EffectiveListVisible && policy.EffectiveInfoVisible);
        Assert.False(policy.IsOverride);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstFullScreenSidebarToggleUsesVisiblePanels(bool list)
    {
        var policy = new ShellLayoutPolicy();
        policy.SetFullScreen(true);
        policy.ToggleSidebar(list);
        Assert.Equal(list, policy.EffectiveListVisible);
        Assert.Equal(!list, policy.EffectiveInfoVisible);
        Assert.True(policy.IsOverride);
        policy.SetFullScreen(false);
        Assert.Equal(list, policy.EffectiveListVisible);
        Assert.Equal(!list, policy.EffectiveInfoVisible);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CustomPanelsSurviveFullScreenRoundTrip(bool list, bool info)
    {
        var policy = new ShellLayoutPolicy();
        policy.Reset("a");
        if (list) policy.ToggleSidebar(true);
        if (info) policy.ToggleSidebar(false);
        policy.SetFullScreen(true);
        Assert.Equal(list, policy.EffectiveListVisible);
        Assert.Equal(info, policy.EffectiveInfoVisible);
        policy.SetFullScreen(false);
        Assert.Equal(list, policy.EffectiveListVisible);
        Assert.Equal(info, policy.EffectiveInfoVisible);
        Assert.Equal(list || info, policy.IsOverride);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("next", false)]
    public void SuccessfulNewContentResetsCustomFullScreen(string? path, bool untitled)
    {
        var policy = new ShellLayoutPolicy();
        policy.ToggleSidebar(true);
        policy.SetFullScreen(true);
        Assert.True(policy.ObserveOpened(path, untitled));
        Assert.True(policy.IsPanelless);
        Assert.False(policy.IsFullScreen || policy.IsOverride);
        Assert.True(policy.HasContent);
    }

    [Fact]
    public void DuplicateCompletionAndSavePreserveUserLayout()
    {
        var policy = new ShellLayoutPolicy();
        policy.Reset("a");
        policy.ToggleSidebar(true);
        policy.SetFullScreen(true);
        Assert.False(policy.ObserveOpened("A"));
        policy.SavedPath("saved");
        Assert.False(policy.ObserveOpened("saved"));
        Assert.True(policy.IsFullScreen && policy.IsOverride && policy.ListVisible);
        Assert.False(policy.InfoVisible);
    }

    [Fact]
    public void NewSessionWithSamePathAndEmptyModuleResetOverrides()
    {
        var policy = new ShellLayoutPolicy();
        policy.Reset("a");
        policy.ToggleSidebar(true);
        policy.SetFullScreen(true);
        policy.Reset("a");
        Assert.True(policy.IsPanelless);
        Assert.False(policy.IsFullScreen || policy.IsOverride);
        policy.Reset();
        Assert.True(policy.ListVisible && policy.InfoVisible);
        Assert.False(policy.HasContent || policy.IsFullScreen || policy.IsOverride);
    }

    [Fact]
    public void HidingBothOnEmptyModuleIsCanonicalPanelless()
    {
        var policy = new ShellLayoutPolicy();
        policy.ToggleSidebar(true);
        policy.ToggleSidebar(false);
        Assert.True(policy.IsPanelless);
        Assert.False(policy.IsOverride || policy.HasContent);
        policy.SetFullScreen(true);
        policy.SetFullScreen(false);
        Assert.True(policy.IsPanelless);
    }

    [Fact]
    public void FirstUntitledSavePreservesFullScreenAndPanels()
    {
        var policy = new ShellLayoutPolicy();
        policy.Reset(untitled: true);
        policy.ToggleSidebar(false);
        policy.SetFullScreen(true);
        policy.SavedPath("first.txt");
        Assert.True(policy.IsFullScreen && policy.InfoVisible && policy.IsOverride);
        Assert.False(policy.ListVisible);
        Assert.Equal("first.txt", policy.OpenedPath);
    }
}
