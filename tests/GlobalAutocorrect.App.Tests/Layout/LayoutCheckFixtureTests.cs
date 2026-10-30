using System.Windows;
using System.Windows.Markup;
using Xunit;

namespace GlobalAutocorrect.App.Tests.Layout;

/// <summary>
/// Each layout check, run on a small layout broken on purpose, must report it, and must pass the same layout done right
/// (D63). Without these, a check that can never fail would pass the real windows just the same.
/// </summary>
public class LayoutCheckFixtureTests
{
    private const string Ns = """xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" """;

    private static readonly string Rows = string.Concat(Enumerable.Range(1, 20).Select(i => $"""<Button Content="Row {i}" Margin="0,4" />"""));

    [Fact]
    public void R9_5_ScrollingCheckFailsWithoutAScrollViewer()
    {
        var bad = Scrolling($"""<StackPanel {Ns} x:Name="Rows"><TextBlock Text="Title" />{Rows}</StackPanel>""");
        Assert.Contains(bad, p => p.Contains("not inside a ScrollViewer"));
        Assert.Contains(bad, p => p.Contains("not fully in view"));
        Assert.Empty(Scrolling($"""<ScrollViewer {Ns} VerticalScrollBarVisibility="Auto"><StackPanel x:Name="Rows"><TextBlock Text="Title" />{Rows}</StackPanel></ScrollViewer>"""));
    }

    [Fact]
    public void R9_5_ScrollingCheckFailsWhenAnInnerScrollViewerSwallowsTheWheel()
    {
        // The settings window before D63: the NavigationView's scroll viewer around a page with its own.
        var bad = Scrolling($"""
            <ScrollViewer {Ns} VerticalScrollBarVisibility="Auto">
              <ScrollViewer VerticalScrollBarVisibility="Auto"><StackPanel x:Name="Rows"><TextBlock Text="Title" />{Rows}</StackPanel></ScrollViewer>
            </ScrollViewer>
            """);
        Assert.Contains(bad, p => p.Contains("not fully in view"));
    }

    [Fact]
    public void R9_5_ClippingCheckFailsOnTextCutOffByItsColumn()
    {
        const string Text = "Free the AI's memory after this long";
        var bad = Run(Clipping, $"""<Grid {Ns} Width="300"><Grid.ColumnDefinitions><ColumnDefinition Width="60" /><ColumnDefinition /></Grid.ColumnDefinitions><TextBlock Text="{Text}" /></Grid>""");
        Assert.Contains(bad, p => p.Contains("is clipped by"));
        Assert.Empty(Run(Clipping, $"""<Grid {Ns} Width="300"><Grid.ColumnDefinitions><ColumnDefinition Width="60" /><ColumnDefinition /></Grid.ColumnDefinitions><TextBlock Text="{Text}" TextWrapping="Wrap" /></Grid>"""));
    }

    [Fact]
    public void R9_5_ClippingCheckFailsOnTextCutShortOrRunningOffTheWindow()
    {
        var trimmed = Run(Clipping, $"""<StackPanel {Ns} Width="60"><TextBlock Text="Gemma 4 E4B (recommended)" TextTrimming="CharacterEllipsis" /></StackPanel>""");
        Assert.Contains(trimmed, p => p.Contains("ellipsis"));
        var offWindow = Run(Clipping, $"""<StackPanel {Ns} Orientation="Horizontal"><Border Width="380" /><TextBlock Text="Restore defaults" /></StackPanel>""");
        Assert.Contains(offWindow, p => p.StartsWith("TextBlock \"Restore defaults\"") && p.Contains("is clipped by"));
    }

    [Fact]
    public void R9_5_ClippingCheckFailsOnAVisibleControlWithNoSize()
    {
        var bad = Run(Clipping, $"""<StackPanel {Ns}><Button Content="Add" Width="0" /></StackPanel>""");
        Assert.Contains(bad, p => p.Contains("laid out at 0"));
    }

    [Fact]
    public void R9_5_OverlapCheckFailsOnControlsInTheSameCell()
    {
        var bad = Run(Overlaps, $"""<Grid {Ns}><Button Content="Add" HorizontalAlignment="Left" Width="120" /><CheckBox Content="On" HorizontalAlignment="Left" Margin="60,0,0,0" /></Grid>""");
        Assert.Contains(bad, p => p.Contains("overlaps"));
        Assert.Empty(Run(Overlaps, $"""<StackPanel {Ns}><Button Content="Add" /><CheckBox Content="On" /></StackPanel>"""));
    }

    [Fact]
    public void R9_5_NameCheckFailsOnControlsWithNothingToRead()
    {
        var bad = Run((_, region) => LayoutChecks.Names(region), $"""
            <StackPanel {Ns}>
              <TextBox />
              <RadioButton><StackPanel><TextBlock Text="Cautious" /></StackPanel></RadioButton>
              <Button Content="Add" />
              <ComboBox AutomationProperties.Name="AI model" />
            </StackPanel>
            """);
        Assert.Equal(2, bad.Count);
        Assert.Contains(bad, p => p.StartsWith("TextBox"));
        Assert.Contains(bad, p => p.StartsWith("RadioButton"));
    }

    [Fact]
    public void R9_5_TabCheckFailsOnAControlTabSkipsOrTabGoingBackUp()
    {
        var skipped = Run(TabOrder, $"""<StackPanel {Ns}><Button Content="First" /><Button Content="Second" IsTabStop="False" /></StackPanel>""");
        Assert.Contains(skipped, p => p.Contains("\"Second\" can't be reached"));
        var backwards = Run(TabOrder, $"""<StackPanel {Ns}><Button Content="Top" TabIndex="2" /><Button Content="Bottom" TabIndex="1" /></StackPanel>""");
        Assert.Contains(backwards, p => p.Contains("back to"));
        var rowBackwards = Run(TabOrder, $"""<StackPanel {Ns} Orientation="Horizontal"><Button Content="Left" TabIndex="2" /><Button Content="Right" TabIndex="1" /></StackPanel>""");
        Assert.Contains(rowBackwards, p => p.Contains("back to"));
        Assert.Empty(Run(TabOrder, $"""<StackPanel {Ns}><StackPanel Orientation="Horizontal"><TextBox Width="100" AutomationProperties.Name="Word" /><Button Content="Add" /></StackPanel><Button Content="Below" /></StackPanel>"""));
    }

    // ---- Running a check on a fixture ---------------------------------------------------------------------------------

    private static List<string> Clipping(LayoutHost host, FrameworkElement region) => LayoutChecks.Clipping(host, region);
    private static List<string> Overlaps(LayoutHost host, FrameworkElement region) => LayoutChecks.Overlaps(host, region);
    private static List<string> TabOrder(LayoutHost host, FrameworkElement region) => LayoutChecks.TabOrder(host, region);

    /// <summary>Lays the XAML out in a 400 x 300 window (never shown) and runs a check on it.</summary>
    private static List<string> Run(Func<LayoutHost, FrameworkElement, List<string>> check, string xaml)
    {
        var problems = new List<string>();
        WpfApp.Run(_ =>
        {
            var window = new Window { Content = XamlReader.Parse(xaml) };
            using var host = LayoutHost.Of(window, new Size(400, 300), 1.0);
            problems = check(host, host.Root);
        });
        return problems;
    }

    private static List<string> Scrolling(string xaml) =>
        Run((host, region) => LayoutChecks.Scrolling(host, (FrameworkElement)LogicalTreeHelper.FindLogicalNode(region, "Rows")), xaml);
}
