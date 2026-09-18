using KOTU.Core.Cli;
using KOTU.Core.Contracts;
using KOTU.Core.Jobs;
using Xunit;

namespace KOTU.Core.Tests;

public class ArchiveLaunchRequestTests
{
    [Theory]
    [InlineData("--compress", LaunchVerb.Compress)]
    [InlineData("--extract-here", LaunchVerb.ExtractHere)]
    [InlineData("--extract-to-folder", LaunchVerb.ExtractToFolder)]
    public void InitialAndRedirectedRequestsKeepEverySelection(string token, LaunchVerb verb)
    {
        string[] paths = [@"C:\한글 폴더\a.zip", @"D:\자료\b.7z"];
        var initial = LaunchRequest.Parse([token, .. paths]);
        var redirected = LaunchRequest.ParseCommandLine($"\"C:\\KOTU.exe\" {token} \"{paths[0]}\" \"{paths[1]}\"");
        Assert.Equal(verb, initial.Verb);
        Assert.Equal(token, initial.VerbToken);
        Assert.Equal(paths, initial.Paths);
        Assert.Equal(paths, redirected.Paths);
        Assert.Equal(paths[0], initial.FilePath);
    }

    [Fact]
    public void SelectionCannotBeChangedAfterRequestOrContextCreation()
    {
        string[] input = ["first", "second"];
        var request = LaunchRequest.FromPaths(LaunchVerb.Compress, input);
        var context = new OpenContext { InputPaths = input };
        input[0] = "changed";
        Assert.Equal(["first", "second"], request.Paths);
        Assert.Equal(["first", "second"], context.InputPaths);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)request.Paths)[0] = "changed");
        Assert.Equal("legacy", new LaunchRequest(LaunchVerb.Compress, "legacy").FilePath);
    }

    [Fact]
    public async Task BatchRegistrationIsCompleteBeforeNotificationsAndIdleGateRejectsWholeBatch()
    {
        var service = new BackgroundJobService();
        var firstNotificationCount = 0;
        service.Changed += () => Interlocked.CompareExchange(ref firstNotificationCount, service.GetSnapshots().Count, 0);
        var request = new BackgroundJobRequest(BackgroundJobKind.ExtractArchive, "Extract", null, null);
        var handles = service.StartMany([(request, _ => Task.CompletedTask), (request, _ => Task.CompletedTask)]);
        Assert.Equal(2, firstNotificationCount);
        await Task.WhenAll(handles.Select(handle => handle.Completion));
        using var lease = service.TryAcquireIdleLease();
        Assert.NotNull(lease);
        Assert.Throws<InvalidOperationException>(() => service.StartMany([(request, _ => Task.CompletedTask), (request, _ => Task.CompletedTask)]));
        Assert.Equal(2, service.GetSnapshots().Count);
    }

    [Fact]
    public async Task ResultLocationUpdatesSnapshotsButCannotChangeTerminalHistory()
    {
        var service = new BackgroundJobService();
        BackgroundJobContext? captured = null;
        var handle = service.Start(new(BackgroundJobKind.ExtractArchive, "Extract", "archive", null), context =>
        {
            captured = context;
            context.SetResultPath("actual/output");
            return Task.CompletedTask;
        });
        var result = await handle.Completion;
        Assert.Equal("actual/output", result.ResultPath);
        captured!.SetResultPath("late/path");
        Assert.Equal("actual/output", Assert.Single(service.GetSnapshots()).ResultPath);
    }
}
