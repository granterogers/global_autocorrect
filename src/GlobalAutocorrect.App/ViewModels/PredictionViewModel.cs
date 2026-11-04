using CommunityToolkit.Mvvm.ComponentModel;
using GlobalAutocorrect.App.Services;

namespace GlobalAutocorrect.App.ViewModels;

/// <summary>Prediction (9.3.c, 7.7): on or off, and (D80) whether to guess the next word almost always.</summary>
public sealed partial class PredictionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private bool _loading;

    [ObservableProperty] private bool _predictionEnabled;
    [ObservableProperty] private bool _alwaysSuggestNextWord = true;

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
        }
        finally { _loading = false; }
    }
}
