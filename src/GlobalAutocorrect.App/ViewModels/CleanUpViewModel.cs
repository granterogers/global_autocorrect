using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.Core.Settings;

namespace GlobalAutocorrect.App.ViewModels;

/// <summary>One entry in the model drop-down: the file name we store and the friendly name we show.</summary>
public sealed record ModelChoice(string File, string Display);

/// <summary>Clean-up (9.3.b): pause length, how bold corrections are, whole paragraph and look-back (D52), underline on/off,
/// when the AI frees its memory, model.</summary>
public sealed partial class CleanUpViewModel : ObservableObject
{
    public const int MinPauseMs = 300;
    public const int MaxPauseMs = 2000;
    public const int PauseStepMs = 100;

    private readonly ISettingsService _settings;
    private readonly string _modelsDir;
    private bool _loading;

    [ObservableProperty] private double _pauseMs = 700;
    [ObservableProperty] private bool _underlineEnabled = true;
    [ObservableProperty] private Boldness _boldness = Boldness.Standard;
    [ObservableProperty] private ModelChoice? _selectedModel;
    [ObservableProperty] private bool _fixWholeParagraph = true;
    [ObservableProperty] private bool _lookBackWhenPaused = true;
    [ObservableProperty] private int _idleUnloadMinutes = 15;

    /// <summary>D52: when the AI model is unloaded after you stop typing, freeing about 4 GB of memory.</summary>
    public IReadOnlyList<int> IdleUnloadChoices { get; } = [5, 15, 30, 60];

    /// <summary>"Cautious" corrects only what you type yourself, so these two switches are off then.</summary>
    public bool BoldEnoughForParagraphSwitches => Boldness != Boldness.Cautious;

    public ObservableCollection<ModelChoice> Models { get; } = [];

    /// <param name="modelsDir">Where the GGUF files live: %LOCALAPPDATA%\GlobalAutocorrect\cache\models.</param>
    public CleanUpViewModel(ISettingsService settings, string modelsDir)
    {
        _settings = settings;
        _modelsDir = modelsDir;
        Refresh();
    }

    /// <summary>Keeps the pause inside 300–2000 ms in 100 ms steps (9.3.b).</summary>
    public static int NormalisePause(double ms)
    {
        if (double.IsNaN(ms)) return 700;
        double stepped = Math.Round(ms / PauseStepMs, MidpointRounding.AwayFromZero) * PauseStepMs;
        return (int)Math.Clamp(stepped, MinPauseMs, MaxPauseMs);
    }

    public string PauseLabel => (NormalisePause(PauseMs) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " seconds";

    public bool IsCautious { get => Boldness == Boldness.Cautious; set { if (value) Boldness = Boldness.Cautious; } }
    public bool IsStandard { get => Boldness == Boldness.Standard; set { if (value) Boldness = Boldness.Standard; } }
    public bool IsBold { get => Boldness == Boldness.Bold; set { if (value) Boldness = Boldness.Bold; } }

    partial void OnPauseMsChanged(double value)
    {
        int ms = NormalisePause(value);
        if (Math.Abs(ms - value) > 0.001) { PauseMs = ms; return; }   // snap, then save once
        OnPropertyChanged(nameof(PauseLabel));
        if (!_loading) _settings.Update(_settings.Settings with { PauseMs = ms });
    }

    partial void OnBoldnessChanged(Boldness value)
    {
        OnPropertyChanged(nameof(IsCautious));
        OnPropertyChanged(nameof(IsStandard));
        OnPropertyChanged(nameof(IsBold));
        OnPropertyChanged(nameof(BoldEnoughForParagraphSwitches));
        if (!_loading) _settings.Update(_settings.Settings with { Boldness = value });
    }

    partial void OnFixWholeParagraphChanged(bool value)
    {
        if (!_loading) _settings.Update(_settings.Settings with { FixWholeParagraph = value });
    }

    partial void OnLookBackWhenPausedChanged(bool value)
    {
        if (!_loading) _settings.Update(_settings.Settings with { LookBackWhenPaused = value });
    }

    partial void OnIdleUnloadMinutesChanged(int value)
    {
        if (!_loading) _settings.Update(_settings.Settings with { IdleUnloadMinutes = value });
    }

    partial void OnUnderlineEnabledChanged(bool value)
    {
        if (!_loading) _settings.Update(_settings.Settings with { UnderlineEnabled = value });
    }

    partial void OnSelectedModelChanged(ModelChoice? value)
    {
        // Takes effect after a restart: the model host starts once, at launch.
        if (!_loading && value is not null) _settings.Update(_settings.Settings with { Model = value.File });
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            var s = _settings.Settings;
            PauseMs = NormalisePause(s.PauseMs);
            UnderlineEnabled = s.UnderlineEnabled;
            Boldness = s.Boldness;
            FixWholeParagraph = s.FixWholeParagraph;
            LookBackWhenPaused = s.LookBackWhenPaused;
            IdleUnloadMinutes = IdleUnloadChoices.Contains(s.IdleUnloadMinutes) ? s.IdleUnloadMinutes : 15;
            LoadModels(s.Model);
        }
        finally { _loading = false; }
    }

    private void LoadModels(string current)
    {
        var files = new List<string>();
        try
        {
            if (Directory.Exists(_modelsDir))
                files.AddRange(Directory.EnumerateFiles(_modelsDir, "*.gguf").Select(Path.GetFileName).OfType<string>());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var choices = files
            .OrderBy(f => f == ModelNames.Recommended ? 0 : 1).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ModelChoice(f, ModelNames.Friendly(f))).ToList();
        if (!string.IsNullOrWhiteSpace(current) && choices.All(c => !c.File.Equals(current, StringComparison.OrdinalIgnoreCase)))
            choices.Insert(0, new ModelChoice(current, ModelNames.Friendly(current) + " (not downloaded yet)"));

        if (!choices.SequenceEqual(Models))
        {
            Models.Clear();
            foreach (var c in choices) Models.Add(c);
        }
        var selected = Models.FirstOrDefault(c => c.File.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (!Equals(SelectedModel, selected)) SelectedModel = selected;
    }
}
