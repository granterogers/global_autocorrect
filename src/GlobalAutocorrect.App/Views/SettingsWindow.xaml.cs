using System.Windows;
using System.Windows.Threading;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.ViewModels;
using GlobalAutocorrect.App.Views.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace GlobalAutocorrect.App.Views;

/// <summary>
/// The settings window (9.1, 9.3): a Mica FluentWindow with a NavigationView on the left. Every change is saved at once
/// through <see cref="ISettingsService"/>; the window only keeps itself up to date while it is open.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModels _viewModels;
    private readonly UiContext _ui = new();
    private readonly DispatcherTimer _timer;

    public SettingsWindow(ISettingsService settings, IStatusSource status, SettingsEnvironment? environment = null)
    {
        _viewModels = new SettingsViewModels(settings, status, environment ?? SettingsEnvironment.Default());

        InitializeComponent();
        RootNavigation.SetPageProviderService(new PageProvider(_viewModels));

        // Live numbers on Home: refresh on host events plus a 2 s timer while the window is open (9.3.a).
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => _viewModels.RefreshLive();
        status.StateChanged += OnStateChanged;
        settings.Changed += OnStateChanged;

        Loaded += (_, _) =>
        {
            _timer.Start();
            if (RootNavigation.SelectedItem is null) NavigateTo<HomePage>();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            status.StateChanged -= OnStateChanged;
            settings.Changed -= OnStateChanged;
            _viewModels.Dispose();
        };
    }

    /// <summary>The navigation control; the screenshot tool uses it to show each page.</summary>
    public NavigationView Navigation => RootNavigation;

    public void NavigateTo<TPage>() where TPage : class => RootNavigation.Navigate(typeof(TPage));

    private void OnStateChanged() => _ui.Post(() => _viewModels.RefreshSettings());

    private sealed class PageProvider(SettingsViewModels vm) : INavigationViewPageProvider
    {
        public object? GetPage(Type pageType) => pageType switch
        {
            _ when pageType == typeof(HomePage) => new HomePage(vm.Home),
            _ when pageType == typeof(CleanUpPage) => new CleanUpPage(vm.CleanUp),
            _ when pageType == typeof(PredictionPage) => new PredictionPage(vm.Prediction),
            _ when pageType == typeof(ShortcutsPage) => new ShortcutsPage(vm.Shortcuts),
            _ when pageType == typeof(AppsPage) => new AppsPage(vm.Apps),
            _ when pageType == typeof(DictionaryPage) => new DictionaryPage(vm.Dictionary),
            _ when pageType == typeof(AboutPage) => new AboutPage(vm.About),
            _ => null,
        };
    }
}
