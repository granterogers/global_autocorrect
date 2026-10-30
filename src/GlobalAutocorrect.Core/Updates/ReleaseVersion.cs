namespace GlobalAutocorrect.Core.Updates;

/// <summary>
/// D58: an app version such as <c>0.9.0</c> or <c>0.9.0-rc1</c> (a leading "v" and any "+build" part are ignored).
/// Ordered as semantic versions: a pre-release comes before its release (0.9.0-rc1 &lt; 0.9.0-rc2 &lt; 0.9.0), and
/// pre-release labels compare their numbers as numbers, so rc10 comes after rc2.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The part after "-", such as "rc1"; null for a release.</summary>
    public string? PreRelease { get; }

    public bool IsPreRelease => PreRelease is not null;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return false;
        }

        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        var n = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out n[i])) return false;
        }

        version = new ReleaseVersion(n[0], n[1], n[2], pre);
        return true;
    }

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Not a version: {text}");

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;   // the release comes after its pre-releases
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>Dot-separated labels in turn; within a label, runs of digits compare as numbers ("rc2" &lt; "rc10").</summary>
    private static int ComparePreRelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            int c = CompareLabel(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    private static int CompareLabel(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            bool da = char.IsAsciiDigit(a[i]), db = char.IsAsciiDigit(b[j]);
            int si = i, sj = j;
            while (i < a.Length && char.IsAsciiDigit(a[i]) == da) i++;
            while (j < b.Length && char.IsAsciiDigit(b[j]) == db) j++;
            string ra = a[si..i], rb = b[sj..j];
            int c;
            if (da && db)
            {
                ra = ra.TrimStart('0');
                rb = rb.TrimStart('0');
                c = ra.Length != rb.Length ? ra.Length.CompareTo(rb.Length) : string.CompareOrdinal(ra, rb);
            }
            else if (da != db) c = da ? -1 : 1;   // as in semver, numbers come before words
            else c = string.Compare(ra, rb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ReleaseVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, IsPreRelease);
    public override string ToString() => $"{Major}.{Minor}.{Patch}{(PreRelease is null ? "" : "-" + PreRelease)}";

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
}
