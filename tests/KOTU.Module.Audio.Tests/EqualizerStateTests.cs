using System.Text.Json;
using Xunit;

namespace KOTU.Module.Audio.Tests;

public class EqualizerStateTests
{
    private static EqualizerCatalog Catalog() => new([60, 170, 310],
        [new("Rock", new EqualizerSnapshot(7.5f, [5, -3, 4])), new("Flat", new EqualizerSnapshot(12, [0, 0, 0]))]);

    [Theory]
    [InlineData("", "")]
    [InlineData("Rock", "Rock")]
    [InlineData("Unavailable preset", "")]
    public void LegacySelectionRestoresOrFallsBackToOff(string saved, string expected)
    {
        var state = new EqualizerState(saved, "");
        state.SetCatalog(Catalog());
        Assert.Equal(expected, state.Selection);
    }

    [Fact]
    public void ChangingPresetBandPreservesEveryOtherGainAndPreamp()
    {
        var catalog = Catalog();
        var state = new EqualizerState("Rock", "");
        state.SetCatalog(catalog);
        state.SetBand(1, 2.3f);
        Assert.Equal(EqualizerState.CustomSelection, state.Selection);
        Assert.Equal(7.5f, state.Current!.Preamp);
        Assert.Equal(new float[] { 5, 2.3f, 4 }, state.Current.CopyGains());
        Assert.Equal(new float[] { 5, -3, 4 }, catalog.Find("Rock")!.CopyGains());
    }

    [Fact]
    public void OffAndPresetDoNotOverwriteCustomAndCustomRoundTrips()
    {
        var state = new EqualizerState("Rock", "");
        state.SetCatalog(Catalog());
        state.SetBand(0, -4.2f);
        var custom = state.Custom;
        state.Select("");
        Assert.Null(state.Current);
        state.Select("Flat");
        Assert.Equal(12, state.Current!.Preamp);
        Assert.Same(custom, state.Custom);
        var reloaded = new EqualizerState(state.Selection, state.SerializeCustom());
        reloaded.SetCatalog(Catalog());
        Assert.Equal("Flat", reloaded.Selection);
        reloaded.Select(EqualizerState.CustomSelection);
        Assert.Equal(7.5f, reloaded.Current!.Preamp);
        Assert.Equal(new float[] { -4.2f, -3, 4 }, reloaded.Current.CopyGains());
    }

    [Fact]
    public void CustomSelectionRestoresAcrossRestart()
    {
        var state = new EqualizerState("Rock", "");
        state.SetCatalog(Catalog());
        state.SetBand(2, 6.8f);
        var restored = new EqualizerState(state.Selection, state.SerializeCustom());
        Assert.True(restored.SetCatalog(Catalog()));
        Assert.Equal(EqualizerState.CustomSelection, restored.Selection);
        Assert.Equal(6.8f, restored.Current![2]);
    }

    [Fact]
    public void ResetUsesZeroPreampAndKeepsNativeFlatPresetUntouched()
    {
        var state = new EqualizerState("Flat", "");
        state.SetCatalog(Catalog());
        state.Reset();
        Assert.Equal(EqualizerState.CustomSelection, state.Selection);
        Assert.Equal(0, state.Current!.Preamp);
        Assert.Equal(new float[] { 0, 0, 0 }, state.Current.CopyGains());
        Assert.Equal(12, state.Catalog!.Find("Flat")!.Preamp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid json")]
    [InlineData("null")]
    [InlineData("{\"Version\":2,\"Preamp\":0,\"Gains\":[0,0,0]}")]
    [InlineData("{\"Version\":1,\"Preamp\":0,\"Gains\":[0,0]}")]
    [InlineData("{\"Version\":1,\"Preamp\":0,\"Gains\":null}")]
    [InlineData("{\"Version\":1,\"Preamp\":21,\"Gains\":[0,0,0]}")]
    [InlineData("{\"Version\":1,\"Preamp\":0,\"Gains\":[0,-21,0]}")]
    [InlineData("{\"Version\":1,\"Preamp\":0,\"Gains\":[0,1e1000,0]}")]
    [InlineData("{\"Version\":1,\"Preamp\":\"NaN\",\"Gains\":[0,0,0]}")]
    [InlineData("{\"Version\":1,\"Preamp\":0,\"Gains\":[0,\"Infinity\",0]}")]
    public void InvalidSavedCustomSafelySelectsOff(string json)
    {
        var state = new EqualizerState(EqualizerState.CustomSelection, json);
        Assert.False(state.SetCatalog(Catalog()));
        Assert.Equal("", state.Selection);
        Assert.Null(state.Custom);
        Assert.True(state.Select(EqualizerState.CustomSelection));
        Assert.Equal(new float[] { 0, 0, 0 }, state.Current!.CopyGains());
    }

    [Fact]
    public void SnapshotsDefendAgainstInputAndOutputMutation()
    {
        float[] input = [1, 2, 3];
        var snapshot = new EqualizerSnapshot(4, input);
        input[0] = 20;
        var output = snapshot.CopyGains();
        output[1] = -20;
        var next = snapshot.WithBand(2, 8);
        Assert.Equal(new float[] { 1, 2, 3 }, snapshot.CopyGains());
        Assert.Equal(new float[] { 1, 2, 8 }, next.CopyGains());
        Assert.Equal(4, next.Preamp);
    }

    [Fact]
    public void PendingAndSerializedSnapshotsCannotBeChangedByLaterInput()
    {
        var state = new EqualizerState("Rock", "");
        state.SetCatalog(Catalog());
        state.SetBand(0, 2);
        var pending = state.Current;
        var saved = state.SerializeCustom();
        state.SetBand(0, 3);
        Assert.Equal(2, pending![0]);
        Assert.Equal(2, JsonDocument.Parse(saved).RootElement.GetProperty("Gains")[0].GetSingle());
    }

    [Fact]
    public void MissingNativeMetadataDoesNotInventChoicesOrForgetSavedCustom()
    {
        const string json = "{\"Version\":1,\"Preamp\":4,\"Gains\":[1,2,3]}";
        var state = new EqualizerState(EqualizerState.CustomSelection, json);
        Assert.Null(state.Catalog);
        Assert.False(state.Select("Rock"));
        Assert.False(state.Select(EqualizerState.CustomSelection));
        state.Select("");
        Assert.Equal(json, state.SerializeCustom());
        state.SetCatalog(Catalog());
        Assert.Equal("", state.Selection);
        state.Select(EqualizerState.CustomSelection);
        Assert.Equal(4, state.Current!.Preamp);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Rock", false)]
    [InlineData("", true)]
    [InlineData("Rock", true)]
    public void OtherWindowSelectionPreservesLatestSavedCustom(string selection, bool hadCustom)
    {
        var settings = new SharedSettings();
        if (hadCustom)
        {
            var initial = settings.Open();
            initial.SetBand(0, 1);
            settings.Save(initial);
        }
        var first = settings.Open();
        var second = settings.Open();
        if (hadCustom)
        {
            second.Select(EqualizerState.CustomSelection);
            settings.Save(second);
        }

        first.SetBand(1, 9.4f);
        settings.Save(first);
        var latestCustom = settings.Values[EqualizerState.CustomKey];
        second.Select(selection);
        settings.Save(second);

        Assert.Equal(selection, settings.Values[EqualizerState.SelectionKey]);
        Assert.Equal(latestCustom, settings.Values[EqualizerState.CustomKey]);
        var reopened = settings.Open();
        reopened.Select(EqualizerState.CustomSelection);
        Assert.Equal(9.4f, reopened.Current![1]);
    }

    [Fact]
    public void ExplicitCustomSelectionSavesTheValuesThatWindowApplies()
    {
        var settings = new SharedSettings();
        var initial = settings.Open();
        initial.Select("Rock");
        initial.SetBand(0, 2);
        settings.Save(initial);
        var first = settings.Open();
        var second = settings.Open();
        first.SetBand(0, 8);
        settings.Save(first);

        second.Select("");
        second.Select(EqualizerState.CustomSelection);
        settings.Save(second);

        var reopened = settings.Open();
        Assert.Equal(EqualizerState.CustomSelection, reopened.Selection);
        Assert.Equal(second.Current!.Preamp, reopened.Current!.Preamp);
        Assert.Equal(second.Current.CopyGains(), reopened.Current.CopyGains());
        Assert.Equal(2, reopened.Current[0]);
    }

    [Fact]
    public void PendingCustomEditIsSavedEvenWhenImmediatelySwitchedOff()
    {
        var settings = new SharedSettings();
        var state = settings.Open();
        state.Select("Rock");
        state.SetBand(2, 6.8f);
        state.Select("");
        settings.Save(state);

        var reopened = settings.Open();
        Assert.Equal("", reopened.Selection);
        reopened.Select(EqualizerState.CustomSelection);
        Assert.Equal(7.5f, reopened.Current!.Preamp);
        Assert.Equal(new float[] { 5, -3, 6.8f }, reopened.Current.CopyGains());
    }

    [Fact]
    public void ResetAndFirstCustomSelectionPersistFlatValues()
    {
        var settings = new SharedSettings();
        var first = settings.Open();
        first.Select(EqualizerState.CustomSelection);
        settings.Save(first);
        Assert.Equal(new float[] { 0, 0, 0 }, settings.Open().Current!.CopyGains());

        var second = settings.Open();
        first.Select("Rock");
        first.SetBand(0, 9);
        settings.Save(first);
        second.Reset();
        settings.Save(second);
        var reopened = settings.Open();
        Assert.Equal(0, reopened.Current!.Preamp);
        Assert.Equal(new float[] { 0, 0, 0 }, reopened.Current.CopyGains());
    }

    private sealed class SharedSettings
    {
        internal Dictionary<string, string> Values { get; } = new()
        {
            [EqualizerState.SelectionKey] = string.Empty,
            [EqualizerState.CustomKey] = string.Empty,
        };

        internal EqualizerState Open()
        {
            var state = new EqualizerState(Values[EqualizerState.SelectionKey], Values[EqualizerState.CustomKey]);
            state.SetCatalog(Catalog());
            return state;
        }

        internal void Save(EqualizerState state) => state.WriteSettings((key, value) => Values[key] = value);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(20.1f)]
    [InlineData(-20.1f)]
    public void InvalidManualValuesAreIgnored(float value)
    {
        var state = new EqualizerState("Rock", "");
        state.SetCatalog(Catalog());
        state.SetBand(0, value);
        Assert.Equal("Rock", state.Selection);
        Assert.Equal(5, state.Current![0]);
    }
}
