using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GlobalAutocorrect.Core.Health;

namespace GlobalAutocorrect.Core.Learning;

/// <summary>
/// D179 (user: "as bugs are automatically squashed on their side, improvements are sent to the mother code... I guess this
/// would need approval"). What an install sends when its owner switches on "Share what it learns" on the About page, and
/// the one check every receiver makes (the relay in <c>relay/</c> mirrors it, the GitHub job runs it again):
/// <list type="bullet">
/// <item>per program, the same counts <c>scripts/publish-learning.ps1</c> publishes for the developer's PC (D81): fixes
/// written, failed checks, undos and the like;</item>
/// <item>fingerprints of changes the owner undid (D84): one-way codes of the words, never the words.</item>
/// </list>
/// Plus a random install id, and a random key only the relay sees, so nobody else can send as this install. Never typed
/// text, names, file names or anything about the PC or person (8.6). What is sent is exactly <see cref="ToJson"/>, which
/// the About page shows before and after the switch is on.
/// </summary>
public static partial class ShareLearning
{
    public const int Format = 1;

    /// <summary>In the brain's per-PC counts (D103), installs that share from the app are "c-" and their id.</summary>
    public const string CommunityPrefix = "c-";

    public const int MaxApps = 200;
    public const int MaxMistakes = 5000;
    public const int MaxBytes = 256 * 1024;

    /// <summary>A count far beyond any real one (they fade daily): a bigger one is someone trying to outweigh everyone.</summary>
    public const double MaxCount = 100_000;

    public sealed record Submission(int Format, string Id, string Key, string Version,
        Dictionary<string, AppStats> Apps, Dictionary<string, int> Mistakes);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly PropertyInfo[] Stats = typeof(AppStats).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(double) && p.CanWrite).ToArray();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")] private static partial Regex ProgramName();
    [GeneratedRegex("^[0-9a-f]{16}$")] private static partial Regex Fingerprint();
    [GeneratedRegex("^[0-9a-f]{32,128}$")] private static partial Regex KeyText();
    [GeneratedRegex("^[0-9A-Za-z.+-]{1,32}$")] private static partial Regex VersionText();

    /// <summary>An install id this check accepts: what <see cref="NewId"/> makes, or an older 12-digit one.</summary>
    public static bool IdOk(string? id) => Brain.IsPcId(id) && id!.Length is >= 8 and <= 40 && !id.StartsWith(CommunityPrefix, StringComparison.Ordinal);

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static string NewKey() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// What this install sends: programs with something to learn from (names in lower case), counts rounded as the brain
    /// holds them, and the undone changes' fingerprints. Anything that wouldn't pass <see cref="Parse"/> is left out.
    /// </summary>
    public static Submission Build(string id, string key, string version, IReadOnlyDictionary<string, AppStats> apps,
        IReadOnlyDictionary<string, int> mistakes)
    {
        var kept = new Dictionary<string, AppStats>(StringComparer.Ordinal);
        foreach (var (name, s) in apps.OrderByDescending(a => a.Value.Events).ThenBy(a => a.Key, StringComparer.Ordinal))
        {
            string n = name.ToLowerInvariant();
            if (kept.Count >= MaxApps || !ProgramName().IsMatch(n) || kept.ContainsKey(n) || s.Events <= 0) continue;
            var r = new AppStats();
            foreach (var p in Stats) p.SetValue(r, Math.Round(Math.Clamp((double)p.GetValue(s)!, 0, MaxCount), 2));
            kept[n] = r;
        }
        var marks = mistakes.Where(m => Fingerprint().IsMatch(m.Key) && m.Value > 0)
            .OrderBy(m => m.Key, StringComparer.Ordinal).Take(MaxMistakes)
            .ToDictionary(m => m.Key, m => Math.Min(m.Value, 1000), StringComparer.Ordinal);
        string v = VersionText().IsMatch(version) ? version : "unknown";
        return new Submission(Format, id, key, v, kept, marks);
    }

    public static string ToJson(Submission s) => JsonSerializer.Serialize(s, Json);

    /// <summary>
    /// A submission every part of it checked, or null. Unknown fields, a program or count that doesn't fit, a fingerprint
    /// that isn't one, too many entries or too many bytes all refuse the whole thing: nothing is guessed at.
    /// <paramref name="needKey"/> is false for what the relay hands on (it keeps the key to itself).
    /// </summary>
    public static Submission? Parse(string json, bool needKey = true)
    {
        if (json.Length > MaxBytes) return null;
        Submission? s;
        try { s = JsonSerializer.Deserialize<Submission>(json, Json); }
        catch (JsonException) { return null; }
        if (s is null || s.Format != Format || !IdOk(s.Id) || s.Version is null || !VersionText().IsMatch(s.Version)) return null;
        if (needKey ? s.Key is null || !KeyText().IsMatch(s.Key) : s.Key is not (null or "")) return null;
        if (s.Apps is null || s.Mistakes is null || s.Apps.Count > MaxApps || s.Mistakes.Count > MaxMistakes) return null;
        foreach (var (name, stats) in s.Apps)
        {
            if (!ProgramName().IsMatch(name) || stats is null) return null;
            foreach (var p in Stats)
                if ((double)p.GetValue(stats)! is var v && (!double.IsFinite(v) || v < 0 || v > MaxCount)) return null;
        }
        foreach (var (mark, count) in s.Mistakes)
            if (!Fingerprint().IsMatch(mark) || count is < 1 or > 1000) return null;
        return s with
        {
            Key = s.Key ?? "",
            Apps = new Dictionary<string, AppStats>(s.Apps, StringComparer.OrdinalIgnoreCase),
            Mistakes = new Dictionary<string, int>(s.Mistakes, StringComparer.Ordinal),
        };
    }

    /// <summary>"3 programs and 12 undone changes", for the About page.</summary>
    public static string Describe(Submission s) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{s.Apps.Count} program{(s.Apps.Count == 1 ? "" : "s")} and {s.Mistakes.Count} undone change{(s.Mistakes.Count == 1 ? "" : "s")}");
}
