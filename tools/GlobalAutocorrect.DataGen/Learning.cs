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

    /// <summary>D179: points every install's "Share what it learns" at the relay (relay/deploy.ps1). Only the address changes.</summary>
    public static void WriteShare(string url, string brainPath)
    {
        var b = Brain.Read(brainPath) ?? throw new InvalidOperationException("the brain didn't load");
        var share = new BrainShare(url);
        if (share.Address is null) throw new ArgumentException("--url must be an https address");
        if (b.Share?.Url == url) { Console.WriteLine("brain-share: already " + url); return; }
        var next = b with { Share = share, Updated = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) };
        string text = Brain.ToJson(next);
        if (Brain.Parse(text)?.Share is null) throw new InvalidOperationException("the brain written would not load in the app");
        File.WriteAllText(brainPath, text);
        Console.WriteLine("brain-share: " + url);
    }

    public static void Publish(string localPath, string pcId, string dir, string brainPath)
    {
        if (!Brain.IsPcId(pcId))
            throw new ArgumentException("--pc must be a short id of letters, digits and dashes");
        var today = DateOnly.FromDateTime(DateTime.Now);
        Directory.CreateDirectory(dir);
        var mine = AppHealth.Combine([AppHealth.ReadLocal(localPath, today)], today);
        File.WriteAllText(Path.Combine(dir, pcId + ".json"), AppHealth.ToJson(mine));

        // D84: this PC's mistake fingerprints (never the words).
        string mistakesDir = Path.Combine(dir, "mistakes");
        Directory.CreateDirectory(mistakesDir);
        string localMistakes = Path.Combine(Path.GetDirectoryName(localPath)!, "mistakes.json");
        var mem = new MistakeMemory(localMistakes);
        mem.Load();
        File.WriteAllText(Path.Combine(mistakesDir, pcId + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new MistakeMemory.File(new(mem.Local.OrderBy(m => m.Key, StringComparer.Ordinal).ToDictionary())), Indented));

        var next = Rebuild(dir, brainPath, today);
        Console.WriteLine($"learning: this PC {mine.Apps.Count} programs and {mem.Count} mistakes; brain {next.Apps.Count} programs and {next.Mistakes.Count} mistakes, updated {next.Updated}");
    }

    /// <summary>
    /// D179: folds in what installs shared from the app (the relay's list, already stripped of their keys): each one's
    /// counts as <c>data/learning/community/&lt;id&gt;.json</c>, and the undone-change fingerprints enough of them agree on as
    /// <c>data/learning/mistakes/community.json</c> (never one install's own list). Then the brain is rebuilt as for a
    /// publish, and <paramref name="summaryPath"/> says what changed, for whoever approves it.
    /// </summary>
    public static void Community(string inPath, string dir, string brainPath, string summaryPath)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(inPath));
        var subs = new List<ShareLearning.Submission>();
        int refused = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
            if (ShareLearning.Parse(e.GetRawText(), needKey: false) is { } s) subs.Add(s); else refused++;

        Directory.CreateDirectory(dir);
        var own = Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var fold = CommunityLearning.Fold(subs, own);

        string communityDir = Path.Combine(dir, "community");
        if (Directory.Exists(communityDir)) foreach (var f in Directory.GetFiles(communityDir, "*.json")) File.Delete(f);   // replaced whole each time
        Directory.CreateDirectory(communityDir);
        foreach (var (id, apps) in fold.Installs)
            File.WriteAllText(Path.Combine(communityDir, id + ".json"), AppHealth.ToJson(apps));
        string mistakesDir = Path.Combine(dir, "mistakes");
        Directory.CreateDirectory(mistakesDir);
        File.WriteAllText(Path.Combine(mistakesDir, "community.json"),
            System.Text.Json.JsonSerializer.Serialize(new MistakeMemory.File(fold.Mistakes), Indented));

        var old = Brain.Read(brainPath) ?? new BrainFile();
        var next = Rebuild(dir, brainPath, today);
        string summary = CommunityLearning.Summarise(old, next, fold.Installs.Count, refused, fold.SkippedOwn);
        File.WriteAllText(summaryPath, summary);
        Console.WriteLine(summary);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// The brain's profiles and mistakes (D102), rebuilt from every PC's file and every sharing install's (D179), so nothing
    /// is counted twice however often it runs. Its tuning, words, prompt and write rules are kept, and its date changes only
    /// when something in it did.
    /// </summary>
    private static BrainFile Rebuild(string dir, string brainPath, DateOnly today)
    {
        // D103: each PC's counts, by its id, so a PC can leave its own out when it loads the brain; D179: sharing installs as "c-<id>".
        static IEnumerable<(string Id, AppHealth.ProfileFile? File)> Read(string folder, string prefix) =>
            Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.json").Select(f => (Id: prefix + Path.GetFileNameWithoutExtension(f), File: AppHealth.Read<AppHealth.ProfileFile>(f)))
                : [];
        var pcs = Read(dir, "").Concat(Read(Path.Combine(dir, "community"), ShareLearning.CommunityPrefix))
            .Where(p => p.File?.Apps is not null && Brain.IsPcId(p.Id))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(p => p.Id, p => new Dictionary<string, AppStats>(p.File!.Apps, StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var combined = AppHealth.Combine(pcs.Values.Select(v => (IReadOnlyDictionary<string, AppStats>)v), today);

        // D84: the shared list of fingerprints enough PCs agree on.
        string mistakesDir = Path.Combine(dir, "mistakes");
        var shared = MistakeMemory.Combine((Directory.Exists(mistakesDir) ? Directory.GetFiles(mistakesDir, "*.json") : [])
            .Select(AppHealth.Read<MistakeMemory.File>).Where(f => f?.Mistakes is not null).Select(f => (IReadOnlyDictionary<string, int>)f!.Mistakes));

        var old = Brain.Read(brainPath) ?? new BrainFile();
        var next = old with { Apps = combined.Apps, Mistakes = shared.Mistakes, Pcs = pcs };
        if (Brain.ToJson(next with { Updated = "" }) != Brain.ToJson(old with { Updated = "" }))
            next = next with { Updated = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) };
        else next = old;
        string text = Brain.ToJson(next);
        if (Brain.Parse(text) is null) throw new InvalidOperationException("the brain written would not load in the app");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(brainPath))!);
        File.WriteAllText(brainPath, text);
        return next;
    }
}
