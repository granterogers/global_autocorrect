using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.Tray;

namespace GlobalAutocorrect.App.ViewModels;

/// <summary>The accuracy numbers from the last build's metrics.json (6.6, 9.3.g), already formatted for display.</summary>
public sealed record MetricsSummary(string ErrorsFixed, string FalseEdits, string TypicalTime, string Model, string Date, string Caption)
{
    /// <summary>Reads metrics.json; null when the file is missing or isn't a metrics file.</summary>
    public static MetricsSummary? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("errorsFixedRate", out var fixedRate) || !root.TryGetProperty("falseEditRate", out var falseRate)
                || !fixedRate.TryGetDouble(out double fixedValue) || !falseRate.TryGetDouble(out double falseValue))
                return null;

            string latency = root.TryGetProperty("latencyP50Ms", out var p50) && p50.TryGetDouble(out double ms)
                ? $"{Math.Round(ms).ToString("0", CultureInfo.InvariantCulture)} ms" : "—";
            string model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? ModelNames.Friendly(m.GetString()!) : "—";
            string date = root.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String ? FormatDate(d.GetString()!) : "—";
            string set = root.TryGetProperty("set", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "";
            string messages = root.TryGetProperty("messages", out var n) && n.TryGetInt32(out int count)
                ? $"{count} {(set.Length > 0 ? set + " " : "")}test messages" : "test messages";

            return new MetricsSummary(
                Percent(fixedValue), Percent(falseValue), latency, model, date,
                $"Measured on {messages} with {model} on {date}.");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    // Two decimals below 1% (0.19%, not 0.2%): small false-edit rates are the ones people care about.
    private static string Percent(double rate) => (rate * 100).ToString(rate < 0.01 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + "%";

    private static string FormatDate(string iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")) : iso;
}

/// <summary>
/// About (9.3.g, 6.6): version, the measured accuracy, credits, start with Windows (8.1), and updates from GitHub (D58).
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly StartupRegistration _startup;
    private readonly string _noticePath;
    private readonly Action<string> _openFile;
    private readonly IUpdateService? _updates;
    private bool _loading;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private string _updateTitle = "";
    [ObservableProperty] private string _updateCaption = "";
    [ObservableProperty] private string _updateMessage = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))] private bool _installing;
    [ObservableProperty] private string _startupMessage = "";
    [ObservableProperty] private string _noticeMessage = "";

    /// <param name="metricsPath">metrics.json next to the exe; missing is fine.</param>
    /// <param name="noticePath">NOTICE.md with the credits for SCOWL and the Leipzig corpus.</param>
    /// <param name="openFile">Opens a file in the user's default app. Null uses the shell.</param>
    /// <param name="updates">D58: the update check; null when it isn't running (no update is ever shown).</param>
    public AboutViewModel(ISettingsService settings, string metricsPath, string noticePath, StartupRegistration startup, Action<string>? openFile = null,
        IUpdateService? updates = null)
    {
        _settings = settings;
        _updates = updates;
        _startup = startup;
        _noticePath = noticePath;
        _openFile = openFile ?? OpenWithShell;
        Metrics = MetricsSummary.TryLoad(metricsPath);
        Refresh();
    }

    public string AppName => TrayController.AppName;
    public string Channel => "Release candidate";
    /// <summary>The running app's version, such as "0.9.0-rc1" (D58 compares releases with it).</summary>
    public static string InstalledVersion { get; } = ReadVersion();
    public string Version => InstalledVersion;
    public MetricsSummary? Metrics { get; }
    public bool HasMetrics => Metrics is not null;
    public bool HasNoMetrics => Metrics is null;
    public string NoMetricsText => "Not measured in this build";
    public string VersionLine => $"Version {Version} · {Channel}";

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(_settings.Settings with { StartWithWindows = value });
        StartupMessage = _startup.Apply(value) ? "" : "Windows wouldn't let us change this. Your organisation may manage start-up apps.";
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(_settings.Settings with { CheckForUpdates = value });
        if (value) _updates?.CheckNow();
        RefreshUpdate();
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            StartWithWindows = _settings.Settings.StartWithWindows;
            CheckForUpdates = _settings.Settings.CheckForUpdates;
        }
        finally { _loading = false; }
        RefreshUpdate();
    }

    /// <summary>D58: "Update available: vX" only while the switch is on and a newer release was found.</summary>
    private void RefreshUpdate()
    {
        var u = _settings.Settings.CheckForUpdates ? _updates?.Available : null;
        HasUpdate = u is not null;
        Installing = _updates?.Installing == true;
        UpdateTitle = u is null ? "" : $"Update available: v{u.Version}";
        UpdateCaption = u is null ? "" : $"Downloads {Size(u.Zip.Size)}, checks it against the checksum published with it, then closes and restarts Global Autocorrect with the new version.";
        if (u is null) UpdateMessage = "";
    }

    private static string Size(long bytes) => bytes <= 0 ? "the update" : $"about {Math.Max(1, (int)Math.Round(bytes / 1048576.0))} MB";

    private bool CanInstallUpdate() => !Installing;

    /// <summary>D58: only ever from the user's click. Downloads, checks the checksum and runs the new version's installer.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdate()
    {
        if (_updates is null) return;
        UpdateMessage = "Downloading and checking the update…";
        Installing = true;
        string? problem = await _updates.InstallAsync();
        Installing = _updates.Installing;
        UpdateMessage = problem ?? "Installing. Global Autocorrect will close and start again in a moment.";
    }

    [RelayCommand]
    private void OpenNotice()
    {
        if (!File.Exists(_noticePath))
        {
            NoticeMessage = "The credits file wasn't found next to the app.";
            return;
        }
        try
        {
            _openFile(_noticePath);
            NoticeMessage = "";
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            NoticeMessage = "Couldn't open the credits file.";
        }
    }

    /// <summary>The informational version without the build metadata, such as "0.9.0-rc1".</summary>
    private static string ReadVersion()
    {
        var asm = typeof(AboutViewModel).Assembly;
        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(v)) return "0.9.0-rc1";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    private static void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // No app is set up for .md files: Notepad always reads plain text.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
    }
}
