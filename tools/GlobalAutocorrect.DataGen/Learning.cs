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
    public static void Publish(string localPath, string pcId, string dir, string brainPath)
    {
        if (pcId.Length is < 4 or > 64 || pcId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("--pc must be a short id of letters, digits and dashes");
        var today = DateOnly.FromDateTime(DateTime.Now);
        Directory.CreateDirectory(dir);
        var mine = AppHealth.Combine([AppHealth.ReadLocal(localPath, today)], today);
        File.WriteAllText(Path.Combine(dir, pcId + ".json"), AppHealth.ToJson(mine));

        var all = Directory.GetFiles(dir, "*.json")
            .Select(AppHealth.Read<AppHealth.ProfileFile>)
            .Where(p => p is not null)
            .Select(p => (IReadOnlyDictionary<string, AppStats>)p!.Apps);
        var combined = AppHealth.Combine(all, today);

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
        var next = old with { Apps = combined.Apps, Mistakes = shared.Mistakes };
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
