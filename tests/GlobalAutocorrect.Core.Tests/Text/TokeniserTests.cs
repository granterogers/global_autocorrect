using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Text;

public sealed class TokeniserTests
{
    private static Token[] Words(string s) => Tokeniser.Tokenise(s).Where(t => t.IsWord).ToArray();

    [Fact]
    public void R4_4_e_KeepsApostrophesHyphensAndNumberPunctuationInsideWords()
    {
        Assert.Equal(["don't", "well-known", "10:30", "2.5", "£1,000", "50%"],
            Words("don't well-known 10:30, 2.5 £1,000 50%.").Select(t => t.Text));
    }

    [Fact]
    public void R4_4_e_OffsetsRoundTrip()
    {
        const string s = "Hi  there,\r\nok? 👍";
        var tokens = Tokeniser.Tokenise(s);
        Assert.Equal(s, string.Concat(tokens.Select(t => t.Text)));
        Assert.All(tokens, t => Assert.Equal(t.Text, s[t.Start..t.End]));
        Assert.Contains(tokens, t => t.Kind == TokenKind.Newline && t.Text == "\r\n");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Punct && t.Text == "👍");
    }

    [Theory]
    [InlineData("https://example.co.uk/report.", "https://example.co.uk/report", TokenFlags.Url)]
    [InlineData("www.example.co.uk/menu", "www.example.co.uk/menu", TokenFlags.Url)]
    [InlineData("sam.jones@example.com,", "sam.jones@example.com", TokenFlags.Email)]
    public void R4_3_c_DetectsUrlsAndEmails(string input, string token, TokenFlags flag)
    {
        var w = Assert.Single(Words(input));
        Assert.Equal(token, w.Text);
        Assert.Equal(flag, w.Flags);
        Assert.True(w.IsProtected);
    }

    [Theory]
    [InlineData("3pm", TokenFlags.Number)]
    [InlineData("b2b", TokenFlags.Number)]
    [InlineData("my_var", TokenFlags.CodeLike)]
    [InlineData("getText", TokenFlags.CodeLike)]
    [InlineData("#release", TokenFlags.CodeLike)]
    [InlineData("hello", TokenFlags.None)]
    [InlineData("London", TokenFlags.None)]
    public void R4_3_c_ClassifiesDigitsAndCodeLikeTokens(string word, TokenFlags expected)
    {
        Assert.Equal(expected, Assert.Single(Words(word)).Flags);
    }

    [Fact]
    public void R4_5_a_NormDistIsEditDistanceOverLongerLength()
    {
        Assert.Equal(1.0 / 3, Similarity.NormDist("teh", "the"), 3);   // one adjacent swap
        Assert.Equal(0.2, Similarity.NormDist("bookk", "book"), 3);
        Assert.Equal(0, Similarity.NormDist("", ""));
        Assert.True(Similarity.NormDist("happy", "glad") > 0.5);
    }

    [Fact]
    public void R4_4_b_SplitsSentencesButNotAbbreviationsOrDecimals()
    {
        const string s = "Meet Dr. Smith at 2.30 today. It's fine! Is it? not finished";
        var parts = SentenceSplitter.Split(s).Select(r => s[r.Start..r.End]).ToArray();
        Assert.Equal(["Meet Dr. Smith at 2.30 today.", "It's fine!", "Is it?", "not finished"], parts);
        Assert.Equal(s.IndexOf("It's", StringComparison.Ordinal), SentenceSplitter.StartOfLastSentences(s, 3));
    }

    [Theory]
    [InlineData("All done. ", true)]
    [InlineData("Really? ", true)]
    [InlineData("All done.", false)]
    [InlineData("See Mr. ", false)]
    [InlineData("still typing ", false)]
    public void R4_4_a_DetectsSentenceEnd(string text, bool expected) =>
        Assert.Equal(expected, SentenceSplitter.EndsSentence(text));
}
