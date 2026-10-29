using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GlobalAutocorrect.App.Models;
using GlobalAutocorrect.App.Services;

namespace GlobalAutocorrect.App.ViewModels;

public enum DownloadState { Idle, Downloading, Done, Failed }

/// <summary>One dot in the step indicator at the top of the first-run window.</summary>
public sealed class StepItem(int number, string title) : ObservableObject
{
    private string _state = "Upcoming";
    public int Number { get; } = number;
    public string Title { get; } = title;
    public bool ShowLine => Number > 1;

    /// <summary>"Done", "Current" or "Upcoming": the XAML styles the dot from this.</summary>
    public string State { get => _state; set => SetProperty(ref _state, value); }
}

/// <summary>A shortcut shown as keycap chips on the last step, with what it does.</summary>
public sealed record ShortcutRow(IReadOnlyList<string> Keys, string Description);

/// <summary>
/// First-run window (9.4): what the app does, the model download with progress, and a ready screen with the shortcuts.
/// Finish records FirstRunDone through <see cref="ISettingsService"/>.
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    public const int LastStep = 3;

    private readonly ISettingsService _settings;
    private readonly IModelInstaller _installer;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private DownloadState _state = DownloadState.Idle;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _stepName = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMessage))] private string _message = "";

    public FirstRunViewModel(ISettingsService settings, IModelInstaller installer)
    {
        _settings = settings;
        _installer = installer;
        Steps = [new(1, "About"), new(2, "Download"), new(3, "Ready")];
        if (installer.IsInstalled(settings.Settings.Model)) State = DownloadState.Done;
        // The model went missing after an earlier set-up: go straight to the download.
        Step = settings.Settings.FirstRunDone && State != DownloadState.Done ? 2 : 1;
        UpdateSteps();
    }

    public ObservableCollection<StepItem> Steps { get; }

    /// <summary>Raised after Finish has saved; the window closes itself.</summary>
    public event Action? Finished;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsIdle => State == DownloadState.Idle;
    public bool IsDownloading => State == DownloadState.Downloading;
    public bool IsDone => State == DownloadState.Done;
    public bool IsFailed => State == DownloadState.Failed;
    public bool CanGoBack => Step > 1 && State != DownloadState.Downloading;
    public bool CanGoNext => Step < LastStep && (Step != 2 || State == DownloadState.Done);
    public bool IsLastStep => Step == LastStep;
    public bool HasMessage => Message.Length > 0;

    /// <summary>Pause, undo, Ctrl+Z and Tab (9.4), as keycap chips.</summary>
    public IReadOnlyList<ShortcutRow> ShortcutRows
    {
        get
        {
            var s = _settings.Settings;
            return
            [
                new(Split(s.PauseShortcut), "Pause or resume Global Autocorrect"),
                new(Split(s.UndoShortcut), "Undo the latest correction, from anywhere"),
                new(["Ctrl", "Z"], "Undo a correction straight after it happens"),
                new(["Tab"], "Accept the suggested next word"),
            ];
        }
    }

    partial void OnStepChanged(int value)
    {
        UpdateSteps();
        OnPropertyChanged(nameof(ShortcutRows));
        RaiseNavigation();
    }

    partial void OnStateChanged(DownloadState value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        RaiseNavigation();
    }

    private void RaiseNavigation()
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsLastStep));
    }

    private void UpdateSteps()
    {
        foreach (var s in Steps) s.State = s.Number < Step ? "Done" : s.Number == Step ? "Current" : "Upcoming";
    }

    [RelayCommand]
    private void Next()
    {
        if (CanGoNext) Step++;
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack) Step--;
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (State is DownloadState.Downloading or DownloadState.Done) return;
        State = DownloadState.Downloading;
        Message = "";
        Percent = 0;
        ProgressText = "Getting ready…";
        StepName = "Starting the download…";
        _cts = new CancellationTokenSource();
        try
        {
            await _installer.InstallAsync(new UiProgress<DownloadProgress>(OnProgress), _cts.Token);
            Percent = 100;
            State = DownloadState.Done;
        }
        catch (OperationCanceledException)
        {
            State = DownloadState.Idle;
            Message = "The download was cancelled. You can start it again whenever you're ready.";
        }
        catch (Exception e)
        {
            State = DownloadState.Failed;
            Message = FriendlyError(e);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Stops a running download, for when the window is closed.</summary>
    public void CancelDownload() => _cts?.Cancel();

    [RelayCommand]
    private void Finish()
    {
        _settings.Update(_settings.Settings with { FirstRunDone = true });
        Finished?.Invoke();
    }

    private void OnProgress(DownloadProgress p)
    {
        if (State != DownloadState.Downloading) return;
        Percent = Math.Round(p.Fraction * 100, 1);
        ProgressText = FormatSize(p.Done, p.Total);
        StepName = DescribeStep(p.Step);
    }

    /// <summary>"2.1 of 5.6 GB" (sizes in the units Windows shows).</summary>
    public static string FormatSize(long done, long total)
    {
        const double Gb = 1024.0 * 1024 * 1024;
        var c = CultureInfo.InvariantCulture;
        if (total <= 0) return (done / Gb).ToString("0.0", c) + " GB";
        return $"{(done / Gb).ToString("0.0", c)} of {(total / Gb).ToString("0.0", c)} GB";
    }

    public static string DescribeStep(string step) => step switch
    {
        "AI engine" => "Downloading the AI engine…",
        "GPU runtime" => "Downloading the graphics card support…",
        "CPU fallback" => "Downloading the backup engine…",
        "Language model" => "Downloading the language model…",
        "Unpacking" => "Unpacking the files…",
        "Ready" => "Finishing off…",
        _ => "Downloading…",
    };

    /// <summary>A plain-language reason; never the raw exception text, and nothing the user typed (8.6).</summary>
    public static string FriendlyError(Exception e) => e switch
    {
        HttpRequestException => "We couldn't reach the download server. Check your internet connection and try again.",
        InvalidDataException => e.Message,   // "The language model download was damaged. Please try again."
        UnauthorizedAccessException => "Windows wouldn't let us save the files. Check that your user folder is writable and try again.",
        IOException => "We couldn't save the files. Make sure you have about 6 GB of free space and try again.",
        _ => "Something went wrong with the download. Please try again.",
    };

    private static string[] Split(string shortcut) =>
        shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
