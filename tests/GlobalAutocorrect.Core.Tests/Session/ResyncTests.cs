using GlobalAutocorrect.Core.Session;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Session;

public class ResyncTests
{
    private static TypingSession Typed(string s)
    {
        var session = new TypingSession();
        long seq = 0;
        foreach (char c in s) session.Apply(new KeyInput(KeyKind.Char, c, ++seq, seq * 50));
        return session;
    }

    [Fact]
    public void R4_6_c_D61_AReadBehindTheKeysIsNeverAdopted()
    {
        // Random-typing test: a resync from a read that hadn't drawn the last two keys left the record two short, and a
        // later fix landed two characters off.
        var s = Typed("I could ");
        Assert.False(s.Resync("I cou"));
        Assert.Equal("I could ", s.Typed);
    }

    [Theory]
    [InlineData("Notepad made it I Could ")]       // the app's own autocorrect changed text before the last key
    [InlineData("I could ")]                  // a space typed last reads as a non-breaking space
    public void R4_6_c_D61_AFieldThatShowsTheLastKeyIsAdopted(string field)
    {
        var s = Typed("I could ");
        Assert.True(s.Resync(field));
    }
}
