using System.Text;
using System.Text.RegularExpressions;
using GlobalAutocorrect.Core.Instant;

namespace GlobalAutocorrect.DataGen;

internal static class Commands
{
    private static readonly string[] LevelOrder = ["None", "Light", "Medium", "Heavy", "Garbled"];

    /// <summary>Per-message level choice: None 26%, Light 28%, Medium 28%, Heavy 18%.</summary>
    private static NoiseLevel PickLevel(Random rng)
    {
        double r = rng.NextDouble();
        if (r < 0.26) return NoiseLevel.None;
        if (r < 0.54) return NoiseLevel.Light;
        if (r < 0.82) return NoiseLevel.Medium;
        return NoiseLevel.Heavy;
    }

    private const int FreqTopN = 80_000;

    private static readonly Regex FreqToken = new("^[A-Za-z]+(['-][A-Za-z]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds the SymSpell frequency list (1.5): corpus tokens that are letters (with internal apostrophes/hyphens)
    /// and valid en_GB-ise, counts summed by lower-case form, top 80,000, "word count" per line.
    /// </summary>
    public static void Freq(string wordsPath, string assetsDir, string output)
    {
        var speller = HunspellSpellChecker.FromAssets(assetsDir);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(wordsPath, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !long.TryParse(parts[2], out long count)) continue;
            string token = parts[1];
            if (!FreqToken.IsMatch(token) || !speller.IsValid(token)) continue;
            string lower = token.ToLowerInvariant();
            counts[lower] = counts.GetValueOrDefault(lower) + count;
        }

        var top = counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(FreqTopN)
            .ToList();

        var sb = new StringBuilder();
        foreach (var kv in top) sb.Append(kv.Key).Append(' ').Append(kv.Value).Append('\n');
        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"freq: kept {counts.Count} valid words, wrote {top.Count} to {output}");
    }

    /// <summary>
    /// D68: an eval set of the user's own slip kinds, from the tuning set's clean texts only (the holdout is never used).
    /// Each kind gets <paramref name="perKind"/> items with only that kind of slip, labelled with the kind as its level,
    /// so the eval reports a fix rate per kind; "mixed" items use the user profile at about one slip in four words.
    /// </summary>
    public static void UserStyle(string tuningPath, string output, int seed, int perKind)
    {
        var rng = new Random(seed);
        var noise = new UkKeyboardNoise(rng);
        var cleans = JsonLines.Read<EvalItem>(tuningPath).Select(i => i.Clean).Where(c => c.Split(' ').Length >= 8).ToList();
        string[] kinds = ["misplace", "insert", "drop", "swap", "garble", "merge", "double", "triple", "neighbour", "doublespace", "spacepunct", "mixed"];
        var items = new List<EvalItem>();
        foreach (var kind in kinds)
        {
            for (int k = 0; k < perKind; k++)
            {
                string clean = cleans[rng.Next(cleans.Count)];
                int words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                int count = kind == "mixed" ? Math.Max(3, words / 4) : 1 + rng.Next(2);
                var r = noise.ApplyKind(clean, kind, count);
                if (r.Ops.Count == 0) continue;
                items.Add(new EvalItem($"{kind}-{k:D3}", clean, r.Noisy, kind, r.Ops));
            }
        }
        JsonLines.Write(output, items);
        Console.WriteLine($"userstyle: wrote {items.Count} items to {output}");
    }

    public static void Noise(string input, string output, int seed)
    {
        var rng = new Random(seed);
        var noise = new UkKeyboardNoise(rng);
        var items = new List<EvalItem>();
        foreach (var m in JsonLines.Read<CleanMessage>(input))
        {
            var level = PickLevel(rng);
            var result = noise.Apply(m.Clean, level);
            items.Add(new EvalItem(m.Id, m.Clean, result.Noisy, level.ToString(), result.Ops));
        }
        JsonLines.Write(output, items);
        Console.WriteLine($"noise: wrote {items.Count} items to {output}");
    }

    public static void Split(string noisyPath, string garbledPath, int seed, int holdoutSize, string outDir)
    {
        var all = JsonLines.Read<EvalItem>(noisyPath);
        all.AddRange(JsonLines.Read<GarbledMessage>(garbledPath)
            .Select(g => new EvalItem(g.Id, g.Clean, g.Noisy, "Garbled", ["garbled"])));

        var rng = new Random(seed);
        int total = all.Count;
        var groups = LevelOrder
            .Select(level => all.Where(i => i.Level == level).OrderBy(i => i.Id, StringComparer.Ordinal).ToList())
            .ToList();
        if (groups.Sum(g => g.Count) != total) throw new InvalidDataException("Items with an unknown level.");

        foreach (var g in groups) Shuffle(g, rng);

        var take = groups.Select(g => (int)Math.Round(g.Count * (double)holdoutSize / total, MidpointRounding.AwayFromZero)).ToArray();
        int diff = holdoutSize - take.Sum();
        if (diff != 0)
        {
            int largest = 0;
            for (int i = 1; i < groups.Count; i++) if (groups[i].Count > groups[largest].Count) largest = i;
            take[largest] += diff;
        }

        var holdout = new List<EvalItem>();
        var tuning = new List<EvalItem>();
        for (int i = 0; i < groups.Count; i++)
        {
            holdout.AddRange(groups[i].Take(take[i]));
            tuning.AddRange(groups[i].Skip(take[i]));
        }

        JsonLines.Write(Path.Combine(outDir, "holdout.jsonl"), holdout.OrderBy(i => i.Id, StringComparer.Ordinal));
        JsonLines.Write(Path.Combine(outDir, "tuning.jsonl"), tuning.OrderBy(i => i.Id, StringComparer.Ordinal));
        Console.WriteLine($"split: holdout {holdout.Count}, tuning {tuning.Count} (total {total})");
        Report("holdout", holdout);
        Report("tuning", tuning);
    }

    private static void Report(string name, List<EvalItem> items) =>
        Console.WriteLine($"  {name}: " + string.Join(", ", LevelOrder.Select(l => $"{l}={items.Count(i => i.Level == l)}")));

    private static void Shuffle<T>(IList<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
