using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Tests.Instant;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Prediction;

/// <summary>D159 (user: "If this is still an op" should be "option", not "open": "based on the context").</summary>
public sealed class ModelRankingTests : IDisposable
{
    private static readonly Lazy<NgramModel> Model = new(() => NgramModel.FromAssets(HunspellTests.Assets()));
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ga-rank-" + Guid.NewGuid().ToString("N") + ".txt");

    public void Dispose() { try { File.Delete(_path); } catch (IOException) { } }

    private Predictor Make(Func<string, IReadOnlyList<TokenChance>?> next) => new(Model.Value, new UserHistory(_path)) { ModelNext = next };

    [Fact]
    public void R7_1_D159_TheModelsReadingOfTheSentencePicksTheWord()
    {
        string? asked = null;
        var p = Make(prompt => { asked = prompt; return [new("tion", 0.55), new("en", 0.12), new("portunity", 0.05)]; });
        var got = p.Suggest("I received a letter offering me more credit.\nIf this is still an op");
        Assert.True(got?.Word == "option", $"got={got} asked={(asked is null ? "never" : asked.Length.ToString())}");
        Assert.EndsWith("If this is still an op", asked);   // the sentence so far, earlier lines included
        Assert.Contains("letter", asked);
    }

    [Fact]
    public void R7_1_D159_NoClearPickOrNoModelFallsBackToTheWordList()
    {
        var flat = Make(_ => [new("tion", 0.2), new("en", 0.19)]);
        var none = Make(_ => null);
        var plain = new Predictor(Model.Value, new UserHistory(_path));
        string text = "If this is still an op";
        Assert.Equal(plain.Suggest(text)?.Word, flat.Suggest(text)?.Word);
        Assert.Equal(plain.Suggest(text)?.Word, none.Suggest(text)?.Word);
    }
}
