using GlobalAutocorrect.Core.Prediction;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Prediction;

public class FieldCatchUpTests
{
    [Theory]
    [InlineData("Looking forward to hea", 'a', true)]
    [InlineData("Looking forward to he", 'a', false)]     // the browser hasn't drawn the "a" yet (CKEditor in CI)
    [InlineData("Looking forward ", ' ', true)]      // a space typed last reads as a non-breaking space
    [InlineData("Looking forward", ' ', false)]
    [InlineData("", 'a', false)]
    public void R7_4_D59_TheSuggestionWaitsForTheFieldToShowTheKey(string before, char key, bool shows) =>
        Assert.Equal(shows, FieldCatchUp.ShowsKey(before, key));
}
