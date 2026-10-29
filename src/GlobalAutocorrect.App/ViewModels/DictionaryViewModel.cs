using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GlobalAutocorrect.App.Services;

namespace GlobalAutocorrect.App.ViewModels;

/// <summary>Dictionary (9.3.f, 8.5): the user's own words, with search, add and remove, and the auto-learn switch.</summary>
public sealed partial class DictionaryViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly UiContext _ui = new();
    private bool _loading;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _newWord = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _autoLearn;

    public DictionaryViewModel(ISettingsService settings)
    {
        _settings = settings;
        _settings.Dictionary.Changed += OnDictionaryChanged;
        Refresh();
    }

    /// <summary>The words that match the search box, in alphabetical order.</summary>
    public ObservableCollection<string> Words { get; } = [];
    public bool HasError => Error.Length > 0;

    partial void OnSearchChanged(string value) => Reload();
    partial void OnNewWordChanged(string value) => Error = "";

    partial void OnAutoLearnChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(_settings.Settings with { AutoLearnWords = value });
    }

    private void OnDictionaryChanged() => _ui.Post(Refresh);

    public void Refresh()
    {
        _loading = true;
        try { AutoLearn = _settings.Settings.AutoLearnWords; }
        finally { _loading = false; }
        Reload();
    }

    private void Reload()
    {
        var all = _settings.Dictionary.Words;
        string q = Search.Trim();
        var shown = q.Length == 0 ? all : all.Where(w => w.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!shown.SequenceEqual(Words))
        {
            Words.Clear();
            foreach (var w in shown) Words.Add(w);
        }

        CountText = all.Count switch { 0 => "No words yet", 1 => "1 word", var n => $"{n} words" };
        IsEmpty = Words.Count == 0;
        EmptyText = all.Count == 0
            ? "No words yet. Add one above, or undo a correction and that word is added for you."
            : "No words match your search.";
    }

    [RelayCommand]
    private void Add()
    {
        string word = NewWord.Trim();
        if (word.Length == 0)
        {
            Error = "Type a word to add.";
            return;
        }
        if (_settings.Dictionary.Add(word))
        {
            NewWord = "";
            Reload();
            return;
        }
        Error = _settings.Dictionary.Contains(word)
            ? $"“{word}” is already in your dictionary."
            : "Add one word at a time, without spaces.";
    }

    [RelayCommand]
    private void Remove(string? word)
    {
        if (word is null) return;
        _settings.Dictionary.Remove(word);
        Reload();
    }

    public void Dispose() => _settings.Dictionary.Changed -= OnDictionaryChanged;
}
