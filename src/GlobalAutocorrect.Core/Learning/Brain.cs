using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Health;

namespace GlobalAutocorrect.Core.Learning;

/// <summary>
/// D102, user request "the brains of the program are adaptable without needing to recompile it ... update this brain
/// online, stored in the code repo ... the binary will frequently look for any updates to the brain". Everything the app
/// learns or is tuned with, as data: <c>brain/brain.json</c> in the GitHub source. A copy ships with each release; the app
/// downloads the latest from the repository's main branch every <see cref="CheckEveryMinutes"/> minutes and applies it
/// while running. Data only: no code, every value checked and held to a safe range, so a bad file can't break the app.
/// Counts and fingerprints only, never typed text (8.6).
/// </summary>
public sealed record BrainFile
{
    /// <summary>The shape of the file. An app only takes a brain whose format it knows (<see cref="Brain.Format"/>).</summary>
    public int Format { get; init; } = Brain.Format;

    /// <summary>When it was last changed, UTC, ISO 8601. Of two brains the later one is used.</summary>
    public string Updated { get; init; } = "";

    /// <summary>D81: per program, how fixes went on every PC that published (counts only).</summary>
    public Dictionary<string, AppStats> Apps { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>D84: fingerprints of changes that were mistakes on enough PCs (never the words).</summary>
    public Dictionary<string, int> Mistakes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Timing and behaviour values (names of <see cref="EngineOptions"/> properties in <see cref="Brain.Knobs"/>).</summary>
    public Dictionary<string, double> Tuning { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per program, how fixes are written there whatever the counts say: "retype" or "arrows".</summary>
    public Dictionary<string, string> Write { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public DateTimeOffset UpdatedAt =>
        DateTimeOffset.TryParse(Updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : DateTimeOffset.MinValue;
}

public static class Brain
{
    public const int Format = 1;

    /// <summary>Where the latest brain is read from: this repository's main branch, nowhere else.</summary>
    public static readonly Uri Url = new("https://raw.githubusercontent.com/granterogers/global_autocorrect/main/brain/brain.json");

    public const int CheckEveryMinutes = 15;

    /// <summary>A brain bigger than this is refused (the real one is a few KB).</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The values a brain may change, each held to its range. User settings (pause, boldness, what's on) are never here:
    /// they are the user's, and are applied after the brain's.
    /// </summary>
    public static IReadOnlyDictionary<string, (int Min, int Max, Func<EngineOptions, int, EngineOptions> Set)> Knobs { get; } =
        new Dictionary<string, (int, int, Func<EngineOptions, int, EngineOptions>)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ActiveTypingMs"] = (500, 5000, (o, v) => o with { ActiveTypingMs = v }),
            ["WriteGapMs"] = (50, 1000, (o, v) => o with { WriteGapMs = v }),
            ["WriteDeadlineMs"] = (5000, 120_000, (o, v) => o with { WriteDeadlineMs = v }),
            ["InstantQuietMs"] = (20, 500, (o, v) => o with { InstantQuietMs = v }),
            ["RevisitAfterMs"] = (0, 10_000, (o, v) => o with { RevisitAfterMs = v }),
            ["MaxLineBreaksCrossed"] = (0, 50, (o, v) => o with { MaxLineBreaksCrossed = v }),
            ["ContextSentences"] = (1, 6, (o, v) => o with { ContextSentences = v }),
            ["ShakyAppGapMs"] = (0, 3000, (o, v) => o with { ShakyAppGapMs = v }),
            ["ParagraphsLookedBack"] = (0, 10, (o, v) => o with { ParagraphsLookedBack = v }),
            ["RetypeMaxDistance"] = (0, 1000, (o, v) => o with { RetypeMaxDistance = v }),
        };

    public static readonly IReadOnlySet<string> WriteModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "retype", "arrows" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// A brain this app can use, or null: not JSON, a format it doesn't know (a newer app is needed), or too big. Entries
    /// that don't belong (unknown knobs, malformed fingerprints, unknown write modes, negative counts) are left out.
    /// </summary>
    public static BrainFile? Parse(string json)
    {
        if (json.Length > MaxBytes) return null;
        BrainFile? b;
        try { b = JsonSerializer.Deserialize<BrainFile>(json, Json); }
        catch (JsonException) { return null; }
        if (b is null || b.Format != Format) return null;
        return b with
        {
            Apps = Collect(b.Apps, a => a.Key.Length is > 0 and <= 64 && Sane(a.Value), StringComparer.OrdinalIgnoreCase),
            Mistakes = Collect(b.Mistakes, m => m.Key.Length == 16 && m.Key.All(char.IsAsciiHexDigitLower), StringComparer.Ordinal),
            Tuning = Collect(b.Tuning, t => Knobs.ContainsKey(t.Key) && double.IsFinite(t.Value), StringComparer.OrdinalIgnoreCase),
            Write = Collect(b.Write, w => w.Key.Length is > 0 and <= 64 && w.Value is not null && WriteModes.Contains(w.Value), StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>The entries that pass <paramref name="keep"/>; of two keys that differ only in case, the later wins.</summary>
    private static Dictionary<string, T> Collect<T>(Dictionary<string, T>? from, Func<KeyValuePair<string, T>, bool> keep, StringComparer comparer)
    {
        var d = new Dictionary<string, T>(comparer);
        foreach (var e in from ?? []) if (keep(e)) d[e.Key] = e.Value;
        return d;
    }

    private static bool Sane(AppStats? s) =>
        s is not null && new[] { s.Writes, s.VerifyFailures, s.Stuck, s.Undos, s.ArrowFailures, s.RetypeFailures }.All(v => double.IsFinite(v) && v >= 0);

    public static string ToJson(BrainFile b) => JsonSerializer.Serialize(b, Json);

    /// <summary>Reads a brain file; null if missing, unreadable or unusable.</summary>
    public static BrainFile? Read(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The later of two brains (by <see cref="BrainFile.Updated"/>); <paramref name="a"/> on a tie.</summary>
    public static BrainFile? Later(BrainFile? a, BrainFile? b) =>
        a is null ? b : b is null ? a : b.UpdatedAt > a.UpdatedAt ? b : a;

    /// <summary><paramref name="o"/> with the brain's tuning applied, each value held to its range.</summary>
    public static EngineOptions Tune(EngineOptions o, IReadOnlyDictionary<string, double>? tuning)
    {
        if (tuning is null) return o;
        foreach (var (name, value) in tuning)
        {
            if (!Knobs.TryGetValue(name, out var k) || !double.IsFinite(value)) continue;
            o = k.Set(o, (int)Math.Clamp(Math.Round(value), k.Min, k.Max));
        }
        return o;
    }
}
