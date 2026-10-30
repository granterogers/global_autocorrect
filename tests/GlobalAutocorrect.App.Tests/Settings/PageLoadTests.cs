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
