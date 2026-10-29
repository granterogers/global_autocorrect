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
        // Crash record for diagnosis: exception type and stack only, never messages (they could echo text, 8.6).
        AppDomain.CurrentDomain.UnhandledException += (_, a) => WriteCrash(a.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, a) => WriteCrash(a.Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => WriteCrash(a.Exception);

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

    private static void WriteCrash(Exception? ex)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            for (var x = ex; x is not null; x = x.InnerException)
                sb.AppendLine(x.GetType().FullName).AppendLine(x.StackTrace);
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppHost.DataDir, "crash.txt"), $"{DateTime.Now:O}\n{sb}\n");
        }
        catch (System.IO.IOException) { }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await Host.DisposeAsync();
        _single?.Dispose();
        base.OnExit(e);
    }
}
