using System.IO;
using System.Windows;
using System.Xml.Linq;
using GlobalAutocorrect.App.Theme;
using Wpf.Ui.Appearance;
using Xunit;

namespace GlobalAutocorrect.App.Tests.Theme;

public class ThemeTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void R9_2_d_ContrastAtLeast4_5()
    {
        var failures = new List<string>();
        void Check(string what, string fg, string bg)
        {
            double r = ContrastMath.Ratio(fg, bg);
            if (r < 4.5) failures.Add($"{what}: {fg} on {bg} = {r:F2}:1");
        }

        foreach (var (mode, n) in new[] { ("light", Palette.Light), ("dark", Palette.Dark) })
        {
            Check($"{mode} TextPrimary/SurfaceBase", n.TextPrimary, n.SurfaceBase);
            Check($"{mode} TextPrimary/SurfaceCard", n.TextPrimary, n.SurfaceCard);
            Check($"{mode} TextSecondary/SurfaceBase", n.TextSecondary, n.SurfaceBase);
            Check($"{mode} TextSecondary/SurfaceCard", n.TextSecondary, n.SurfaceCard);
        }
        // Button text on the accent fills.
        Check("white on AccentPrimary", "#FFFFFF", Palette.AccentPrimary);
        Check("white on AccentDark", "#FFFFFF", Palette.AccentDark);

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public void R9_2_d_ContrastMathMatchesKnownValues()
    {
        Assert.Equal(21.0, ContrastMath.Ratio("#FFFFFF", "#000000"), 2);
        Assert.Equal(1.0, ContrastMath.Ratio("#1F6FEB", "#1F6FEB"), 5);
    }

    [Fact]
    public void R9_2_a_Tokens_xaml_matches_Palette()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tokens.xaml");
        Assert.True(File.Exists(path), $"{path} was not copied next to the test assembly");
        var colours = XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value.Trim(), StringComparer.Ordinal);

        void Same(string key, string expected) =>
            Assert.True(colours.TryGetValue(key, out var actual) && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
                $"{key}: Tokens.xaml has '{(colours.TryGetValue(key, out var v) ? v : "(missing)")}', Palette has '{expected}'");

        Same("AccentPrimary", Palette.AccentPrimary);
        Same("AccentLight", Palette.AccentLight);
        Same("AccentDark", Palette.AccentDark);
        foreach (var (prefix, n) in new[] { ("Light", Palette.Light), ("Dark", Palette.Dark) })
        {
            Same(prefix + "SurfaceBase", n.SurfaceBase);
            Same(prefix + "SurfaceCard", n.SurfaceCard);
            Same(prefix + "BorderSoft", n.BorderSoft);
            Same(prefix + "TextPrimary", n.TextPrimary);
            Same(prefix + "TextSecondary", n.TextSecondary);
        }
    }

    [Fact]
    public void R9_2_c_Tokens_xaml_defines_type_scale_spacing_and_radii()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tokens.xaml");
        var doubles = XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "Double")
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => double.Parse(e.Value, System.Globalization.CultureInfo.InvariantCulture));
        var expected = new Dictionary<string, double>
        {
            ["FontSizeDisplay"] = 28, ["FontSizeTitle"] = 20, ["FontSizeSubtitle"] = 16, ["FontSizeBody"] = 14, ["FontSizeCaption"] = 12,
            ["Space4"] = 4, ["Space8"] = 8, ["Space12"] = 12, ["Space16"] = 16, ["Space24"] = 24, ["Space32"] = 32,
        };
        foreach (var (k, v) in expected) Assert.Equal(v, doubles[k]);
    }

    /// <summary>Loads the real Tokens.xaml in a WPF Application and checks the service swaps the brushes for light and dark.</summary>
    [Fact]
    public void R9_2_d_ThemeServiceSwapsBrushesForLightAndDark()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/GlobalAutocorrect;component/Theme/Tokens.xaml", UriKind.Absolute)
                });
                var service = new ThemeService(app);

                service.Apply(ApplicationTheme.Dark);
                Assert.Equal(Palette.ToColor(Palette.Dark.SurfaceCard), ((System.Windows.Media.SolidColorBrush)app.Resources["SurfaceCardBrush"]).Color);
                Assert.Equal(Palette.ToColor(Palette.Dark.TextPrimary), (System.Windows.Media.Color)app.Resources["TextPrimary"]);
                Assert.Equal(ApplicationTheme.Dark, service.Current);

                service.Apply(ApplicationTheme.Light);
                Assert.Equal(Palette.ToColor(Palette.Light.SurfaceCard), ((System.Windows.Media.SolidColorBrush)app.Resources["SurfaceCardBrush"]).Color);
                Assert.Equal(Palette.ToColor(Palette.Light.TextSecondary), ((System.Windows.Media.SolidColorBrush)app.Resources["TextSecondaryBrush"]).Color);
                Assert.Equal(ApplicationTheme.Light, service.Current);
            }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
