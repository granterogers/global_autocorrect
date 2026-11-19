using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GlobalAutocorrect.App.Services;

namespace GlobalAutocorrect.App.ViewModels;

/// <summary>Prediction (9.3.c, 7.7): on or off, and (D80) whether to guess the next word almost always.</summary>
public sealed partial class PredictionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private bool _loading;

    [ObservableProperty] private bool _predictionEnabled;
    [ObservableProperty] private bool _alwaysSuggestNextWord = true;

    // D142: phrases learned from the user's repeats: on or off, and each one visible and removable.
    [ObservableProperty] private bool _learnPhrases = true;
    [ObservableProperty] private string _phrasesText = "";
    public ObservableCollection<string> Phrases { get; } = [];

    partial void OnLearnPhrasesChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(_settings.Settings with { LearnPhrases = value });
    }

    [RelayCommand]
    private void Remove(string? phrase)
    {
        if (phrase is null || _settings.Phrases is not { } m) return;
        m.Forget(phrase);
        _settings.SavePhrases();
        ReloadPhrases();
    }

    [RelayCommand]
    private void ForgetAll()
    {
        if (_settings.Phrases is not { } m) return;
        m.ForgetAll();
        _settings.SavePhrases();
        ReloadPhrases();
    }

    private void ReloadPhrases()
    {
        Phrases.Clear();
        var kept = _settings.Phrases?.Kept ?? [];
        foreach (var (phrase, _) in kept) Phrases.Add(phrase);
        PhrasesText = kept.Count == 0
            ? "None yet. A phrase appears here once you've typed it three times, on two different days."
            : kept.Count == 1 ? "1 phrase learnt" : $"{kept.Count} phrases learnt";
    }

    public PredictionViewModel(ISettingsService settings)
    {
        _settings = settings;
        Refresh();
    }

    partial void OnPredictionEnabledChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(_settings.Settings with { PredictionEnabled = value });
    }

    partial void OnAlwaysSuggestNextWordChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(_settings.Settings with { AlwaysSuggestNextWord = value });
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            PredictionEnabled = _settings.Settings.PredictionEnabled;
            AlwaysSuggestNextWord = _settings.Settings.AlwaysSuggestNextWord;
            LearnPhrases = _settings.Settings.LearnPhrases;
            ReloadPhrases();
        }
        finally { _loading = false; }
    }
}
