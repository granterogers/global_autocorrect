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
    public void R9_3_b_D52_ParagraphSwitchesTurnOffWholeParagraphAndLookBack()
    {
        var tuned = new EngineOptions { FixWholeParagraph = true, RevisitAfterMs = 1500 };
        var standard = new AppSettings().ToEngineOptions(tuned);
        Assert.True(standard.FixWholeParagraph);
        Assert.Equal(1500, standard.RevisitAfterMs);

        Assert.False(new AppSettings { FixWholeParagraph = false }.ToEngineOptions(tuned).FixWholeParagraph);
        Assert.Equal(1500, new AppSettings { FixWholeParagraph = false }.ToEngineOptions(tuned).RevisitAfterMs);
        Assert.Equal(0, new AppSettings { LookBackWhenPaused = false }.ToEngineOptions(tuned).RevisitAfterMs);
        Assert.True(new AppSettings { LookBackWhenPaused = false }.ToEngineOptions(tuned).FixWholeParagraph);

        // Cautious overrides both switches, whatever they say.
        var cautious = new AppSettings { Boldness = Boldness.Cautious }.ToEngineOptions(tuned);
        Assert.False(cautious.FixWholeParagraph);
        Assert.Equal(0, cautious.RevisitAfterMs);
    }

    [Fact]
    public void R9_3_b_D52_ParagraphSwitchesAreSavedAndOnByDefault()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """{ "FirstRunDone": true }""");   // a settings file from before D52
        var old = new SettingsStore(file).Load();
        Assert.True(old.FixWholeParagraph);
        Assert.True(old.LookBackWhenPaused);

        var store = new SettingsStore(file);
        store.Save(old with { FixWholeParagraph = false, LookBackWhenPaused = false, IdleUnloadMinutes = 5 });
        var back = new SettingsStore(file).Load();
        Assert.False(back.FixWholeParagraph);
        Assert.False(back.LookBackWhenPaused);
        Assert.Equal(5, back.IdleUnloadMinutes);
    }

    [Fact]
    public void R5_2_PromptIsASettingAndABrokenFileFallsBack()
    {
        Directory.CreateDirectory(_dir);
        string good = Path.Combine(_dir, "prompt.json"), bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(good, """{ "System": "Fix typos.", "Examples": [ { "Role": "user", "Content": "teh" }, { "Role": "assistant", "Content": "the" } ] }""");
        File.WriteAllText(bad, "{ nope");
        Assert.Equal("Fix typos.", GlobalAutocorrect.Core.Ai.PromptTemplate.LoadOrDefault(good).System);
        Assert.Same(GlobalAutocorrect.Core.Ai.PromptTemplate.Default, GlobalAutocorrect.Core.Ai.PromptTemplate.LoadOrDefault(bad));
        Assert.Same(GlobalAutocorrect.Core.Ai.PromptTemplate.Default, GlobalAutocorrect.Core.Ai.PromptTemplate.LoadOrDefault(null));
    }

    [Fact]
    public void R5_2_ModelIsASettingDefaultingToTheSpikeAWinner() =>
        Assert.Equal("gemma-4-E4B-it-Q5_K_M.gguf", new AppSettings().Model);
}
