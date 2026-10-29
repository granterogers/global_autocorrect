using GlobalAutocorrect.DataGen;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.EvalData;

public sealed class EvalDataTests
{
    private const string Sample =
        "I think its time we spoke to their manager. I'll check the rota and a decent time for your meeting, " +
        "because they're not around until the afternoon. Don't forget that the printer on the second floor is broken.";

    [Fact]
    public void R6_1_a_MessagesValid()
    {
        var lines = EvalDataFiles.ReadJsonLines("messages.jsonl");
        Assert.True(lines.Count >= 400, $"only {lines.Count} messages");
        var ids = lines.Select(l => l.Str("id")).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Str("clean"))));
    }

    [Fact]
    public void R6_1_b_NoiseIsDeterministicAndKeepsProtectedTokens()
    {
        const string text = "Please call me at 3pm about the £12.99 invoice, see https://example.co.uk/x or email jo@example.com for details.";
        var a = new UkKeyboardNoise(new Random(20261029));
        var b = new UkKeyboardNoise(new Random(20261029));
        for (int i = 0; i < 50; i++)
        {
            var ra = a.Apply(text, NoiseLevel.Heavy);
            var rb = b.Apply(text, NoiseLevel.Heavy);
            Assert.Equal(rb.Noisy, ra.Noisy);
            Assert.Equal(rb.Ops, ra.Ops);
            Assert.Contains("3pm", ra.Noisy, StringComparison.Ordinal);
            Assert.Contains("£12.99", ra.Noisy, StringComparison.Ordinal);
            Assert.Contains("https://example.co.uk/x", ra.Noisy, StringComparison.Ordinal);
            Assert.Contains("jo@example.com", ra.Noisy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void R6_1_b_NoneLevelLeavesTextUnchanged()
    {
        var r = new UkKeyboardNoise(new Random(1)).Apply(Sample, NoiseLevel.None);
        Assert.Equal(Sample, r.Noisy);
        Assert.Empty(r.Ops);
    }

    [Fact]
    public void R6_1_b_EachOpTypeCanOccur()
    {
        string[] expected = ["drop", "double", "swap", "neighbour", "merge", "split", "misplace", "apostrophe", "lowercase", "grammar"];
        var seen = new HashSet<string>();
        var noise = new UkKeyboardNoise(new Random(7));
        for (int i = 0; i < 300; i++)
            foreach (string op in noise.Apply(Sample, NoiseLevel.Heavy).Ops) seen.Add(op);
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), seen.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void R6_1_b_NeighbourUsesUkAdjacency()
    {
        // UK QWERTY, staggered rows: a key touches left/right in its row plus two keys in each adjacent row.
        // For 'g' that is f, h (row), t, y (above), v, b (below). Diagonals r, u, c, n are NOT adjacent on a staggered layout.
        var allowed = new HashSet<char>("fhtyvb");
        Assert.Equal(allowed.OrderBy(c => c), UkKeyboardNoise.NeighboursOf('g').OrderBy(c => c));

        var seen = new HashSet<char>();
        for (int seed = 0; seed < 600; seed++)
        {
            var r = new UkKeyboardNoise(new Random(seed)).Apply("ggggg", NoiseLevel.Light);
            if (r.Ops.Count != 1 || r.Ops[0] != "neighbour") continue;
            var changed = r.Noisy.Where(c => c != 'g').ToList();
            Assert.Single(changed);
            Assert.Contains(changed[0], allowed);
            seen.Add(changed[0]);
        }
        Assert.Equal(allowed.Count, seen.Count);
    }

    [Fact]
    public void R6_1_c_FiftyGarbledMessages()
    {
        var lines = EvalDataFiles.ReadJsonLines("garbled.jsonl");
        Assert.Equal(50, lines.Count);
        Assert.All(lines, l => Assert.NotEqual(l.Str("clean"), l.Str("noisy")));
    }

    [Theory]
    [InlineData("holdout.jsonl")]
    [InlineData("tuning.jsonl")]
    public void R6_1_d_AtLeast20PercentClean(string file)
    {
        var lines = EvalDataFiles.ReadJsonLines(file);
        Assert.NotEmpty(lines);
        int clean = lines.Count(l => l.Str("noisy") == l.Str("clean"));
        Assert.True(clean * 5 >= lines.Count, $"{file}: {clean} of {lines.Count} are clean");
    }

    [Fact]
    public void R6_1_e_HoldoutAtLeast200AndDisjoint()
    {
        var holdout = EvalDataFiles.ReadJsonLines("holdout.jsonl").Select(l => l.Str("id")).ToList();
        var tuning = EvalDataFiles.ReadJsonLines("tuning.jsonl").Select(l => l.Str("id")).ToList();
        Assert.True(holdout.Count >= 200, $"holdout has {holdout.Count}");
        Assert.Empty(holdout.Intersect(tuning, StringComparer.Ordinal));
        var union = holdout.Concat(tuning).ToList();
        Assert.Equal(460, union.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(union.Count, union.Distinct(StringComparer.Ordinal).Count());
    }
}
