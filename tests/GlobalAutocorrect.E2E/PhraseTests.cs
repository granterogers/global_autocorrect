using System.IO;
using GlobalAutocorrect.Core.Prediction;
using GlobalAutocorrect.E2E.Support;
using GlobalAutocorrect.Windows.Security;
using Xunit;
using static GlobalAutocorrect.Windows.Native.NativeMethods;

namespace GlobalAutocorrect.E2E;

/// <summary>D142: a phrase the user repeats comes back whole after its first words, and Tab types all of it.</summary>
[Collection("screen")]
[Trait("Category", "E2E")]
public sealed class PhraseTests
{
    private static string Model(string s) => s;

    /// <summary>The phrases file as the app keeps it: encrypted for this Windows user (synthetic text).</summary>
    private static void LearnedPhrase(string dataDir)
    {
        var day = new DateOnly(2026, 11, 1);
        var m = new PhraseMemory(() => day);
        for (int d = 0; d < 2; d++)
        {
            for (int i = 0; i < 2; i++)
            {
                foreach (var w in "let me know if you have any questions".Split(' ')) m.Word(w);
                m.Break();
            }
            day = DateOnly.FromDateTime(DateTime.Now).AddDays(-d);   // recent, so it isn't forgotten
        }
        Assert.NotEmpty(m.Kept);
        File.WriteAllBytes(Path.Combine(dataDir, "phrases.dat"), UserOnlyCrypto.Protect(m.Save()));
    }

    [Fact]
    public void R7_4_D142_TabTypesTheRestOfARepeatedPhrase()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm, prediction: true, seed: LearnedPhrase);
        using var box = new DesktopTests.WpfBox();
        Desk.Type("Thanks, let me know ");
        Thread.Sleep(1200);   // the suggestion appears after a background read
        Desk.Keys(VK_TAB);
        Assert.True(Desk.WaitUntil(() => box.Text.StartsWith("Thanks, let me know if you have any questions", StringComparison.Ordinal), 4000),
            $"got \"{box.Text}\"; {app.Diagnostics()}");
        Assert.Equal(0, app.Desyncs);
    }
}
