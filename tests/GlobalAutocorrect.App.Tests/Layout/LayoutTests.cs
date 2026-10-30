using System.IO;
using System.Windows;
using System.Windows.Controls;
using GlobalAutocorrect.App.Services;
using GlobalAutocorrect.App.Tests.Settings;
using GlobalAutocorrect.App.Tray;
using GlobalAutocorrect.App.ViewModels;
using GlobalAutocorrect.App.Views;
using GlobalAutocorrect.App.Views.Pages;
using Xunit;

namespace GlobalAutocorrect.App.Tests.Layout;

/// <summary>
/// Every settings page and every first-run step, at the window's default and smallest size, at 100% and 200% display
/// scale, laid out for real and checked (9.5, D63). Each layout is built once and every check runs on it; each test
/// below reports one check over all 40 layouts. LayoutCheckFixtureTests shows each check failing on a broken layout.
/// </summary>
public class LayoutTests
{
    [Fact]
    public void R9_5_EveryPageScrollsToItsLastRow() => AssertNone("Scrolling");

    [Fact]
    public void R9_5_NoTextIsClippedAndNothingHasNoSize() => AssertNone("Clipping");

    [Fact]
    public void R9_5_NoInteractiveControlsOverlap() => AssertNone("Overlaps");

    [Fact]
    public void R9_5_EveryControlHasAnAccessibleName() => AssertNone("Names");

    [Fact]
    public void R9_5_TabReachesEveryControlTopToBottom() => AssertNone("TabOrder");

    [Fact]
    public void R9_5_AllFortyLayoutsWereCheckedWithTheirFullestContent()
    {
        Assert.Equal(20 * Scales.Length, Results.Value.Count);
        // The longest version of each page: an update on offer, words in the dictionary, the download step.
        foreach (var r in Results.Value)
        {
            if (r.Case.Target == "about") Assert.Contains("Update available: v0.9.0-rc2", r.Texts);
            if (r.Case.Target == "dictionary") Assert.Contains("Remove Kubernetes", r.Texts);   // its chip's button
            if (r.Case.Target == "clean-up") Assert.Contains("Cautious", r.Texts);   // a radio button's name
            if (r.Case.Target == "first-run step 2") Assert.Contains("Ready when you are", r.Texts);
        }
    }

    private static void AssertNone(string check)
    {
        var problems = Results.Value.SelectMany(r => r.Problems[check].Select(p => $"{r.Case}: {p}")).ToList();
        Assert.True(problems.Count == 0, $"{problems.Count} problem(s):\n" + string.Join("\n", problems));
    }

    // ---- The layouts --------------------------------------------------------------------------------------------------

    internal sealed record Case(string Target, string SizeName, Size Size, int Percent)
    {
        public override string ToString() => $"{Target} {SizeName} {Size.Width:0}x{Size.Height:0} at {Percent}%";
    }

    private sealed record Result(Case Case, Dictionary<string, List<string>> Problems, HashSet<string> Texts);

    /// <summary>
    /// D70: 100% in the fast tier (its 30 s budget, 11.1.a); GA_LAYOUT_SCALES="100,200" in check.ps1 full adds 200%,
    /// which doubles the layouts built.
    /// </summary>
    private static readonly int[] Scales = (System.Environment.GetEnvironmentVariable("GA_LAYOUT_SCALES") ?? "100")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();

    private static readonly Lazy<List<Result>> Results = new(() =>
    {
        var results = new List<Result>();
        WpfApp.Run(_ =>
        {
            foreach (int percent in Scales)
            {
                foreach (var (sizeName, size) in Sizes(920, 640, 760, 520, percent)) results.AddRange(SettingsPages(sizeName, size, percent));
                foreach (var (sizeName, size) in Sizes(640, 520, 560, 480, percent)) results.AddRange(FirstRunSteps(sizeName, size, percent));
            }
        });
        return results;
    });

    /// <summary>
    /// The default and the smallest size of a window (SettingsWindow.xaml, FirstRunWindow.xaml), as they are at a display
    /// scale. WPF lays out in device-independent pixels (DIPs), and the window's sizes are in DIPs, so a scale doesn't
    /// shrink the window's layout; what it changes is how many DIPs fit on the screen. At 200% a 1920 x 1080 screen with
    /// a 48-pixel taskbar has 960 x 516 DIPs to offer, so the window gets no more than that, and never less than its
    /// minimum, which Windows enforces (D63).
    /// </summary>
    internal static IEnumerable<(string Name, Size Size)> Sizes(double width, double height, double minWidth, double minHeight, int percent)
    {
        double scale = percent / 100.0;
        double screenW = 1920 / scale, screenH = (1080 - 48) / scale;
        yield return ("default", new Size(Math.Max(minWidth, Math.Min(width, screenW)), Math.Max(minHeight, Math.Min(height, screenH))));
        yield return ("small", new Size(minWidth, minHeight));
    }

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

    private static IEnumerable<Result> SettingsPages(string sizeName, Size size, int percent)
    {
        using var s = new FakeServices();
        var env = Environment(s);
        foreach (var w in new[] { "Anwen", "Cwmbran", "Kubernetes", "Llanelli", "Pontypridd", "Rhiwbina", "Tredegar", "Xbox" })
            s.Dictionary.Add(w);
        var window = new SettingsWindow(s, s, env, new UpdateTests.FakeUpdates { Available = UpdateTests.Rc2 });
        window.Navigation.Transition = Wpf.Ui.Animations.Transition.None;
        window.Navigation.TransitionDuration = 0;
        using var host = LayoutHost.Of(window, size, percent / 100.0);

        var results = new List<Result>();
        foreach (var (name, type) in Pages)
        {
            window.Navigation.Navigate(type);
            host.Pump();
            foreach (var sv in LayoutChecks.Descendants(host.Root).OfType<ScrollViewer>()) sv.ScrollToHome();   // the last page scrolled
            host.Pump();
            var page = LayoutChecks.Descendants(host.Root).OfType<Page>().Single(p => p.GetType() == type);
            results.Add(Check(new Case(name, sizeName, size, percent), host, page));
        }
        return results;
    }

    private static IEnumerable<Result> FirstRunSteps(string sizeName, Size size, int percent)
    {
        using var s = new FakeServices();
        var vm = new FirstRunViewModel(s, new FakeInstaller());
        var window = new FirstRunWindow(vm);
        using var host = LayoutHost.Of(window, size, percent / 100.0);

        var results = new List<Result>();
        for (int step = 1; step <= FirstRunViewModel.LastStep; step++)
        {
            // Step 2 is checked as it first appears, with its Download button; the download then lets Next go on.
            if (step == 3) vm.DownloadCommand.Execute(null);
            if (step > 1) vm.NextCommand.Execute(null);
            host.Pump();
            Assert.Equal(step, vm.Step);
            results.Add(Check(new Case($"first-run step {step}", sizeName, size, percent), host, (FrameworkElement)host.Root.Child));
        }
        vm.CancelDownload();
        return results;
    }

    /// <summary>Runs every check on one layout. Scrolling goes last: it moves the page.</summary>
    private static Result Check(Case c, LayoutHost host, FrameworkElement region)
    {
        var rows = RowsOf(region);
        // What is on screen: visible text and the controls' accessible names.
        var texts = LayoutChecks.Descendants(region).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text)
            .Concat(LayoutChecks.Interactive(region).Select(LayoutChecks.AccessibleName)).ToHashSet();
        var problems = new Dictionary<string, List<string>>
        {
            ["Clipping"] = LayoutChecks.Clipping(host, host.Root),
            ["Overlaps"] = LayoutChecks.Overlaps(host, host.Root),
            ["Names"] = LayoutChecks.Names(host.Root),
            ["TabOrder"] = LayoutChecks.TabOrder(host, region),
            ["Scrolling"] = LayoutChecks.Scrolling(host, rows),
        };
        return new Result(c, problems, texts);
    }

    /// <summary>The panel that holds a page's (or first-run step's) title and its rows.</summary>
    internal static FrameworkElement RowsOf(FrameworkElement region)
    {
        var title = LayoutChecks.Descendants(region).OfType<TextBlock>()
            .First(t => t.IsVisible && t.Style == (Style)Application.Current.FindResource("PageTitle"));
        return (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(title);
    }

    /// <summary>The settings window's surroundings, like the screenshot tool's: three models, last build's numbers.</summary>
    private static SettingsEnvironment Environment(FakeServices s)
    {
        string models = Path.Combine(s.Dir, "models");
        Directory.CreateDirectory(models);
        foreach (var f in new[] { "gemma-4-E4B-it-Q5_K_M.gguf", "gemma-4-E2B-it-Q5_K_M.gguf", "Qwen3.5-4B-Q4_K_M.gguf" })
            File.WriteAllBytes(Path.Combine(models, f), []);
        string metrics = Path.Combine(s.Dir, "metrics.json");
        File.WriteAllText(metrics, """{"set":"holdout","messages":200,"model":"gemma-4-E4B-it-Q5_K_M.gguf","date":"2026-10-29","errorsFixedRate":0.952,"falseEditRate":0.004,"latencyP50Ms":346}""");
        return new SettingsEnvironment(models, metrics, Path.Combine(s.Dir, "NOTICE.md"),
            new StartupRegistration(@"Software\GlobalAutocorrectTests\Layout", @"C:\App\x.exe"));
    }
}
