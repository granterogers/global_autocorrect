using GlobalAutocorrect.Core.Health;

namespace GlobalAutocorrect.DataGen;

/// <summary>
/// D81: publishes what this PC's copy learned (per program, counts only) into the source. This PC's file in
/// <c>data/learning</c> is replaced, never added to, and the shipped <c>assets/app-profiles.json</c> is rebuilt from every
/// PC's file, so nothing is counted twice however often it runs.
/// </summary>
internal static class Learning
{
    public static void Publish(string localPath, string pcId, string dir, string outPath)
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
        File.WriteAllText(outPath, AppHealth.ToJson(combined));

        // D84: this PC's mistake fingerprints (never the words), and the shipped list of those enough PCs agree on.
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
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "mistakes.json"), System.Text.Json.JsonSerializer.Serialize(shared, json));
        Console.WriteLine($"learning: this PC {mine.Apps.Count} programs and {mem.Count} mistakes; shipped {combined.Apps.Count} programs and {shared.Mistakes.Count} mistakes");
    }
}
