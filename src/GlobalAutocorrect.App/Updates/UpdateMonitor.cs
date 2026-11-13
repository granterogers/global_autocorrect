using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.Core.Updates;

namespace GlobalAutocorrect.App.Updates;

/// <summary>
/// D58: checks GitHub for a newer release at start-up and then once a day, in the background, while "Check for updates"
/// is on. A newer version is shown on the About page and announced in the tray once per version. Nothing is installed
/// until the user clicks "Install update".
/// </summary>
/// <param name="check">Asks for a newer release; null for none or a failed check (never throws).</param>
/// <param name="download">Downloads and checks an update; returns the folder holding install.ps1.</param>
/// <param name="runInstaller">Starts install.ps1 in that folder.</param>
/// <param name="notify">Shows a tray notification.</param>
/// <param name="onUi">Runs the settings change and notification on the UI thread (settings belong there); inline when null.</param>
public sealed class UpdateMonitor(
    ISettingsService settings,
    Func<CancellationToken, Task<AvailableUpdate?>> check,
    Func<AvailableUpdate, CancellationToken, Task<string>> download,
    Action<string> runInstaller,
    Action<string> notify,
    Action<Action>? onUi = null,
    Func<TimeSpan>? idleFor = null,
    Action<string>? runInstallerQuietly = null) : IUpdateService, IDisposable
{
    public static TimeSpan FirstCheckAfter { get; } = TimeSpan.FromSeconds(20);

    /// <summary>D131: every 3 hours (was once a day, D58), so a fix released by the cloud routine arrives the same day.</summary>
    public static TimeSpan CheckEvery { get; } = TimeSpan.FromHours(3);

    /// <summary>
    /// D131, user request "make things run as automatically as possible … ensure it doesn't interfere with my operation of
    /// the computer": a found update is installed by itself once nobody has touched the keyboard or mouse for this long,
    /// with no window. The app closes and starts again in a few seconds, while nobody is typing.
    /// </summary>
    public static TimeSpan IdleBeforeInstall { get; } = TimeSpan.FromMinutes(10);

    private Timer? _idleTimer;

    private readonly CancellationTokenSource _stop = new();
    private Timer? _timer;
    private int _checking;
    private volatile AvailableUpdate? _available;
    private volatile bool _installing;

    public AvailableUpdate? Available => _available;
    public bool Installing => _installing;
    public event Action? Changed;

    /// <summary>First check shortly after start-up (not to compete with loading), then once a day.</summary>
    public void Start()
    {
        _timer = new Timer(_ => _ = CheckAsync(), null, FirstCheckAfter, CheckEvery);
        if (idleFor is not null) _idleTimer = new Timer(_ => _ = InstallIfIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>D131: installs the update found, quietly, if the setting is on and the PC has been idle long enough.</summary>
    public async Task InstallIfIdleAsync()
    {
        try
        {
            if (_available is null || _installing || idleFor is null || !settings.Settings.InstallUpdatesWhenIdle || !settings.Settings.CheckForUpdates) return;
            if (idleFor() < IdleBeforeInstall) return;
            await InstallAsync(quietly: true).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }   // never crash the app from the background
    }

    public void CheckNow() => _ = Task.Run(CheckAsync);

    /// <summary>One check. Never throws, never on the UI thread when started by the timer or <see cref="CheckNow"/>.</summary>
    public async Task CheckAsync()
    {
        if (!settings.Settings.CheckForUpdates || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var found = await check(_stop.Token).ConfigureAwait(false);
            if (found is null) return;   // nothing newer, or offline: keep what an earlier check found
            _available = found;
            Changed?.Invoke();
            string v = found.Version.ToString();
            (onUi ?? (a => a()))(() =>
            {
                if (settings.Settings.UpdateNotifiedVersion == v) return;
                settings.Update(settings.Settings with { UpdateNotifiedVersion = v });
                notify($"Global Autocorrect {v} is available. Open Settings › About to install it.");
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }   // a background check must never crash the app
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    public Task<string?> InstallAsync() => InstallAsync(quietly: false);

    public async Task<string?> InstallAsync(bool quietly)
    {
        if (_available is not { } update) return "There's no update to install.";
        if (_installing) return "The update is already being downloaded.";
        _installing = true;
        Changed?.Invoke();
        try
        {
            string folder = await Task.Run(() => download(update, _stop.Token)).ConfigureAwait(false);
            if (quietly && runInstallerQuietly is not null) runInstallerQuietly(folder);
            else runInstaller(folder);
            return null;
        }
        catch (UpdateRefusedException e)
        {
            return e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return "The update couldn't be installed. Nothing was changed.";
        }
        finally
        {
            _installing = false;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _idleTimer?.Dispose();
        _stop.Cancel();
        _stop.Dispose();
    }
}

/// <summary>D58: runs a checked update's install.ps1, which closes this app cleanly, replaces it and starts the new one.</summary>
public static class UpdateInstaller
{
    /// <summary>Where updates are downloaded: a new folder in %TEMP% each time.</summary>
    public static string NewWorkFolder() => Path.Combine(Path.GetTempPath(), "GlobalAutocorrect-update-" + Guid.NewGuid().ToString("N"));

    /// <summary>Removes update folders left from earlier installs (best effort).</summary>
    public static void CleanOldWorkFolders()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Path.GetTempPath(), "GlobalAutocorrect-update-*"))
            {
                try { if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(d, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Starts install.ps1 in its own console window, which stays open if the install fails.</summary>
    public static void Run(string folder)
    {
        string script = Path.Combine(folder, "install.ps1");
        string pwsh = FindPwsh() ?? throw new UpdateRefusedException(
            $"Installing needs PowerShell 7, which wasn't found. The checked update is in {folder}: run its installer from there.");
        var psi = new ProcessStartInfo(pwsh) { UseShellExecute = false, WorkingDirectory = folder };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command" }) psi.ArgumentList.Add(a);
        string quoted = "'" + script.Replace("'", "''") + "'";
        psi.ArgumentList.Add($"try {{ & {quoted} }} catch {{ Write-Host $_ -ForegroundColor Red; Read-Host 'The update did not finish. Press Enter to close' }}");
        using var p = Process.Start(psi) ?? throw new UpdateRefusedException("The installer couldn't be started. Nothing was changed.");
    }

    /// <summary>
    /// D131: the same installer with no window (the PC is idle; nobody is there to read it). The outcome goes to
    /// diagnostics.log through the new app's start, and a failed install leaves the running app as it was.
    /// </summary>
    public static void RunQuietly(string folder)
    {
        string script = Path.Combine(folder, "install.ps1");
        string pwsh = FindPwsh() ?? throw new UpdateRefusedException("Installing needs PowerShell 7, which wasn't found.");
        var psi = new ProcessStartInfo(pwsh) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new UpdateRefusedException("The installer couldn't be started. Nothing was changed.");
    }

    private static string? FindPwsh()
    {
        var candidates = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim('"'), "pwsh.exe")); }
            catch (ArgumentException) { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
