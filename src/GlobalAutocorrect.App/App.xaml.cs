using System.Threading;
using System.Windows;
using GlobalAutocorrect.Windows.Targets;

namespace GlobalAutocorrect.App;

public partial class App : Application
{
    private Mutex? _single;

    public AppHost Host { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 5.6.c: only one copy runs.
        _single = SingleInstance.TryAcquire();
        if (_single is null)
        {
            Shutdown();
            return;
        }

        // 10.8: never run elevated (it would also make every normal app refuse our input).
        if (IntegrityLevel.Current > 0x2000)
        {
            MessageBox.Show("Global Autocorrect shouldn't be run as administrator. Please start it normally.",
                "Global Autocorrect", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        await Host.StartAsync();
        OnHostStarted();
        _overlays = new Overlay.OverlayController(Host, Dispatcher);   // 4.7 underline, 7.x suggestions
    }

    private Overlay.OverlayController? _overlays;

    /// <summary>Tray icon and first-run window hook in here (M9).</summary>
    partial void OnHostStartedPartial();
    private void OnHostStarted() => OnHostStartedPartial();

    protected override async void OnExit(ExitEventArgs e)
    {
        await Host.DisposeAsync();
        _single?.Dispose();
        base.OnExit(e);
    }
}
