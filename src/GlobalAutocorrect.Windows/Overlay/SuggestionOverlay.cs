using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlobalAutocorrect.Windows.Overlay;

/// <summary>
/// The faint word completion just after the caret (7.1, 7.6). Font size comes from the caret height. It is only ever
/// shown, never focused; Tab acceptance is decided by the keyboard hook (7.4).
/// </summary>
public sealed class SuggestionOverlay : OverlayWindow
{
    private readonly TextBlock _text = new()
    {
        Opacity = 0.55,
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.None,
    };

    public string Completion { get; private set; } = "";

    public SuggestionOverlay(Brush foreground)
    {
        _text.Foreground = foreground;
        Content = _text;
    }

    /// <param name="caret">Caret rectangle in physical pixels (x, y, width, height).</param>
    public void ShowAt(string completion, (double X, double Y, double W, double H) caret)
    {
        Completion = completion;
        _text.Text = completion;
        double h = Math.Max(caret.H, 12);
        _text.FontSize = h * 0.72 * PixelsToDipsSafe();
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = (_text.DesiredSize.Width + 8) / PixelsToDipsSafe();
        PlaceAt(caret.X + Math.Max(caret.W, 1) + 1, caret.Y, width, h);
    }

    public void HideSuggestion()
    {
        Completion = "";
        Hide();
    }

    private double PixelsToDipsSafe()
    {
        try { return PixelsToDips; } catch (InvalidOperationException) { return 1; }
    }
}
