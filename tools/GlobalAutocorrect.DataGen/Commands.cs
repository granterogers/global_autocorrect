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
