using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Settings;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Settings;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ga-settings-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void R8_6_SettingsRoundTripAsPlainUtf8Json()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { PauseMs = 900, Boldness = Boldness.Cautious, ExcludedApps = ["Foo"] });
        byte[] bytes = File.ReadAllBytes(store.Path);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
        Assert.Contains("\"Cautious\"", File.ReadAllText(store.Path));
        var s = store.Load();
        Assert.Equal(900, s.PauseMs);
        Assert.Equal(["Foo"], s.ExcludedApps!);
    }

    [Fact]
    public void R8_6_BrokenSettingsFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json");
        Assert.Equal(700, new SettingsStore(Path.Combine(_dir, "settings.json")).Load().PauseMs);
    }

    [Theory]
    [InlineData(100, 300)]
    [InlineData(700, 700)]
    [InlineData(5000, 2000)]
    public void R4_4_a_PauseLengthIsASettingWithinRange(int set, int expected) =>
        Assert.Equal(expected, new AppSettings { PauseMs = set }.ToEngineOptions(new EngineOptions()).PauseMs);

    [Fact]
    public void R9_3_b_BoldnessMapsToTheSimilarityThreshold()
    {
        var tuned = new EngineOptions();
        Assert.True(new AppSettings { Boldness = Boldness.Cautious }.ToEngineOptions(tuned).Rules.MaxNormalisedDistance < tuned.Rules.MaxNormalisedDistance);
        Assert.Equal(tuned.Rules.MaxNormalisedDistance, new AppSettings().ToEngineOptions(tuned).Rules.MaxNormalisedDistance);
        Assert.True(new AppSettings { Boldness = Boldness.Bold }.ToEngineOptions(tuned).Rules.MaxNormalisedDistance > tuned.Rules.MaxNormalisedDistance);
    }

    [Fact]
    public void R5_2_ModelIsASettingDefaultingToTheSpikeAWinner() =>
        Assert.Equal("gemma-4-E4B-it-Q5_K_M.gguf", new AppSettings().Model);
}
