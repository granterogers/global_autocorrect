namespace GlobalAutocorrect.DataGen;

internal static class Program
{
    private const string Usage = """
        Usage:
          GlobalAutocorrect.DataGen noise --in <messages.jsonl> --out <noisy.jsonl> --seed <int>
          GlobalAutocorrect.DataGen split --noisy <noisy.jsonl> --garbled <garbled.jsonl> --seed <int> --holdout <int> --out <dir>
          GlobalAutocorrect.DataGen freq --words <words.txt> --assets <assetsDir> --out <freq_en_GB.txt>
          GlobalAutocorrect.DataGen ngram --sentences <sentences.txt> --assets <assetsDir> --out <ngram.bin> [--min-count <int>]
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("noise" or "split" or "freq" or "ngram"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var opts = ParseOptions(args.Skip(1).ToArray());
        if (opts is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            if (args[0] == "noise")
            {
                if (!Has(opts, "in", "out", "seed") || !int.TryParse(opts["seed"], out int seed)) return Fail();
                Commands.Noise(opts["in"], opts["out"], seed);
            }
            else if (args[0] == "ngram")
            {
                if (!Has(opts, "sentences", "assets", "out")) return Fail();
                int minCount = 3;
                if (opts.TryGetValue("min-count", out var mc) && !int.TryParse(mc, out minCount)) return Fail();
                NgramBuilder.Build(opts["sentences"], opts["assets"], opts["out"], minCount);
            }
            else if (args[0] == "freq")
            {
                if (!Has(opts, "words", "assets", "out")) return Fail();
                Commands.Freq(opts["words"], opts["assets"], opts["out"]);
            }
            else
            {
                if (!Has(opts, "noisy", "garbled", "seed", "holdout", "out")
                    || !int.TryParse(opts["seed"], out int seed)
                    || !int.TryParse(opts["holdout"], out int holdout)) return Fail();
                Commands.Split(opts["noisy"], opts["garbled"], seed, holdout, opts["out"]);
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}");
            return 1;
        }
    }

    private static int Fail()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static bool Has(Dictionary<string, string> opts, params string[] keys) => keys.All(opts.ContainsKey);

    private static Dictionary<string, string>? ParseOptions(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) return null;
            d[args[i][2..]] = args[i + 1];
        }
        return d;
    }
}
