using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlobalAutocorrect.App.Models;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.Theme;
using GlobalAutocorrect.App.Tray;
using GlobalAutocorrect.App.ViewModels;
using GlobalAutocorrect.App.Views;
using GlobalAutocorrect.App.Views.Pages;
using GlobalAutocorrect.Core.Dictionary;
using GlobalAutocorrect.Core.Settings;
using GlobalAutocorrect.Llm;
using Wpf.Ui.Appearance;

// Renders every settings page and every first-run step, in light and dark, to PNG files (9.5; D62):
//   artifacts/screens/<page>-<light|dark>-<100|125|150|175|200>.png   default window size    (10 shots x 2 x 5 = 100)
//   artifacts/screens/<page>-min-<light|dark>-<100|150>.png           smallest window size   (10 shots x 2 x 2 = 40)
//   artifacts/screens/extra/...                                       other states, such as a failed download (100%)
// Windows are shown far off screen and drawn with RenderTargetBitmap. Mica doesn't render that way, so the tool puts the
// SurfaceBase token colour behind each window instead. Nothing here starts the real app, its keyboard hook or its model.
// Usage: GlobalAutocorrect.Screens [repoRoot]
return ScreenTool.Run(args);

internal static class ScreenTool
{
    private static readonly (string Name, Type Page)[] Pages =
    [
        ("home", typeof(HomePage)),
        ("clean-up", typeof(CleanUpPage)),
        ("prediction", typeof(PredictionPage)),
        ("shortcuts", typeof(ShortcutsPage)),
        ("apps", typeof(AppsPage)),
        ("dictionary", typeof(DictionaryPage)),
        ("about", typeof(AboutPage)),
    ];

    /// <summary>Display scales, in percent, that every window is drawn at (9.5 asks for 100 and 150).</summary>
    private static readonly int[] Scales = [100, 125, 150, 175, 200];

    /// <summary>Scales for the window at its smallest size.</summary>
    private static readonly int[] SmallScales = [100, 150];

    // 7 pages + 3 first-run steps, light and dark, at each scale and at the smallest size.
    private static readonly int Expected = (Pages.Length + 3) * 2 * (Scales.Length + SmallScales.Length);

    public static int Run(string[] args)
    {
        int code = 1;
        var thread = new Thread(() => code = RunCore(args));   // WPF needs an STA thread
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return code;
    }

    private static int RunCore(string[] args)
    {
        // The real App's crash handler writes to the data folder: never the user's %APPDATA% from this tool.
        Environment.SetEnvironmentVariable("GA_DATA_DIR", Path.Combine(Path.GetTempPath(), "GlobalAutocorrect.Screens", "data"));
        string root = args.Length > 0 ? args[0] : FindRoot();
        string outDir = Path.Combine(root, "artifacts", "screens");
        string extraDir = Path.Combine(outDir, "extra");
        Directory.CreateDirectory(extraDir);
        foreach (var old in Directory.GetFiles(outDir, "*.png").Concat(Directory.GetFiles(extraDir, "*.png"))) File.Delete(old);

        string work = Path.Combine(Path.GetTempPath(), "GlobalAutocorrect.Screens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // App.xaml gives us the same resource dictionaries as the real app, without its start-up (see CreateApp).
            var app = CreateApp();
            var theme = new ThemeService(app);
            var env = MakeEnvironment(root, work);

            int count = 0;
            foreach (var dark in new[] { false, true })
            {
                theme.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
                string mode = dark ? "dark" : "light";
                string surface = Palette.For(dark).SurfaceBase;

                // Layout is in device-independent pixels, so each window is laid out once and drawn at every scale.
                foreach (var (name, page) in Pages)
                {
                    var services = new FakeServices(work);
                    var window = new SettingsWindow(services, services, env);
                    Prepare(window);
                    Show(window, theme, dark);
                    window.Navigation.Transition = Wpf.Ui.Animations.Transition.None;
                    window.Navigation.TransitionDuration = 0;
                    window.Navigation.Navigate(page);
                    FlushHard(window);
                    foreach (int percent in Scales)
                        count += Save(window, Path.Combine(outDir, $"{name}-{mode}-{percent}.png"), percent, surface);

                    // The smallest size the window allows, to check nothing clips or overlaps.
                    Shrink(window);
                    foreach (int percent in SmallScales)
                        count += Save(window, Path.Combine(outDir, $"{name}-min-{mode}-{percent}.png"), percent, surface);
                    window.Close();
                }

                // First-run steps. Step 2 is shown as it first appears; the other states go to extra/ (100% only).
                (string Name, int Step, Func<FakeInstaller> Installer, Func<FirstRunViewModel, Task>? Drive, bool Extra)[] runs =
                [
                    ("firstrun-1", 1, () => new FakeInstaller(), null, false),
                    ("firstrun-2", 2, () => new FakeInstaller(), null, false),
                    ("firstrun-3", 3, () => new FakeInstaller { Installed = true }, null, false),
                    ("firstrun-2-downloading", 2, () => new FakeInstaller(), vm => vm.DownloadCommand.ExecuteAsync(null), true),
                    ("firstrun-2-failed", 2, () => new FakeInstaller { Fail = true }, vm => vm.DownloadCommand.ExecuteAsync(null), true),
                    ("firstrun-2-done", 2, () => new FakeInstaller { Installed = true }, null, true),
                ];
                foreach (var run in runs)
                {
                    var services = new FakeServices(work);
                    var installer = run.Installer();
                    var vm = new FirstRunViewModel(services, installer);
                    var window = new FirstRunWindow(vm);
                    Prepare(window);
                    Show(window, theme, dark);
                    for (int step = 1; step < run.Step; step++) vm.NextCommand.Execute(null);
                    // Started inside the dispatcher so the command's continuations come back to this thread.
                    if (run.Drive is not null) Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => _ = run.Drive(vm)));
                    FlushHard(window);
                    if (run.Extra)
                    {
                        Save(window, Path.Combine(extraDir, $"{run.Name}-{mode}-100.png"), 100, surface);
                    }
                    else
                    {
                        foreach (int percent in Scales)
                            count += Save(window, Path.Combine(outDir, $"{run.Name}-{mode}-{percent}.png"), percent, surface);
                        Shrink(window);
                        foreach (int percent in SmallScales)
                            count += Save(window, Path.Combine(outDir, $"{run.Name}-min-{mode}-{percent}.png"), percent, surface);
                    }
                    vm.CancelDownload();
                    window.Close();
                }
            }

            Console.WriteLine($"Wrote {count} screenshots to {outDir} (expected {Expected})");
            return count == Expected ? 0 : 2;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\GlobalAutocorrect.Screens", throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// The real App, with its start-up cancelled (D62). Creating an Application queues its start-up (OnStartup), which runs
    /// the first time the dispatcher pumps, as Flush() does. With the installed app running it found the single-instance
    /// mutex taken and shut this one down, after which every pack:// resource load failed ("The URI prefix is not
    /// recognized"); with no copy running it would have started the real app, keyboard hook and model included. The
    /// operations the constructor queues are aborted, and if start-up ever runs anyway it stops at its first line.
    /// </summary>
    private static GlobalAutocorrect.App.App CreateApp()
    {
        var queued = new List<DispatcherOperation>();
        DispatcherHookEventHandler collect = (_, e) => queued.Add(e.Operation);
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += collect;
        var app = new GlobalAutocorrect.App.App();
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= collect;
        foreach (var op in queued) op.Abort();
        // Raised by base.OnStartup, before the mutex, the hook or the model: throwing ends OnStartup there.
        app.Startup += (_, _) => throw new InvalidOperationException("The screenshot tool must never run the app's start-up.");
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        return app;
    }

    // ---- Windows -------------------------------------------------------------------------------------------------------

    private static void Prepare(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -12000;   // far off screen (not -32000: Windows uses that for minimised windows)
        window.Top = 40;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    /// <summary>
    /// Shows a window and puts the wanted theme back. WPF-UI's FluentWindow applies the Windows theme itself once it is
    /// loaded; the real app lets ThemeService follow that, but here light and dark must be chosen by the tool.
    /// </summary>
    private static void Show(Window window, ThemeService theme, bool dark)
    {
        window.Show();
        Flush();
        theme.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        Flush();
    }

    /// <summary>Takes a window down to the smallest size it allows.</summary>
    private static void Shrink(Window window)
    {
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        FlushHard(window);
    }

    private static void Flush(DispatcherPriority priority = DispatcherPriority.ContextIdle)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Lets layout, bindings, templates and any queued navigation finish before the window is drawn.</summary>
    private static void FlushHard(Window window)
    {
        for (int i = 0; i < 4; i++)
        {
            Flush(DispatcherPriority.Loaded);
            Flush(DispatcherPriority.Render);
            Flush();
            window.UpdateLayout();
        }
        Thread.Sleep(150);
        Flush();
        window.UpdateLayout();
    }

    /// <summary>Draws the window at a display scale (percent) over its surface colour; returns 1, for counting.</summary>
    private static int Save(Window window, string path, int percent, string surfaceHex)
    {
        double scale = percent / 100.0;
        double w = window.ActualWidth, h = window.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Palette.ToColor(surfaceHex)), null, new Rect(0, 0, w, h));
        bitmap.Render(backdrop);
        bitmap.Render(window);
        Console.WriteLine($"  {Path.GetFileName(path)}");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return 1;
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GlobalAutocorrect.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository, or pass the repository root.");
    }

    // ---- Demo data -----------------------------------------------------------------------------------------------------

    private static SettingsEnvironment MakeEnvironment(string root, string work)
    {
        string models = Path.Combine(work, "models");
        Directory.CreateDirectory(models);
        foreach (var f in new[] { "gemma-4-E4B-it-Q5_K_M.gguf", "gemma-4-E2B-it-Q5_K_M.gguf", "Qwen3.5-4B-Q4_K_M.gguf" })
            File.WriteAllBytes(Path.Combine(models, f), []);

        // The last eval's numbers when there are any, otherwise a sample, so the About page always shows its layout.
        string metrics = Path.Combine(work, "metrics.json");
        string[] candidates = [Path.Combine(root, "artifacts", "metrics.json"), Path.Combine(root, "artifacts", "metrics-tuning.json")];
        string? real = candidates.FirstOrDefault(File.Exists);
        if (real is not null) File.Copy(real, metrics);
        else File.WriteAllText(metrics, """{"set":"holdout","messages":200,"model":"gemma-4-E4B-it-Q5_K_M.gguf","date":"2026-10-29","errorsFixedRate":0.952,"falseEditRate":0.004,"latencyP50Ms":346}""");

        return new SettingsEnvironment(models, metrics, Path.Combine(root, "assets", "NOTICE.md"),
            new StartupRegistration(@"Software\GlobalAutocorrect.Screens\Run", @"C:\Screens\GlobalAutocorrect.exe"));
    }
}

/// <summary>Demo values for the status card: model loaded, GPU, 340 ms, 57 corrections.</summary>
internal sealed class FakeServices : ISettingsService, IStatusSource
{
    public FakeServices(string work)
    {
        string file = Path.Combine(work, $"dictionary-{Guid.NewGuid():N}.txt");
        Dictionary = new PersonalDictionary(file);
        foreach (var w in new[] { "Anwen", "Bryn", "Cwmbran", "Geraint", "Kubernetes", "Llanelli", "Maesteg", "Nantgarw", "Pontypridd", "Rhiwbina", "Tredegar", "Xbox", "Zoom" })
            Dictionary.Add(w);
    }

    public AppSettings Settings { get; private set; } = new();
    public PersonalDictionary Dictionary { get; }
    public void Update(AppSettings settings) => Settings = settings;
    public event Action? Changed { add { } remove { } }

    public bool Paused => Settings.Paused;
    public ServerState ModelState => ServerState.Ready;
    public ServerInfo? ModelInfo { get; } = new(true, "CUDA0 (NVIDIA RTX PRO 2000)", 43, 43, false);
    public double TypicalLatencyMs => 340;
    public long CorrectionsToday => 57;
    public event Action? StateChanged { add { } remove { } }
}

/// <summary>Pretends to download: reports 2.1 of 5.6 GB, then waits, or fails with a network error.</summary>
internal sealed class FakeInstaller : IModelInstaller
{
    public bool Installed { get; set; }
    public bool Fail { get; init; }

    public bool IsInstalled(string modelFile) => Installed;

    public async Task InstallAsync(IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        if (Fail) throw new HttpRequestException("demo");
        const long Gb = 1024L * 1024 * 1024;
        progress.Report(new DownloadProgress("Language model", (long)(2.1 * Gb), (long)(5.6 * Gb)));
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
