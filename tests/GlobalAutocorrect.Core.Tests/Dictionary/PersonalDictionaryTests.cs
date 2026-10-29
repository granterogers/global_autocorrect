using GlobalAutocorrect.Core.Dictionary;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Dictionary;

public sealed class PersonalDictionaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ga-dict-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "dictionary.txt");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void R8_5_AddContainsRemoveRoundTripsThroughFile()
    {
        var dict = new PersonalDictionary(FilePath);
        Assert.True(dict.Add("Grnat"));
        Assert.True(dict.Add("Zorblax"));
        Assert.True(dict.Contains("Grnat"));

        var reloaded = new PersonalDictionary(FilePath);
        Assert.True(reloaded.Contains("Grnat"));
        Assert.True(reloaded.Contains("Zorblax"));
        Assert.Equal(2, reloaded.Words.Count);

        Assert.True(reloaded.Remove("Grnat"));
        Assert.False(reloaded.Contains("Grnat"));

        var again = new PersonalDictionary(FilePath);
        Assert.False(again.Contains("Grnat"));
        Assert.True(again.Contains("Zorblax"));
    }

    [Fact]
    public void R8_5_ContainsIsCaseInsensitiveAndIgnoresPunctuation()
    {
        var dict = new PersonalDictionary(FilePath);
        dict.Add("Grnat");

        Assert.True(dict.Contains("grnat,"));
        Assert.True(dict.Contains("GRNAT"));
        Assert.True(dict.Contains("(Grnat)"));
        Assert.True(dict.Contains("\"grnat?\""));
        Assert.False(dict.Contains("grant"));
    }

    [Fact]
    public void R8_5_AutoLearnsAfterThreeUnrecognisedUses()
    {
        var dict = new PersonalDictionary(FilePath, autoLearnThreshold: 3, autoLearnEnabled: true);

        Assert.False(dict.NoteUnrecognised("zorblax"));
        Assert.False(dict.NoteUnrecognised("zorblax"));
        Assert.True(dict.NoteUnrecognised("zorblax"));
        Assert.True(dict.Contains("zorblax"));
    }

    [Fact]
    public void R8_5_AutoLearnCanBeDisabled()
    {
        var dict = new PersonalDictionary(FilePath, autoLearnThreshold: 3, autoLearnEnabled: false);

        for (var i = 0; i < 5; i++)
        {
            Assert.False(dict.NoteUnrecognised("zorblax"));
        }

        Assert.False(dict.Contains("zorblax"));

        dict.AutoLearnEnabled = true;
        Assert.False(dict.NoteUnrecognised("zorblax"));
        Assert.False(dict.NoteUnrecognised("zorblax"));
        Assert.True(dict.NoteUnrecognised("zorblax"));
    }

    [Fact]
    public void R8_6_FileIsPlainUtf8WithoutBomOneWordPerLine()
    {
        var dict = new PersonalDictionary(FilePath);
        dict.Add("bravo");
        dict.Add("alpha");

        var bytes = File.ReadAllBytes(FilePath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("alpha\nbravo\n", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void R8_6_AutoLearnCountsAreNotPersisted()
    {
        var dict = new PersonalDictionary(FilePath);
        dict.NoteUnrecognised("zorblax");
        dict.NoteUnrecognised("zorblax");

        Assert.False(File.Exists(FilePath) && File.ReadAllText(FilePath).Contains("zorblax", StringComparison.Ordinal));

        // A fresh instance starts counting from zero: two more uses are not enough.
        var fresh = new PersonalDictionary(FilePath);
        Assert.False(fresh.NoteUnrecognised("zorblax"));
        Assert.False(fresh.NoteUnrecognised("zorblax"));
        Assert.False(fresh.Contains("zorblax"));
    }

    [Fact]
    public void R4_8_c_AddReturnsFalseForDuplicatesAndBlank()
    {
        var dict = new PersonalDictionary(FilePath);

        Assert.True(dict.Add("Grnat"));
        Assert.False(dict.Add("Grnat"));
        Assert.False(dict.Add("grnat"));
        Assert.False(dict.Add(""));
        Assert.False(dict.Add("   "));
        Assert.False(dict.Add("two words"));
        Assert.Single(dict.Words);
    }

    [Fact]
    public void R8_5_ChangedEventFires()
    {
        var dict = new PersonalDictionary(FilePath, autoLearnThreshold: 2);
        var count = 0;
        dict.Changed += () => count++;

        dict.Add("alpha");
        Assert.Equal(1, count);

        dict.Add("alpha");
        Assert.Equal(1, count);

        dict.Remove("alpha");
        Assert.Equal(2, count);

        dict.NoteUnrecognised("zorblax");
        Assert.Equal(2, count);
        dict.NoteUnrecognised("zorblax");
        Assert.Equal(3, count);
    }
}
