using System.Text;
using GlobalAutocorrect.Core.Tests.Simulated;
using Xunit;

namespace GlobalAutocorrect.Core.Tests.Engine;

/// <summary>D148 (stress run 44585361268, the misbehaving model): every way the stand-in misbehaves, on every word of three
/// lines typed on without stopping. None of it may reach the text: no lone space at a line start, no words joined, no word twice.</summary>
public sealed class MisbehavingModelTests
{
    private static readonly Dictionary<string, string> Fixes = new()
    {
        ["Rd"] = "Dr", ["Bailley,"] = "Bailey,", ["yyou're"] = "you're", ["yuo"] = "you", ["t"] = "it", ["th"] = "the", ["BBrighton"] = "Brighton",
    };

    private static string Oracle(string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] is ' ' or '\n') { sb.Append(text[i++]); continue; }
            int j = i;
            while (j < text.Length && text[j] is not (' ' or '\n')) j++;
            string w = text[i..j];
            sb.Append(Fixes.TryGetValue(w, out var f) ? f : w);
            i = j;
        }
        return sb.ToString();
    }

    private const string L1 = "Hello Rd Bailley, could you confirm that yyou're happy to speak for 20 minutes at the Bristol conference on 3 November?";
    private const string L2 = "Could yuo proofread this paragraph for me? I keep reading it and t still sounds odd.";
    private const string L3 = "Fancy getting fish and chips by th harbour in BBrighton after the show?";

    private static string Mis(string answer, int kind, int i)
    {
        var words = answer.Split(' ').ToList();
        if (words.Count < 3 || i >= words.Count - 1) return answer;
        bool content = words[i].Count(char.IsLetter) >= 5 && words[i + 1].Count(char.IsLetter) >= 5;
        if (words[i].Contains('\n') || words[i + 1].Contains('\n')) return answer;
        switch (kind)
        {
            case 0: return answer.ToUpperInvariant();
            case 1: words[i] = words[i].ToUpperInvariant(); break;
            case 2: words.Insert(i, words[i]); break;
            case 3 when content: words.RemoveAt(i); break;
            case 4: words[i] += "?"; break;
            case 5: words[i] += "!"; break;
            case 6 when content: (words[i], words[i + 1]) = (words[i + 1], words[i]); break;
            case 3 or 6: words[i] = words[i].ToUpperInvariant(); break;
            default: words.Insert(i, "really"); break;
        }
        return string.Join(' ', words);
    }

    /// <remarks>D186: 2,112 simulations, each with its own harness and clock, so they run side by side: one after another
    /// they took 37 of the fast check's 70 seconds (11.1.a: under 30).</remarks>
    [Fact]
    public void R4_5_D148_NoMisbehaviourOfTheModelReachesTheText()
    {
        var cases = (from target in new[] { "Hello", "Could", "Fancy" }
                     from trailSpace in new[] { false, true }
                     from kind in Enumerable.Range(0, 8)
                     from i in Enumerable.Range(0, 22)
                     from when in new[] { 0, 1 }
                     select (target, trailSpace, kind, i, when)).ToList();
        var bad = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, c =>
        {
            var (target, trailSpace, k, ii, when) = c;
            int calls = 0;
            var h = new Harness(model: t =>
            {
                string a = Oracle(t);
                if (t.Contains(target) && calls++ == when) a = Mis(a, k, ii);
                return a;
            }, latencyMs: 600);
            h.Type(L1 + "\n" + L2 + (trailSpace ? " " : "") + "\n");
            h.Type(L3[..29]);
            h.Wait(3000);
            h.Type(L3[29..]);
            h.Wait(8000);
            string[] lines = h.Text.Split('\n');
            string[] want = [L1, L2, L3];
            for (int l = 0; l < 3; l++)
                if (lines.Length != 3 || lines[l].TrimEnd().StartsWith(' ') || lines[l].TrimEnd().Split(' ').Length != want[l].Split(' ').Length
                    || lines[l].TrimEnd().Split(' ').Zip(lines[l].TrimEnd().Split(' ').Skip(1)).Any(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)))
                { bad.Add($"{target} trail={trailSpace} kind={k} i={ii} when={when}: [{h.Text.Replace('\n', '|')}]"); break; }
        });
        Assert.True(bad.IsEmpty, string.Join("\n", bad.Order(StringComparer.Ordinal)));
    }
}
