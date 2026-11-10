using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Tests.EvalData;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D110: a line of one word can be fixed; a one-word rewrite still can't.</summary>
public sealed class OneWordLineRuleTests
{
    private static readonly EditRules Rules = new(new EditRuleSettings(),
        HunspellSpellChecker.FromAssets(Path.Combine(EvalDataFiles.RepoRoot(), "assets")), _ => false);

    private static bool Accepted(string a, string b) => Rules.Filter(a, b, WordDiff.Compute(a, b)).Accepted.Any();

    [Theory]
    [InlineData("Thakms,", "Thanks,")]
    [InlineData("Regadrs,", "Regards,")]
    [InlineData("Cheesr", "Cheers")]
    public void R4_5_d_D110_ASignOffAloneOnItsLineIsFixed(string typed, string meant) => Assert.True(Accepted(typed, meant));

    [Theory]
    [InlineData("Thanks,", "Cheers,")]    // a different word, not a typo
    [InlineData("Thanks,", "Thank you,")]
    public void R4_5_d_D110_AOneWordLineIsStillNeverReworded(string typed, string model) => Assert.False(Accepted(typed, model));
}
