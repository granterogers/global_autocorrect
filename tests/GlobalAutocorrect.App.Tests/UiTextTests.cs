using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GlobalAutocorrect.Core.Instant;
using Xunit;
using Path = System.IO.Path;

namespace GlobalAutocorrect.App.Tests;

/// <summary>
/// Every word a user can see is spelt correctly in British English (3.4, 0.4): XAML text in the views, plain-language
/// messages in the app's C# code, and the README. Checked against the same en_GB-ise dictionary the app uses.
/// </summary>
public sealed class UiTextTests
{
    // Names and technical terms that are right but not in a general dictionary.
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Autocorrect", "NVIDIA", "RTX", "PRO", "GPU", "CPU", "Gemma", "E4B", "E2B", "Qwen", "llama", "cpp", "SCOWL",
        "Leipzig", "Corpora", "CC", "BY", "ms", "GB", "MB", "Ctrl", "Alt", "Esc", "Tab", "Del", "Win", "AI", "UK", "rc1",
        "PowerShell", "pwsh", "setup", "ps1", "exe", "md", "json", "Defender", "Windows", "README", "NOTICE", "UI",
        "Wi", "Fi", "OK", "Mica", "Hunspell", "SymSpell", "UIA", "SendInput", "Lexical", "ProseMirror", "Edge",
        "Outlook", "Notepad", "WPF", "PCs", "PC", "zip", "dotnet", "en", "GB", "ise", "n", "gram", "grams", "GGUF",
        "Q5", "K", "M", "msedge", "devenv", "conhost", "KeePass", "KeePassXC", "AnyDesk", "TeamViewer", "Bitwarden",
        "Dashlane", "mstsc", "msrdc", "vmconnect", "CUDA", "cudart", "verify", "holdout", "tokenise", "unzip", "keylogger",
        "Math", "yyyy", "MMMM", "dd",
    };

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "GlobalAutocorrect.slnx"))) d = d.Parent;
        return d!.FullName;
    }

    private static readonly Lazy<HunspellSpellChecker> Speller = new(() => HunspellSpellChecker.FromAssets(Path.Combine(Repo(), "assets")));

    private static IEnumerable<(string Where, string Text)> XamlText()
    {
        string[] attrs = ["Text", "Content", "Title", "PlaceholderText", "Header", "ToolTip", "Description"];
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Repo(), "src", "GlobalAutocorrect.App"), "*.xaml", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (var el in XDocument.Load(f).Descendants())
            {
                foreach (var a in el.Attributes().Where(a => attrs.Contains(a.Name.LocalName) && !a.Value.StartsWith('{')))
                    yield return ($"{Path.GetFileName(f)} {el.Name.LocalName}.{a.Name.LocalName}", a.Value);
                if (el.Name.LocalName is "Run" or "TextBlock" && !el.HasElements && el.Value.Trim().Length > 0)
                    yield return ($"{Path.GetFileName(f)} {el.Name.LocalName}", el.Value);
            }
        }
    }

    /// <summary>String literals in the app's C# that contain a space (sentences users see), skipping paths, ids and formats.</summary>
    private static IEnumerable<(string Where, string Text)> CodeText()
    {
        var lit = new Regex("\\$?\"((?:[^\"\\\\]|\\\\.)*)\"");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Repo(), "src", "GlobalAutocorrect.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (var line in File.ReadLines(f))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in lit.Matches(line))
                {
                    string s = m.Groups[1].Value;
                    if (s.Contains(' ') && !s.Contains('\\') && !s.Contains("://") && s.Count(char.IsLetter) > 8)
                        yield return (Path.GetFileName(f), Regex.Replace(s, "\\{[^}]*\\}", " "));
                }
            }
        }
    }

    private static List<string> Misspelt(IEnumerable<(string Where, string Text)> texts)
    {
        var bad = new List<string>();
        foreach (var (where, text) in texts)
        {
            foreach (Match w in Regex.Matches(text, "[A-Za-z]+(?:'[A-Za-z]+)?"))
            {
                string word = w.Value;
                // Identifiers that leak from interpolations (CultureInfo, ToString) are code, not prose.
                bool identifier = Regex.IsMatch(word, "^[A-Za-z][a-z]+[A-Z]");
                if (Allowed.Contains(word) || word.Length == 1 || identifier) continue;
                if (!Speller.Value.IsValid(word)) bad.Add($"{where}: \"{word}\"");
            }
        }
        return bad;
    }

    [Fact]
    public void R3_4_InterfaceTextIsBritishAndSpeltCorrectly_R0_4()
    {
        var texts = XamlText().ToList();
        Assert.True(texts.Count > 80, $"only {texts.Count} texts found; extraction broke");
        var bad = Misspelt(texts);
        Assert.True(bad.Count == 0, "Misspelt or non-British words:\n" + string.Join('\n', bad.Distinct()));
    }

    [Fact]
    public void R3_4_MessagesInCodeAreBritishAndSpeltCorrectly()
    {
        var bad = Misspelt(CodeText());
        Assert.True(bad.Count == 0, "Misspelt or non-British words:\n" + string.Join('\n', bad.Distinct()));
    }

    [Fact]
    public void R13_2_ReadmeIsBritishAndSpeltCorrectly()
    {
        // Prose only: skip code blocks, inline code and table separators.
        string md = File.ReadAllText(Path.Combine(Repo(), "README.md"));
        // The requirement coverage table is a generated technical index (script and test names), not prose.
        int cov = md.IndexOf("## Requirement coverage", StringComparison.Ordinal);
        int next = cov < 0 ? -1 : md.IndexOf("\n## ", cov + 5, StringComparison.Ordinal);
        if (cov >= 0) md = md[..cov] + (next >= 0 ? md[next..] : "");
        md = Regex.Replace(md, "```.*?```", " ", RegexOptions.Singleline);
        md = Regex.Replace(md, "`[^`]*`", " ");
        var bad = Misspelt([("README.md", md)]);
        Assert.True(bad.Count == 0, "Misspelt or non-British words:\n" + string.Join('\n', bad.Distinct()));
    }
}
