using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GlobalAutocorrect.App.Tests;

/// <summary>
/// The one WPF Application a test process may have: the real App (App.xaml's resources, OnStartup never called, so
/// nothing starts) on its own STA thread. Tests that need WPF run their code on it; an exception is rethrown to the test.
/// </summary>
internal static class WpfApp
{
    /// <summary>
    /// Before any test runs: the app's data folder is a temp folder, never the user's %APPDATA%. The real App's crash
    /// handler writes there, and these tests once filled the user's crash.txt with their own records.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void UseTempDataDir() =>
        Environment.SetEnvironmentVariable("GA_DATA_DIR", Path.Combine(Path.GetTempPath(), "ga-app-tests-" + Environment.ProcessId));

    private static readonly Lazy<App> Instance = new(Start);

    public static void Run(Action<App> body) => Instance.Value.Dispatcher.Invoke(() => body(Instance.Value));

    private static App Start()
    {
        App? app = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            // WPF-UI queues a load of its text-box context menu style that fails outside GlobalAutocorrect.exe ("The URI
            // prefix is not recognized"); the real app never hits it. Only that one is set aside.
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
                e.Handled = e.Exception is NotSupportedException && e.Exception.StackTrace?.Contains("ContextMenuLoader") == true;
            app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return app!;
    }
}
