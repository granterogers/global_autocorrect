using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using GlobalAutocorrect.Core.Health;
using GlobalAutocorrect.Core.Learning;

namespace GlobalAutocorrect.App.Services;

/// <summary>
/// D179: while "Share what it learns" is on and the brain names a relay, sends this install's counts and undone-change
/// fingerprints (<see cref="ShareLearning"/>) once every <see cref="Tunables.ShareEveryHours"/>, the first time
/// <see cref="Tunables.ShareFirstAfterMinutes"/> after start. Off by default; never under the screen tests. A failed send
/// is tried again at the next hourly look. The diagnostics log gets counts and status codes only (8.6).
/// </summary>
public sealed class LearningSharer : IShareService, IDisposable
{
    private readonly Func<bool> _on;
    private readonly Func<BrainShare?> _target;
    private readonly Func<IReadOnlyDictionary<string, AppStats>> _apps;
    private readonly Func<IReadOnlyDictionary<string, int>> _mistakes;
    private readonly Func<Uri, string, CancellationToken, Task<HttpStatusCode>> _post;
    private readonly string _dir;
    private readonly string _version;
    private readonly Action<string> _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly DateTimeOffset _started;
    private Timer? _timer;
    private int _sending;
    private bool _soon;

    public LearningSharer(Func<bool> on, Func<BrainShare?> target, Func<IReadOnlyDictionary<string, AppStats>> apps,
        Func<IReadOnlyDictionary<string, int>> mistakes, string dataDir, string version, Action<string> log,
        Func<Uri, string, CancellationToken, Task<HttpStatusCode>> post, Func<DateTimeOffset>? now = null)
    {
        _on = on; _target = target; _apps = apps; _mistakes = mistakes; _dir = dataDir; _version = version; _log = log; _post = post;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _started = _now();
        LastSent = ReadSent();
    }

    public event Action? Changed;

    public bool Available => _target()?.Address is not null;
    public (DateTimeOffset At, string What)? LastSent { get; private set; }
    public string? Problem { get; private set; }

    private string IdPath => Path.Combine(_dir, "install-id");      // the same id scripts/publish-learning.ps1 uses (D103)
    private string KeyPath => Path.Combine(_dir, "share-key");
    private string SentPath => Path.Combine(_dir, "share-sent");

    /// <summary>Looks every hour whether a send is due.</summary>
    public void Start() => _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

    public void SendSoon()
    {
        _soon = true;
        _timer?.Change(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
    }

    /// <summary>A send is due: the switch is on, there is somewhere to send, start-up is well past and the last one is old enough.</summary>
    public bool Due()
    {
        if (!_on() || !Available) return false;
        var now = _now();
        if (!_soon && now - _started < TimeSpan.FromMinutes(Tunables.ShareFirstAfterMinutes.Value)) return false;
        return LastSent is not { } last || now - last.At >= TimeSpan.FromHours(Tunables.ShareEveryHours.Value);
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!Due() || Interlocked.Exchange(ref _sending, 1) == 1) return;
        try
        {
            var s = Build();
            var status = await _post(_target()!.Address!, ShareLearning.ToJson(s), ct).ConfigureAwait(false);
            _soon = false;
            if ((int)status is >= 200 and < 300)
            {
                LastSent = (_now(), ShareLearning.Describe(s));
                Problem = null;
                WriteSent(LastSent.Value);
                _log($"share: sent {s.Apps.Count} programs and {s.Mistakes.Count} fingerprints");
            }
            else
            {
                // 403: the relay knows this id with another key (the key file was lost): a new id and key next time.
                if (status == HttpStatusCode.Forbidden) { TryDelete(IdPath); TryDelete(KeyPath); }
                Problem = status == HttpStatusCode.TooManyRequests
                    ? "The server is busy. It tries again later."
                    : "The server didn't take it. It tries again later.";
                _log($"share: refused {(int)status}");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Problem = "Couldn't reach the server. It tries again later.";
            _log($"share: failed {e.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
            Changed?.Invoke();
        }
    }

    /// <summary>Exactly what a send now would carry (the id and key are made the first time).</summary>
    public ShareLearning.Submission Build() =>
        ShareLearning.Build(ReadOrMake(IdPath, ShareLearning.IdOk, ShareLearning.NewId), ReadOrMake(KeyPath, k => k.Length >= 32, ShareLearning.NewKey),
            _version, _apps(), _mistakes());

    public string WritePreview()
    {
        string path = Path.Combine(_dir, "share-preview.json");
        File.WriteAllText(path, ShareLearning.ToJson(Build()), Encoding.UTF8);
        return path;
    }

    private static string ReadOrMake(string path, Func<string, bool> ok, Func<string> make)
    {
        string? v = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        if (v is not null && ok(v)) return v;
        v = make();
        File.WriteAllText(path, v);
        return v;
    }

    private (DateTimeOffset, string)? ReadSent()
    {
        try
        {
            if (!File.Exists(SentPath)) return null;
            string[] parts = File.ReadAllText(SentPath).Split('|', 2);
            return DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? (at, parts.Length > 1 ? parts[1] : "") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private void WriteSent((DateTimeOffset At, string What) s)
    {
        try { File.WriteAllText(SentPath, s.At.ToString("O", CultureInfo.InvariantCulture) + "|" + s.What); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _timer?.Dispose();
}
