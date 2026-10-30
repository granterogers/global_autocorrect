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

        // `GlobalAutocorrect.exe --exit` asks the running copy to close cleanly (used by the installer).
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            SingleInstance.SignalExit();
            Shutdown();
            return;
        }

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

        _exitSignal = SingleInstance.CreateExitSignal();
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, -1, executeOnlyOnce: true);

        await Host.StartAsync();
        OnHostStarted();
        _overlays = new Overlay.OverlayController(Host, Dispatcher);   // 4.7 underline, 7.x suggestions
    }

    private Overlay.OverlayController? _overlays;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;

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

    protected override void OnExit(ExitEventArgs e)
    {
        // Wait for the save (history, counts) and the model server to stop. An async OnExit returns at its first
        // await and the process ends there, so nothing after it was guaranteed to run. Off the UI thread, so a
        // continuation that wants the dispatcher can't deadlock; 5 s at most.
        Task.Run(async () => await Host.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
