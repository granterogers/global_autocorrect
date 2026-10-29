using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace GlobalAutocorrect.App.Models;

/// <summary>Progress for the first-run window (9.4): which step, and bytes done of the whole download.</summary>
public sealed record DownloadProgress(string Step, long Done, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>
/// Downloads the pinned llama.cpp build, CUDA runtime and model in-process, the same way scripts/setup.ps1 does (5.7):
/// SHA-256 checked, resumable, cudart beside llama-server (5.1.a), and llama-server set to the high-performance GPU (5.8.d).
/// </summary>
public sealed class ModelDownloader(string assetsDir, string cacheDir, HttpMessageHandler? handler = null)
{
    private sealed record Item(string Step, string Url, string Sha256, string Dest, long Size);

    /// <summary>True when everything the app needs to run on the GPU is already in the cache.</summary>
    public bool IsInstalled(string modelFile)
    {
        var m = Manifest();
        string build = m.GetProperty("llama").GetProperty("build").GetString()!;
        return File.Exists(Path.Combine(cacheDir, "llama", build, "cuda", "llama-server.exe"))
            && File.Exists(Path.Combine(cacheDir, "models", modelFile));
    }

    public async Task InstallAsync(string modelKey, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var m = Manifest();
        var llama = m.GetProperty("llama");
        string build = llama.GetProperty("build").GetString()!;
        string dl = Path.Combine(cacheDir, "downloads");
        var model = m.GetProperty("models").GetProperty(modelKey);

        using var http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = true }, disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
        var items = new List<Item>();
        foreach (var (step, key) in new[] { ("AI engine", "cuda"), ("GPU runtime", "cudart"), ("CPU fallback", "cpu") })
        {
            var e = llama.GetProperty(key);
            string url = e.GetProperty("url").GetString()!;
            items.Add(new Item(step, url, e.GetProperty("sha256").GetString()!, Path.Combine(dl, Path.GetFileName(new Uri(url).LocalPath)), 0));
        }
        items.Add(new Item("Language model", model.GetProperty("url").GetString()!, model.GetProperty("sha256").GetString()!,
            Path.Combine(cacheDir, "models", model.GetProperty("file").GetString()!), 0));

        // Sizes first, so the progress bar covers the whole install.
        for (int i = 0; i < items.Count; i++)
        {
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, items[i].Url), ct);
            items[i] = items[i] with { Size = head.Content.Headers.ContentLength ?? 0 };
        }
        long total = items.Sum(i => i.Size), before = 0;

        foreach (var item in items)
        {
            long baseDone = before;
            await DownloadVerified(http, item, done => progress.Report(new DownloadProgress(item.Step, baseDone + done, total)), ct);
            before += item.Size;
        }

        progress.Report(new DownloadProgress("Unpacking", total, total));
        string cudaDir = Path.Combine(cacheDir, "llama", build, "cuda");
        ZipFile.ExtractToDirectory(items[0].Dest, cudaDir, overwriteFiles: true);
        ZipFile.ExtractToDirectory(items[1].Dest, cudaDir, overwriteFiles: true);   // 5.1.a: same folder
        ZipFile.ExtractToDirectory(items[2].Dest, Path.Combine(cacheDir, "llama", build, "cpu"), overwriteFiles: true);
        SetHighPerformanceGpu(Path.Combine(cudaDir, "llama-server.exe"));   // 5.8.d
        progress.Report(new DownloadProgress("Ready", total, total));
    }

    private static async Task DownloadVerified(HttpClient http, Item item, Action<long> report, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.Dest)!);
        if (File.Exists(item.Dest) && await Sha256(item.Dest, ct) == item.Sha256) { report(item.Size); return; }

        string part = item.Dest + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);   // resume after an interruption
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (have > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0;
        resp.EnsureSuccessStatusCode();

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            long done = have;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                report(done);
            }
        }
        if (await Sha256(part, ct) != item.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException($"The {item.Step.ToLowerInvariant()} download was damaged. Please try again.");
        }
        File.Move(part, item.Dest, overwrite: true);
    }

    private static async Task<string> Sha256(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
    }

    private static void SetHighPerformanceGpu(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        key.SetValue(exe, "GpuPreference=2;");
    }

    private JsonElement Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(assetsDir, "manifest.json"))).RootElement;
}
