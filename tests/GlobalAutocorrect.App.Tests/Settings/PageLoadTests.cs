using System.IO;
using System.Windows;
using GlobalAutocorrect.App.ViewModels;
using GlobalAutocorrect.App.Views.Pages;
using Xunit;

namespace GlobalAutocorrect.App.Tests.Settings;

/// <summary>
/// Builds settings pages from their XAML with the app's own resources. A misspelt icon name, style key or markup
/// extension only fails when the page is opened, so this catches it before the user does (D52: one did).
/// </summary>
public class PageLoadTests
{
    [Fact]
    public void R9_3_b_D52_CleanUpPageLoadsWithItsNewRows()
    {
        var names = new List<string>();
        WpfApp.Run(_ =>
        {
            using var s = new FakeServices();
            var page = new CleanUpPage(new CleanUpViewModel(s, Path.Combine(s.Dir, "models")));
            page.Measure(new Size(900, 2000));
            page.Arrange(new Rect(0, 0, 900, 2000));
            names.AddRange(Find<FrameworkElement>(page).Select(System.Windows.Automation.AutomationProperties.GetName));
        });

        Assert.Contains("Fix the whole paragraph", names);
        Assert.Contains("Look back when you pause", names);
        Assert.Contains("Free the AI's memory after", names);
    }

    [Fact]
    public void R9_3_g_D58_AboutPageLoadsWithItsUpdateRows()
    {
        var names = new List<string>();
        bool shown = false;
        WpfApp.Run(_ =>
        {
            using var s = new FakeServices();
            var updates = new UpdateTests.FakeUpdates { Available = UpdateTests.Rc2 };
            var vm = new AboutViewModel(s, Path.Combine(s.Dir, "metrics.json"), Path.Combine(s.Dir, "NOTICE.md"),
                new GlobalAutocorrect.App.Tray.StartupRegistration(@"Software\GlobalAutocorrectTests\PageLoad", @"C:\App\x.exe"), _ => { }, updates);
            var page = new AboutPage(vm);
            page.Measure(new Size(900, 2000));
            page.Arrange(new Rect(0, 0, 900, 2000));
            // Bindings inside a card's header are attached later, at data-binding priority: let them run.
            var frame = new System.Windows.Threading.DispatcherFrame();
            page.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            var all = Find<FrameworkElement>(page);
            names.AddRange(all.Select(System.Windows.Automation.AutomationProperties.GetName));
            // A card's header isn't in the logical tree: look in each card's header for the title.
            var card = all.OfType<Wpf.Ui.Controls.CardControl>().Single(c => c.Header is FrameworkElement h
                && Find<System.Windows.Controls.TextBlock>(h).Any(t => t.Text == "Update available: v0.9.0-rc2"));
            shown = card.Visibility == Visibility.Visible;
        });

        Assert.Contains("Check for updates", names);
        Assert.Contains("Install update", names);
        Assert.True(shown);
    }

    private static List<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) found.Add(t);
            found.AddRange(Find<T>(child));
        }
        return found;
    }
}
