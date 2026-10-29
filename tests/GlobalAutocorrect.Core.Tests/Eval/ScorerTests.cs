using GlobalAutocorrect.Core.Eval;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Eval;

public sealed class ScorerTests
{
    [Fact]
    public void R6_3_a_PerfectFixCountsAllErrorsFixed()
    {
        var s = Scorer.Score("I saw the cat", "i saw teh cat", "I saw the cat");
        Assert.Equal(2, s.NeedFix);
        Assert.Equal(2, s.Fixed);
        Assert.Equal(0, s.FalseEdits);
        Assert.True(s.Exact);
    }

    [Fact]
    public void R6_3_a_PartialFix()
    {
        var s = Scorer.Score("I saw the cat", "i saw teh cat", "I saw teh cat");
        Assert.Equal(2, s.NeedFix);
        Assert.Equal(1, s.Fixed);
        Assert.False(s.Exact);
    }

    [Fact]
    public void R6_3_b_FalseEditCounted()
    {
        var s = Scorer.Score("I feel happy today", "I feel happy today", "I feel glad today");
        Assert.Equal(4, s.CorrectWords);
        Assert.Equal(1, s.FalseEdits);
        Assert.Equal(0, s.NeedFix);
    }

    [Fact]
    public void R6_3_b_RunTogetherCountsBothWordsAsNeedingFix()
    {
        var s = Scorer.Score("I had to go", "I hadto go", "I had to go");
        Assert.Equal(2, s.NeedFix);
        Assert.Equal(2, s.Fixed);
    }

    [Fact]
    public void R6_3_c_ExactMatchRateAggregates()
    {
        var a = Scorer.Combine([
            new MessageScore(2, 2, 3, 0, true),
            new MessageScore(1, 0, 4, 1, false),
            new MessageScore(0, 0, 5, 0, true),
        ]);
        Assert.Equal(3, a.Messages);
        Assert.Equal(2.0 / 3, a.ExactMatchRate, 10);
        Assert.Equal(2.0 / 3, a.ErrorsFixedRate, 10);
        Assert.Equal(1.0 / 12, a.FalseEditRate, 10);
    }

    [Fact]
    public void R6_3_a_DoubledWordInNoiseNeedsFix()
    {
        var s = Scorer.Score("see the report", "see the the report", "see the report");
        Assert.Equal(1, s.NeedFix);
        Assert.Equal(1, s.Fixed);
        Assert.True(s.Exact);
    }

    [Fact]
    public void R6_3_e_EmptyAggregateIsSafe()
    {
        var a = Scorer.Combine([]);
        Assert.Equal(1, a.ErrorsFixedRate);
        Assert.Equal(0, a.FalseEditRate);
        Assert.Equal(0, a.ExactMatchRate);
    }
}
