using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Learning;

namespace GlobalAutocorrect.DataGen;

/// <summary>
/// D81: publishes what this PC's copy learned (per program, counts only) into the source. This PC's file in
/// <c>data/learning</c> is replaced, never added to, and the brain's profiles and mistakes (D102, <c>brain/brain.json</c>)
/// are rebuilt from every PC's file, so nothing is counted twice however often it runs. The brain's tuning and write rules
/// are kept as they are, and its date changes only when something in it did.
/// </summary>
internal static class Learning
{
    /// <summary>
    /// D111: writes the built-in prompt and rule thresholds into the brain where it has none, so they can be seen and
    /// tuned there. Values the brain already has are kept. The app behaves exactly as before.
    /// </summary>
    public static void WriteDefaults(string brainPath)
    {
        var b = Brain.Read(brainPath) ?? new BrainFile();
        var rules = new GlobalAutocorrect.Core.Engine.EngineOptions().Rules;
        var tuning = new Dictionary<string, double>(b.Tuning, StringComparer.OrdinalIgnoreCase);
        void Add(string k, double v) { if (!tuning.ContainsKey(k)) tuning[k] = v; }
        Add("Rules.MaxNormalisedDistance", rules.MaxNormalisedDistance);
        Add("Rules.MaxChangedWordRatio", rules.MaxChangedWordRatio);
        Add("Rules.MaxNonWordDistance", rules.MaxNonWordDistance);
        Add("Rules.AcceptLikelyKeyboardSlips", rules.AcceptLikelyKeyboardSlips ? 1 : 0);
        Add("Rules.LenientWithNonWords", rules.LenientWithNonWords ? 1 : 0);
        var d = GlobalAutocorrect.Core.Ai.PromptTemplate.Default;
        var next = b with
        {
            Tuning = tuning,
            Prompt = b.Prompt ?? new BrainPrompt(d.System, [.. d.Examples]),
            Updated = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        };
        string text = Brain.ToJson(next);
        if (Brain.Parse(text) is not { Prompt: not null }) throw new InvalidOperationException("the brain written would not load in the app");
        File.WriteAllText(brainPath, text);
        Console.WriteLine($"brain-defaults: {tuning.Count} tuning values, prompt with {next.Prompt!.Examples.Count} examples");
    }

    public static void Publish(string localPath, string pcId, string dir, string brainPath)
    {
        if (!Brain.IsPcId(pcId))
            throw new ArgumentException("--pc must be a short id of letters, digits and dashes");
        var today = DateOnly.FromDateTime(DateTime.Now);
        Directory.CreateDirectory(dir);
        var mine = AppHealth.Combine([AppHealth.ReadLocal(localPath, today)], today);
        File.WriteAllText(Path.Combine(dir, pcId + ".json"), AppHealth.ToJson(mine));

        // D103: each PC's counts, by its id, so a PC can leave its own out when it loads the brain.
        var pcs = Directory.GetFiles(dir, "*.json")
            .Select(f => (Id: Path.GetFileNameWithoutExtension(f), File: AppHealth.Read<AppHealth.ProfileFile>(f)))
            .Where(p => p.File is not null && Brain.IsPcId(p.Id))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(p => p.Id, p => new Dictionary<string, AppStats>(p.File!.Apps, StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var combined = AppHealth.Combine(pcs.Values.Select(v => (IReadOnlyDictionary<string, AppStats>)v), today);

        // D84: this PC's mistake fingerprints (never the words), and the shared list of those enough PCs agree on.
        string mistakesDir = Path.Combine(dir, "mistakes");
        Directory.CreateDirectory(mistakesDir);
        string localMistakes = Path.Combine(Path.GetDirectoryName(localPath)!, "mistakes.json");
        var mem = new MistakeMemory(localMistakes);
        mem.Load();
        var json = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(mistakesDir, pcId + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new MistakeMemory.File(new(mem.Local.OrderBy(m => m.Key, StringComparer.Ordinal).ToDictionary())), json));
        var shared = MistakeMemory.Combine(Directory.GetFiles(mistakesDir, "*.json")
            .Select(AppHealth.Read<MistakeMemory.File>).Where(f => f is not null).Select(f => (IReadOnlyDictionary<string, int>)f!.Mistakes));

        var old = Brain.Read(brainPath) ?? new BrainFile();
        var next = old with { Apps = combined.Apps, Mistakes = shared.Mistakes, Pcs = pcs };
        if (Brain.ToJson(next with { Updated = "" }) != Brain.ToJson(old with { Updated = "" }))
            next = next with { Updated = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) };
        else next = old;
        string text = Brain.ToJson(next);
        if (Brain.Parse(text) is null) throw new InvalidOperationException("the brain written would not load in the app");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(brainPath))!);
        File.WriteAllText(brainPath, text);
        Console.WriteLine($"learning: this PC {mine.Apps.Count} programs and {mem.Count} mistakes; brain {next.Apps.Count} programs and {next.Mistakes.Count} mistakes, updated {next.Updated}");
    }
}
