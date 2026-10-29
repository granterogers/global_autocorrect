using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.Windows.Targets;

namespace GlobalAutocorrect.App.ViewModels;

/// <summary>Apps (9.3.e, 8.4): the programs Global Autocorrect leaves alone.</summary>
public sealed partial class AppsViewModel : ObservableObject
{
    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly ISettingsService _settings;

    [ObservableProperty] private string _newApp = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty] private string _countText = "";

    public AppsViewModel(ISettingsService settings)
    {
        _settings = settings;
        Refresh();
    }

    public ObservableCollection<string> Apps { get; } = [];
    public bool HasError => Error.Length > 0;
    public bool IsUsingDefaults => _settings.Settings.ExcludedApps is null;

    partial void OnNewAppChanged(string value) => Error = "";

    public void Refresh()
    {
        var current = Sorted(_settings.Settings.ExcludedApps ?? AppExclusions.Defaults);
        if (!current.SequenceEqual(Apps))
        {
            Apps.Clear();
            foreach (var a in current) Apps.Add(a);
        }
        CountText = Apps.Count switch { 0 => "No apps are left alone", 1 => "1 app is left alone", var n => $"{n} apps are left alone" };
        OnPropertyChanged(nameof(IsUsingDefaults));
    }

    [RelayCommand]
    private void Add()
    {
        string name = Normalise(NewApp);
        if (name.Length == 0)
        {
            Error = "Type the name of the program, for example notepad.";
            return;
        }
        if (name.Length > 64 || name.IndexOfAny(InvalidChars) >= 0)
        {
            Error = "That doesn't look like a program name. Use just the name, such as notepad, without a folder.";
            return;
        }
        if (Apps.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Error = $"{name} is already in the list.";
            return;
        }

        Save([.. Apps, name]);
        NewApp = "";
    }

    [RelayCommand]
    private void Remove(string? app)
    {
        if (app is null) return;
        Save(Apps.Where(a => !a.Equals(app, StringComparison.OrdinalIgnoreCase)));
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        _settings.Update(_settings.Settings with { ExcludedApps = null });   // null = the built-in list (8.4)
        Error = "";
        Refresh();
    }

    private void Save(IEnumerable<string> apps)
    {
        _settings.Update(_settings.Settings with { ExcludedApps = Sorted(apps) });
        Error = "";
        Refresh();
    }

    private static List<string> Sorted(IEnumerable<string> apps) =>
        apps.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"Notepad.exe " becomes "Notepad": the process name without the extension.</summary>
    internal static string Normalise(string text)
    {
        string name = text.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        return name;
    }
}
