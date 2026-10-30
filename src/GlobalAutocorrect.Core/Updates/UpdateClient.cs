using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace GlobalAutocorrect.Core.Updates;

/// <summary>An update that was refused, with a plain-language reason for the About page.</summary>
public sealed class UpdateRefusedException(string message) : Exception(message);

/// <summary>
/// D58: asks GitHub for the latest release (unauthenticated, nothing about the user is sent), and downloads and checks
/// an update the user chose to install. The checked, extracted folder holds install.ps1; running it is the app's job.
/// </summary>
public sealed class UpdateClient(HttpClient http)
{
    /// <summary>The update check gives up after this; it only ever runs in the background.</summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Largest zip accepted. The app is a few tens of MB; the model is never in the zip.</summary>
    public long MaxZipBytes { get; init; } = 500L * 1024 * 1024;

    /// <summary>A newer release to offer, or null: none, or the check failed (offline, rate-limited, unexpected reply).</summary>
    public async Task<AvailableUpdate?> CheckAsync(ReleaseVersion installed, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(CheckTimeout);
            using var request = Request(Releases.ApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return Releases.Choose(Releases.Parse(json), installed);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return null;   // silently: try again tomorrow
        }
    }

    /// <summary>
    /// Downloads the update's zip into <paramref name="workDir"/> (a new, empty folder), checks its SHA-256 against the
    /// release's SHA256SUMS.txt and extracts it. Returns the folder holding install.ps1. Throws
    /// <see cref="UpdateRefusedException"/> when the checksum file is missing, doesn't list the zip, or doesn't match.
    /// </summary>
    public async Task<string> DownloadAsync(AvailableUpdate update, string workDir, CancellationToken cancellationToken = default)
    {
        if (!Releases.IsOurAssetUrl(update.Zip.Url) || (update.Sums is { } s0 && !Releases.IsOurAssetUrl(s0.Url)))
            throw new UpdateRefusedException("That download isn't from the app's own GitHub releases, so it wasn't used.");
        if (update.Sums is not { } sumsAsset)
            throw new UpdateRefusedException("This release has no checksum file, so the download can't be checked. Nothing was installed.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DownloadTimeout);
        try
        {
            string sumsText;
            using (var request = Request(new Uri(sumsAsset.Url)))
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new UpdateRefusedException("The checksum file couldn't be downloaded, so the update can't be checked. Nothing was installed.");
                sumsText = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            if (!Releases.ParseSums(sumsText).TryGetValue(update.Zip.Name, out var expected))
                throw new UpdateRefusedException("The checksum file doesn't list the download, so it can't be checked. Nothing was installed.");

            Directory.CreateDirectory(workDir);
            string zipPath = Path.Combine(workDir, update.Zip.Name);
            string actual;
            using (var request = Request(new Uri(update.Zip.Url)))
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new UpdateRefusedException("The update couldn't be downloaded. Check your internet connection and try again.");
                await using var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxZipBytes) throw new UpdateRefusedException("The download is far bigger than the app should be, so it was stopped. Nothing was installed.");
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                }
                actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            }

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                TryDelete(zipPath);
                throw new UpdateRefusedException("The download doesn't match its published checksum, so it was deleted and nothing was installed.");
            }

            string extracted = Path.Combine(workDir, "package");
            try
            {
                ZipFile.ExtractToDirectory(zipPath, extracted);   // refuses entries that would land outside the folder
            }
            catch (InvalidDataException)
            {
                throw new UpdateRefusedException("The download couldn't be unpacked. Nothing was installed.");
            }
            if (!File.Exists(Path.Combine(extracted, "install.ps1")))
                throw new UpdateRefusedException("The download has no installer in it. Nothing was installed.");
            return extracted;
        }
        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new UpdateRefusedException("The update couldn't be downloaded. Check your internet connection and try again.");
        }
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, uri);
        r.Headers.UserAgent.Add(new ProductInfoHeaderValue("GlobalAutocorrect", "1"));   // GitHub requires one
        return r;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
