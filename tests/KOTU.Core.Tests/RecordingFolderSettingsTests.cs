using KOTU.Core.Settings;
using KOTU.Module.Record;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class RecordingFolderSettingsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "KOTU-folder-classification");
    private static string Known(Environment.SpecialFolder kind) => Path.Combine(Root, kind.ToString());
    private static string PathFor(string name) => Path.Combine(Root, name);
    private static string Old(Environment.SpecialFolder kind) => Path.Combine(Known(kind), "KOTU");

    public static IEnumerable<object?[]> LegacyCases()
    {
        var music = Old(Environment.SpecialFolder.MyMusic);
        var video = Old(Environment.SpecialFolder.MyVideos);
        var x = PathFor("custom-x");
        var y = PathFor("custom-y");
        yield return [null, null, ""];
        yield return ["", " \t", ""];
        yield return [music, video, ""];
        yield return [music + Path.DirectorySeparatorChar, video.ToUpperInvariant(), ""];
        yield return [x, null, ""];
        yield return [null, x, ""];
        yield return [x, y, ""];
        yield return [x, x, x];
        yield return [x, x.ToUpperInvariant() + Path.DirectorySeparatorChar, x];
        yield return [x, Path.Combine(x, "child", ".."), x];
        yield return [music, music, music];
        yield return [video, video, video];
        yield return [null, music, music];
        yield return [video, null, video];
        yield return [PathFor("PreviousRedirect/Music/KOTU"), PathFor("PreviousRedirect/Music/KOTU"), PathFor("PreviousRedirect/Music/KOTU")];
    }

    [Theory]
    [MemberData(nameof(LegacyCases))]
    public void MigrationUsesActualDestinationsWithBothDefaultsTakingPriority(string? audio, string? video, string expected)
    {
        var settings = new MemorySettingsService();
        settings.Set("record.audioFolder", audio);
        settings.Set("record.videoFolder", video);
        var result = RecordingFolderSettings.Resolve(settings, Known);
        Assert.True(result.NeedsSave);
        Assert.Equal(expected.Length == 0 ? "" : RecordingFolderSettings.Normalize(expected), result.Spec.ConfiguredPath);
        Assert.Equal(audio, settings.Get<string?>("record.audioFolder", null));
        Assert.Equal(video, settings.Get<string?>("record.videoFolder", null));
        Assert.Null(settings.Get<string?>(RecordingFolderSettings.Key, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("custom")]
    public void AuthoritativeNewKeyIgnoresInvalidLegacyAndKnownFolders(string value)
    {
        var settings = new MemorySettingsService();
        var path = value.Length == 0 ? "" : PathFor(value);
        settings.Set(RecordingFolderSettings.Key, path);
        settings.Set("record.audioFolder", "invalid-relative");
        settings.Set("record.videoFolder", "\0");
        var result = RecordingFolderSettings.Resolve(settings, _ => throw new IOException("must not query"));
        Assert.False(result.NeedsSave);
        Assert.Equal(path, result.Spec.ConfiguredPath);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(" ")]
    [InlineData("\0")]
    public void InvalidNewStringIsAnErrorNotLegacyFallback(string invalid)
    {
        var settings = new MemorySettingsService();
        settings.Set(RecordingFolderSettings.Key, invalid);
        Assert.Throws<IOException>(() => RecordingFolderSettings.Resolve(settings, Known));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("\0")]
    [InlineData("wild*card")]
    public void InvalidLegacyStringCannotSilentlyAdoptDocuments(string invalid)
    {
        var settings = new MemorySettingsService();
        settings.Set("record.audioFolder", invalid == "wild*card" ? PathFor(invalid) : invalid);
        Assert.Throws<IOException>(() => RecordingFolderSettings.Resolve(settings, Known));
    }

    [Fact]
    public void MissingNullAndWrongTypesHaveTheSameUnresolvedSemantics()
    {
        foreach (var kind in new[] { "missing", "null", "number", "object" })
        {
            var settings = new MemorySettingsService();
            if (kind == "null") settings.Set<string?>(RecordingFolderSettings.Key, null);
            if (kind == "number") settings.Set(RecordingFolderSettings.Key, 42);
            if (kind == "object") settings.Set(RecordingFolderSettings.Key, new { value = "x" });
            settings.Set("record.audioFolder", 42);
            settings.Set<string?>("record.videoFolder", null);
            var result = RecordingFolderSettings.Resolve(settings, _ => throw new IOException("not needed for two defaults"));
            Assert.True(result.NeedsSave);
            Assert.Equal("", result.Spec.ConfiguredPath);
        }
    }

    [Fact]
    public void UnavailableOldKnownFolderDoesNotInvalidateExplicitPathButCannotInventImplicitDestination()
    {
        var settings = new MemorySettingsService();
        var x = PathFor("x");
        settings.Set("record.audioFolder", x);
        settings.Set("record.videoFolder", x);
        Assert.Equal(x, RecordingFolderSettings.Resolve(settings, _ => "").Spec.ConfiguredPath);
        settings.Set<string?>("record.videoFolder", null);
        Assert.Throws<IOException>(() => RecordingFolderSettings.Resolve(settings, _ => ""));
        settings.Set<string?>("record.audioFolder", null);
        Assert.Equal("", RecordingFolderSettings.Resolve(settings, _ => "").Spec.ConfiguredPath);
    }

    [Fact]
    public void BothMatchingDefaultsStillBecomeDocumentsWhenOldLibrariesCoincide()
    {
        var settings = new MemorySettingsService();
        var library = PathFor("shared-library");
        settings.Set("record.audioFolder", Path.Combine(library, "KOTU"));
        settings.Set("record.videoFolder", Path.Combine(library, "KOTU"));
        Assert.Equal("", RecordingFolderSettings.Resolve(settings, _ => library).Spec.ConfiguredPath);
    }
}
