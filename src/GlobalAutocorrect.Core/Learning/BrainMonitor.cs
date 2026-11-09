namespace GlobalAutocorrect.Core.Learning;

/// <summary>One download of the brain: unchanged since <c>ETag</c> (HTTP 304), or its text and new ETag.</summary>
public sealed record BrainDownload(bool NotModified, string? Body, string? ETag);

/// <summary>
/// D102: keeps the running app on the latest brain. At start-up it uses the later of the copy shipped with the app and
/// the last one downloaded; then, while allowed, it downloads <see cref="Brain.Url"/> every few minutes and, when that is
/// a usable brain different from the current one and not older, saves it and raises <see cref="Changed"/>. Never throws
/// from the timer: offline, a refused file or a full disk just keep the current brain.
/// </summary>
/// <param name="cachePath">Where the downloaded brain is kept (user data, survives updates).</param>
/// <param name="shippedPath">The brain that came with this build.</param>
/// <param name="fetch">Downloads the brain, sending the ETag it was given; null when offline or refused.</param>
/// <param name="enabled">Whether checking is allowed now ("Check for updates").</param>
/// <param name="log">One line per change or refusal: dates and counts only.</param>
public sealed class BrainMonitor(
    string cachePath,
    string shippedPath,
    Func<string?, CancellationToken, Task<BrainDownload?>> fetch,
    Func<bool> enabled,
    Action<string>? log = null) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Timer? _timer;
    private int _checking;
    private string? _etag;
    private string? _refusedEtag;

    public BrainFile Current { get; private set; } = new();

    /// <summary>A new brain was downloaded and saved (raised on a background thread).</summary>
    public event Action<BrainFile>? Changed;

    /// <summary>The later of the shipped and the downloaded brain, made current (no <see cref="Changed"/>).</summary>
    public BrainFile LoadLocal()
    {
        Current = Brain.Later(Brain.Read(cachePath), Brain.Read(shippedPath)) ?? new BrainFile();
        return Current;
    }

    public void Start(TimeSpan firstAfter, TimeSpan every) =>
        _timer = new Timer(_ => _ = CheckAsync(), null, firstAfter, every);

    /// <summary>One check. True when a new brain was taken.</summary>
    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        if (!enabled() || Interlocked.Exchange(ref _checking, 1) == 1) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            var d = await fetch(_etag, cts.Token).ConfigureAwait(false);
            if (d is null || d.NotModified || d.Body is null) return false;
            var b = Brain.Parse(d.Body);
            if (b is null)
            {
                if (_refusedEtag != d.ETag || d.ETag is null) log?.Invoke("brain: download refused (unreadable, or needs a newer app) (D102)");
                _refusedEtag = d.ETag;
                return false;
            }
            _etag = d.ETag;
            if (b.UpdatedAt < Current.UpdatedAt || Brain.ToJson(b) == Brain.ToJson(Current)) return false;
            Save(b);
            Current = b;
            log?.Invoke($"brain: updated to {b.Updated} programs={b.Apps.Count} mistakes={b.Mistakes.Count} tuning={b.Tuning.Count} rules={b.Write.Count} (D102)");
            Changed?.Invoke(b);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }   // a background check must never crash the app
        finally { Volatile.Write(ref _checking, 0); }
    }

    /// <summary>Written whole or not at all, so a crash mid-write never leaves half a brain.</summary>
    private void Save(BrainFile b)
    {
        try
        {
            string tmp = cachePath + ".tmp";
            File.WriteAllText(tmp, Brain.ToJson(b));
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // still used until the app closes
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stop.Cancel();
        _stop.Dispose();
    }
}
