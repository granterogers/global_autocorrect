using System.Globalization;
using System.Text;
using GlobalAutocorrect.Core.Health;

namespace GlobalAutocorrect.Core.Learning;

/// <summary>
/// D179: what the GitHub job (<c>.github/workflows/community-learning.yml</c>) makes of the installs' shared learning
/// before anyone approves it: each install's counts kept apart (so an install can leave its own out, D103), and only the
/// undone-change fingerprints that several installs agree on, so one install (or someone pretending to be one) can't stop
/// a fix for everybody.
/// </summary>
public static class CommunityLearning
{
    /// <summary>Installs that must have undone the same change before every PC avoids it.</summary>
    public const int MistakeInstalls = 3;

    /// <summary>
    /// Programs kept per install, its busiest: every install's counts are in the brain by id (D103), and the brain every
    /// PC downloads must stay small (<see cref="Brain.MaxBytes"/>).
    /// </summary>
    public const int ProgramsPerInstall = 25;

    public sealed record Folded(Dictionary<string, AppHealth.ProfileFile> Installs, Dictionary<string, int> Mistakes, int SkippedOwn);

    /// <param name="ownIds">PCs that publish with scripts/publish-learning.ps1: already counted, so their shares are skipped.</param>
    public static Folded Fold(IEnumerable<ShareLearning.Submission> subs, IReadOnlySet<string> ownIds)
    {
        var installs = new Dictionary<string, AppHealth.ProfileFile>(StringComparer.Ordinal);
        var marks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        int own = 0;
        foreach (var s in subs)
        {
            if (ownIds.Contains(s.Id)) { own++; continue; }
            var busiest = s.Apps.Where(a => a.Value.Events > 0).OrderByDescending(a => a.Value.Events).ThenBy(a => a.Key, StringComparer.Ordinal)
                .Take(ProgramsPerInstall).OrderBy(a => a.Key, StringComparer.Ordinal);
            // No date in the file: an unchanged share leaves it unchanged, so nothing waits for approval for nothing.
            installs[s.Id] = new AppHealth.ProfileFile("", busiest.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));
            foreach (var f in s.Mistakes.Keys)
            {
                if (!marks.TryGetValue(f, out var by)) marks[f] = by = new HashSet<string>(StringComparer.Ordinal);
                by.Add(s.Id);
            }
        }
        var agreed = marks.Where(m => m.Value.Count >= MistakeInstalls).OrderBy(m => m.Key, StringComparer.Ordinal)
            .ToDictionary(m => m.Key, m => m.Value.Count, StringComparer.Ordinal);
        return new Folded(installs, agreed, own);
    }

    /// <summary>What the approval would change in every PC's brain, in plain words (the run's summary page).</summary>
    public static string Summarise(BrainFile old, BrainFile next, int installs, int refused, int skippedOwn)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Shared learning waiting for approval (D179)").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Installs sharing: **{installs}**{(refused > 0 ? $" ({refused} refused by the checks)" : "")}{(skippedOwn > 0 ? $"; {skippedOwn} skipped as PCs that publish themselves" : "")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Programs in the brain: {old.Apps.Count} → {next.Apps.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Undone changes every PC avoids: {old.Mistakes.Count} → {next.Mistakes.Count}");

        var changed = new List<string>();
        foreach (var name in old.Apps.Keys.Union(next.Apps.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var a = old.Apps.GetValueOrDefault(name);
            var b = next.Apps.GetValueOrDefault(name);
            if (a is null) changed.Add($"{name} (new: {Line(b!)})");
            else if (b is null) changed.Add($"{name} (left out: too little left to learn from)");
            else if (Differs(a, b)) changed.Add($"{name} ({Line(a)} → {Line(b)})");
        }
        if (changed.Count > 0)
        {
            sb.AppendLine().AppendLine("Programs whose counts changed:");
            foreach (var c in changed.Take(30)) sb.AppendLine("- " + c);
            if (changed.Count > 30) sb.AppendLine(CultureInfo.InvariantCulture, $"- and {changed.Count - 30} more");
        }
        sb.AppendLine().AppendLine("Approve only if these look like real use. The stress runs and the real-model test have already passed with this brain built in.");
        return sb.ToString();
    }

    private static string Line(AppStats s) =>
        string.Create(CultureInfo.InvariantCulture, $"{s.Writes:0.#} written, {s.VerifyFailures + s.ArrowFailures + s.LineFailures + s.PlaceFailures:0.#} failed checks, {s.Undos:0.#} undone");

    private static bool Differs(AppStats a, AppStats b) =>
        Math.Abs(a.Events - b.Events) > Math.Max(2, 0.2 * Math.Max(a.Events, b.Events))
        || Math.Abs(a.Undos - b.Undos) >= 1 || Math.Abs(a.PlaceFailures + a.ArrowFailures + a.LineFailures - (b.PlaceFailures + b.ArrowFailures + b.LineFailures)) >= 1;
}
