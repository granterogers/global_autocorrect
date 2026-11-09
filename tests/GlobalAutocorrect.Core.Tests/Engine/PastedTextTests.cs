using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Tests.Simulated;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D107, user report: "you also screwed up pasting a hyperlink... don't do that" (4.2: pasted text is never corrected).</summary>
public sealed class PastedTextTests
{
    [Theory]
    [InlineData("example.co.uk/support/teh-page")]
    [InlineData("intercom.help/en/articles/123-teh")]
    [InlineData("ftp://files.example.com/teh")]
    [InlineData("https://example.com/a(b)c")]
    public void R4_5_d_D107_LinksWithOrWithoutASchemeAreOneUrlToken(string link)
    {
        var t = Tokeniser.Tokenise($"see {link} now").Single(x => x.Text.StartsWith(link[..6], StringComparison.Ordinal));
        Assert.True((t.Flags & TokenFlags.Url) != 0 && t.Text == link, t.Text);
    }

    [Fact]
    public void R4_5_d_D107_AMissingSpaceAfterAFullStopIsStillAWordToFix()
    {
        Assert.DoesNotContain(Tokeniser.Tokenise("for now.Is it on"), t => (t.Flags & TokenFlags.Url) != 0);
    }

    [Fact]
    public void R4_2_D107_TextThatCameWithAPasteIsContextButNeverCorrected()
    {
        // The model would "fix" a word inside the pasted text and one the user typed after it.
        var h = new Harness(model: s => s.Replace("recieve", "receive").Replace("helo", "hello"), latencyMs: 50);
        h.Engine.OnInput(h.Ed.Paste("Pasted note: please recieve it "));
        h.Type("and helo again. ");
        h.Wait(2500);
        Assert.Equal("Pasted note: please recieve it and hello again. ", h.Text);
        Assert.Equal(0, h.Counters.Desyncs);
    }
}
