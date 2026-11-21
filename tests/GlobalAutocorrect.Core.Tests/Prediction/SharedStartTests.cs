using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.Core.Tests.Instant;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Prediction;

/// <summary>D158 (user: "If this is still an opt" … "ideally it should be option").</summary>
public sealed class SharedStartTests : IDisposable
{
    private static readonly Lazy<NgramModel> Model = new(() => NgramModel.FromAssets(HunspellTests.Assets()));
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ga-shared-" + Guid.NewGuid().ToString("N") + ".txt");

    public void Dispose() { try { File.Delete(_path); } catch (IOException) { } }

    [Fact]
    public void R7_1_D158_ACloseCallOffersTheWordBothCandidatesStartWith()
    {
        var p = new Predictor(Model.Value, new UserHistory(_path));
        Assert.Equal("option", p.Suggest("If this is still an opt")?.Word);   // "options" and "option" were too close: nothing
    }
}
