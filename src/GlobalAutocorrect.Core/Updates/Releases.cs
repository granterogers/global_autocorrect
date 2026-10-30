using System.Text.Json;

namespace GlobalAutocorrect.Core.Updates;

/// <summary>A file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>One GitHub release, as far as the update check needs it.</summary>
public sealed record Release(string Tag, bool Prerelease, bool Draft, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>A newer version the user can install: its zip, and the checksum file published next to it (D58).</summary>
public sealed record AvailableUpdate(ReleaseVersion Version, string Tag, ReleaseAsset Zip, ReleaseAsset? Sums);

/// <summary>
/// D58: which GitHub release, if any, to offer. Only this repository's release assets are used; a pre-release is only
/// offered to someone already running a pre-release; and never a version equal to or older than the one installed.
/// </summary>
public static class Releases
{
    public const string Repository = "granterogers/global_autocorrect";
    public const string SumsFile = "SHA256SUMS.txt";

    /// <summary>The newest releases first, pre-releases included (the "latest" endpoint leaves them out).</summary>
    public static readonly Uri ApiUrl = new($"https://api.github.com/repos/{Repository}/releases?per_page=20");

    private const string AssetPrefix = "/" + Repository + "/releases/download/";

    /// <summary>True only for https://github.com/granterogers/global_autocorrect/releases/download/… URLs.</summary>
    public static bool IsOurAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && u.IsDefaultPort
        && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(u.UserInfo)
        && u.AbsolutePath.StartsWith(AssetPrefix, StringComparison.Ordinal)
        && !u.AbsolutePath.Contains("/../", StringComparison.Ordinal);

    /// <summary>The zip the release workflow publishes for a version (13.1, D35).</summary>
    public static string ZipName(ReleaseVersion v) => $"GlobalAutocorrect-{v}.zip";

    /// <summary>Reads the GitHub REST API's list of releases. Anything unexpected is skipped, never thrown.</summary>
    public static IReadOnlyList<Release> Parse(string json)
    {
        var list = new List<Release>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
                var assets = new List<ReleaseAsset>();
                if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in a.EnumerateArray())
                    {
                        if (x.ValueKind == JsonValueKind.Object
                            && x.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                            && x.TryGetProperty("browser_download_url", out var url) && url.ValueKind == JsonValueKind.String)
                        {
                            long size = x.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                            assets.Add(new ReleaseAsset(name.GetString()!, url.GetString()!, size));
                        }
                    }
                }
                list.Add(new Release(tag.GetString()!, Flag(r, "prerelease"), Flag(r, "draft"), assets));
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The newest release worth offering: newer than <paramref name="installed"/>, not a draft, a pre-release only when
    /// the installed version is one, and with its zip at one of this repository's release URLs.
    /// </summary>
    public static AvailableUpdate? Choose(IEnumerable<Release> releases, ReleaseVersion installed)
    {
        AvailableUpdate? best = null;
        foreach (var r in releases)
        {
            if (r.Draft || !ReleaseVersion.TryParse(r.Tag, out var v)) continue;
            if ((r.Prerelease || v.IsPreRelease) && !installed.IsPreRelease) continue;
            if (v <= installed || (best is not null && v <= best.Version)) continue;
            var zip = r.Assets.FirstOrDefault(a => a.Name == ZipName(v) && IsOurAssetUrl(a.Url));
            if (zip is null) continue;
            var sums = r.Assets.FirstOrDefault(a => a.Name == SumsFile && IsOurAssetUrl(a.Url));
            best = new AvailableUpdate(v, r.Tag, zip, sums);
        }
        return best;
    }

    /// <summary>
    /// Reads a <c>sha256sum</c>-style file: one "<c>hex  name</c>" per line (a "*" before the name is allowed).
    /// File name → lower-case hex digest.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseSums(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            int sp = line.IndexOfAny([' ', '\t']);
            if (sp != 64) continue;
            var hex = line[..64];
            if (!hex.All(char.IsAsciiHexDigit)) continue;
            var name = line[sp..].Trim().TrimStart('*');
            if (name.Length > 0) d[name] = hex.ToLowerInvariant();
        }
        return d;
    }
}
