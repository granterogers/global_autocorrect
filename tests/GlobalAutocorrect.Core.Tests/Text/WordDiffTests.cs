using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Text;

public sealed class WordDiffTests
{
    private static IReadOnlyList<TextEdit> Diff(string a, string b)
    {
        var edits = WordDiff.Compute(a, b);
        Assert.Equal(b, TextEdit.ApplyAll(a, edits));   // every diff must reproduce the cleaned text exactly
        return edits;
    }

    [Fact]
    public void R4_4_e_UnchangedTextGivesNoEdits() => Assert.Empty(Diff("All good here.", "All good here."));

    [Fact]
    public void R4_4_e_SubstitutionIsMinimalAndKeepsWordSpan()
    {
        var e = Assert.Single(Diff("I saw teh cat", "I saw the cat"));
        Assert.Equal(EditKind.Substitution, e.Kind);
        Assert.Equal(["teh"], e.OriginalWords);
        Assert.Equal(["the"], e.NewWords);
        Assert.Equal((6, 9), (e.SpanStart, e.SpanEnd));
        Assert.True(e.End - e.Start <= 3);
    }

    [Fact]
    public void R4_4_e_SeparateMistakesBecomeSeparateEdits()
    {
        var edits = Diff("teh cat sat on teh mat", "the cat sat on the mat");
        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Equal(EditKind.Substitution, e.Kind));
    }

    [Theory]
    [InlineData("I hadto stand", "I had to stand")]
    [InlineData("just incase it rains", "just in case it rains")]
    [InlineData("the appoint ment is", "the appointment is")]
    public void R4_4_c_RunTogetherAndSplitWordsAreSpacingEdits(string a, string b)
    {
        var e = Assert.Single(Diff(a, b));
        Assert.Equal(EditKind.Spacing, e.Kind);
    }

    [Theory]
    [InlineData("hello ,world", "hello, world")]
    [InlineData("so  much", "so much")]
    public void R4_4_c_SpaceOnlyChangesAreSpacing(string a, string b) =>
        Assert.All(Diff(a, b), e => Assert.Equal(EditKind.Spacing, e.Kind));

    [Theory]
    [InlineData("dont go", "don't go", EditKind.Apostrophe)]
    [InlineData("im here", "I'm here", EditKind.Apostrophe)]
    [InlineData("in london", "in London", EditKind.Capitalisation)]
    [InlineData("ok then", "ok then.", EditKind.Punctuation)]
    public void R4_4_c_ClassifiesApostropheCapitalisationPunctuation(string a, string b, EditKind kind) =>
        Assert.Equal(kind, Assert.Single(Diff(a, b)).Kind);

    [Fact]
    public void R4_4_c_InsertsAndDeletesFunctionWords()
    {
        var ins = Assert.Single(Diff("I going home", "I am going home"));
        Assert.Equal(EditKind.Insertion, ins.Kind);
        Assert.Equal(["am"], ins.NewWords);

        var del = Assert.Single(Diff("see the the report", "see the report"));
        Assert.Equal(EditKind.Deletion, del.Kind);
    }

    [Fact]
    public void R4_4_e_HeavyGarbleStillDiffsWordByWord()
    {
        const string a = "cna yuo sned me teh sldies befor ethe meeting tomorow? i wnat to chekc the nubmers frist.";
        const string b = "Can you send me the slides before the meeting tomorrow? I want to check the numbers first.";
        var edits = Diff(a, b);
        Assert.True(edits.Count >= 10);
        Assert.All(edits, e => Assert.True(Math.Max(e.OriginalWords.Count, e.NewWords.Count) <= 2));
    }
}
