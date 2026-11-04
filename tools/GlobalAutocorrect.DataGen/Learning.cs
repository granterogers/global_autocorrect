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
        Console.WriteLine($"learning: this PC {mine.Apps.Count} programs, shipped profile {combined.Apps.Count} programs");
    }
}
