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

// Renders every settings page and every first-run step, in light and dark, at 100% and 150% scale, to PNG files (9.5):
//   artifacts/screens/<page>-<light|dark>-<100|150>.png      (7 pages + 3 steps = 10 shots x 4 = 40 files)
//   artifacts/screens/extra/...                               (other states, such as a failed download)
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
        string root = args.Length > 0 ? args[0] : FindRoot();
        string outDir = Path.Combine(root, "artifacts", "screens");
        string extraDir = Path.Combine(outDir, "extra");
        Directory.CreateDirectory(extraDir);
        foreach (var old in Directory.GetFiles(outDir, "*.png").Concat(Directory.GetFiles(extraDir, "*.png"))) File.Delete(old);

        string work = Path.Combine(Path.GetTempPath(), "GlobalAutocorrect.Screens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // App.xaml gives us the same resource dictionaries as the real app; OnStartup is never called, so nothing starts.
            var app = new GlobalAutocorrect.App.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var theme = new ThemeService(app);
            var env = MakeEnvironment(root, work);

            int count = 0;
            foreach (var dark in new[] { false, true })
            {
                ApplyTheme(theme, dark);
                string mode = dark ? "dark" : "light";
                string surface = Palette.For(dark).SurfaceBase;
                foreach (int percent in new[] { 100, 150 })
                {
                    double scale = percent / 100.0;

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
                        Save(window, Path.Combine(outDir, $"{name}-{mode}-{percent}.png"), scale, surface);
                        count++;

                        // The smallest size the window allows, to check nothing clips or overlaps (extra/).
                        if (percent == 100)
                        {
                            window.Width = window.MinWidth;
                            window.Height = window.MinHeight;
                            FlushHard(window);
                            Save(window, Path.Combine(extraDir, $"{name}-min-{mode}-100.png"), 1, surface);
                        }
                        window.Close();
                    }

                    // First-run steps. Step 2 is shown as it first appears; the other states go to extra/.
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
                        if (run.Extra && percent != 100) continue;
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
                        string file = run.Extra ? Path.Combine(extraDir, $"{run.Name}-{mode}-{percent}.png") : Path.Combine(outDir, $"{run.Name}-{mode}-{percent}.png");
                        Save(window, file, scale, surface);
                        vm.CancelDownload();
                        window.Close();
                        if (!run.Extra) count++;
                    }
                }
            }

            Console.WriteLine($"Wrote {count} screenshots to {outDir}");
            return count == 40 ? 0 : 2;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\GlobalAutocorrect.Screens", throwOnMissingSubKey: false);
        }
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
    /// Applies light or dark. WPF now and then fails to resolve the pack:// theme dictionary on the very first call
    /// (NotSupportedException "URI prefix is not recognized"); trying again a moment later always works.
    /// </summary>
    private static void ApplyTheme(ThemeService theme, bool dark)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                theme.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
                return;
            }
            catch (NotSupportedException) when (attempt < 5)
            {
                Thread.Sleep(200);
                Flush();
            }
        }
    }

    /// <summary>
    /// Shows a window and puts the wanted theme back. WPF-UI's FluentWindow applies the Windows theme itself once it is
    /// loaded; the real app lets ThemeService follow that, but here light and dark must be chosen by the tool.
    /// </summary>
    private static void Show(Window window, ThemeService theme, bool dark)
    {
        window.Show();
        Flush();
        ApplyTheme(theme, dark);
        Flush();
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

    private static void Save(Window window, string path, double scale, string surfaceHex)
    {
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
