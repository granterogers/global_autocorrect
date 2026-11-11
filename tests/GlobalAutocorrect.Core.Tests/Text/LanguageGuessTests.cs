using GlobalAutocorrect.Core.Tests.Simulated;
using GlobalAutocorrect.Core.Text;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Text;

/// <summary>D116, user request: "just ignore any languages that aren't English" (1.9: UK English only).</summary>
public sealed class LanguageGuessTests
{
    [Theory]
    [InlineData("Hola Jose, ¿cómo estás?")]
    [InlineData("Hola Jose,")]
    [InlineData("Necesito que me llames mañana por la tarde para hablar del proyecto.")]
    [InlineData("Muchas gracias por tu ayuda, nos vemos el lunes")]
    [InlineData("Bonjour, merci pour votre message, je suis dans le bureau demain.")]
    [InlineData("Danke für die Nachricht, ich bin heute nicht im Büro.")]
    [InlineData("Obrigado pela ajuda, não posso hoje.")]
    public void R1_9_D116_OtherLanguagesAreRecognised(string text) => Assert.True(LanguageGuess.IsForeign(text), text);

    [Theory]
    [InlineData("Thanks for the update, I'll call you tomorrow.")]
    [InlineData("We had a fiesta at the office and it was great.")]
    [InlineData("The café on the corner does a good résumé workshop.")]
    [InlineData("i went thier with teh dog yesterday")]
    [InlineData("Thakms,")]
    [InlineData("Hi Jose,")]
    [InlineData("Can you send me the report before Friday? Thanks")]
    public void R1_9_D116_EnglishStaysEnglishTyposAndForeignWordsIncluded(string text) => Assert.False(LanguageGuess.IsForeign(text), text);

    [Fact]
    public void R1_9_D116_SpanishIsLeftAsTypedWhileEnglishIsStillCorrected()
    {
        // The model would "correct" Spanish words into English ones and fix the English typo.
        var h = new Harness(model: s => s.Replace("que", "queue").Replace("teh", "the").Replace("mañana", "manna"), latencyMs: 50);
        h.Type("Necesito que me llames mañana por la tarde.\nI saw teh report. ");
        h.Wait(3000);
        Assert.Equal("Necesito que me llames mañana por la tarde.\nI saw the report. ", h.Text);
    }
}
