using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlobalAutocorrect.Core.Health;

/// <summary>
/// D84, user request "if it happens once, it never happens again". Changes the app made that turned out to be mistakes
/// (the user undid them or typed the original back over them, or the write guard caught them) are remembered, and the
/// same change is never made again. Each is kept as a fingerprint: the first 16 hex digits of SHA-256 over a fixed prefix
/// and the whole words before and after, lower case. The words themselves are not stored (8.6), so the file and what is
/// published from it say nothing readable about what was typed; matching a new change against it needs the words.
/// Shipped fingerprints (<c>assets/mistakes.json</c>) are those that at least <see cref="ShareThreshold"/> PCs, or one PC
/// twice, published (scripts/publish-learning.ps1), so one user's taste doesn't block a fix for everyone.
/// </summary>
public sealed class MistakeMemory
{
    public const int ShareThreshold = 2;
    private const string Prefix = "ga-mistake-v1|";

    private readonly Lock _lock = new();
    private readonly string? _path;
    private Dictionary<string, int> _local = new(StringComparer.Ordinal);
    private HashSet<string> _shipped = new(StringComparer.Ordinal);
    private bool _dirty;

    public MistakeMemory(string? path = null) => _path = path;

    /// <summary>The fingerprint of changing <paramref name="before"/> into <paramref name="after"/> (whole words).</summary>
    public static string Fingerprint(string before, string after)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Prefix + before.Trim().ToLowerInvariant() + "→" + after.Trim().ToLowerInvariant()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>True when this change was a mistake here before, or on enough other PCs.</summary>
    public bool IsKnown(string before, string after)
    {
        string f = Fingerprint(before, after);
        lock (_lock) return _local.ContainsKey(f) || _shipped.Contains(f);
    }

    /// <summary>Remembers a change as a mistake. Returns true the first time.</summary>
    public bool Remember(string before, string after)
    {
        if (string.Equals(before.Trim(), after.Trim(), StringComparison.Ordinal)) return false;
        string f = Fingerprint(before, after);
        lock (_lock)
        {
            _dirty = true;
            if (_local.TryGetValue(f, out int n)) { _local[f] = n + 1; return false; }
            _local[f] = 1;
            return true;
        }
    }

    public int Count { get { lock (_lock) return _local.Count; } }

    /// <summary>This PC's fingerprints and how often each happened, for publishing.</summary>
    public IReadOnlyDictionary<string, int> Local { get { lock (_lock) return new Dictionary<string, int>(_local); } }

    // ---- files ------------------------------------------------------------------------------------------------------

    public sealed record File(Dictionary<string, int> Mistakes);

    public void Load(string? shippedPath = null)
    {
        lock (_lock)
        {
            if (_path is not null && AppHealth.Read<File>(_path) is { } f) _local = new(f.Mistakes, StringComparer.Ordinal);
            if (shippedPath is not null && AppHealth.Read<File>(shippedPath) is { } s) _shipped = new(s.Mistakes.Keys, StringComparer.Ordinal);
        }
    }

    /// <summary>D102: takes a new brain's shared fingerprints while running. This PC's own are kept.</summary>
    public void UseShipped(IEnumerable<string> fingerprints)
    {
        var s = new HashSet<string>(fingerprints, StringComparer.Ordinal);
        lock (_lock) _shipped = s;
    }

    public void Save()
    {
        if (_path is null) return;
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(new File(new(_local)), new JsonSerializerOptions { WriteIndented = true });
            _dirty = false;
        }
        try { System.IO.File.WriteAllText(_path, json); }
        catch (IOException) { _dirty = true; }
        catch (UnauthorizedAccessException) { _dirty = true; }
    }

    /// <summary>
    /// D84: the shipped list from every PC's published fingerprints: kept when <see cref="ShareThreshold"/> PCs published
    /// it, or one PC saw it that often. The value is how many PCs.
    /// </summary>
    public static File Combine(IEnumerable<IReadOnlyDictionary<string, int>> pcs)
    {
        var pcsWith = new Dictionary<string, int>(StringComparer.Ordinal);
        var times = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pc in pcs)
            foreach (var (f, n) in pc)
            {
                pcsWith[f] = pcsWith.GetValueOrDefault(f) + 1;
                times[f] = Math.Max(times.GetValueOrDefault(f), n);
            }
        return new File(pcsWith.Where(p => p.Value >= ShareThreshold || times[p.Key] >= ShareThreshold)
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }
}
