using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlobalAutocorrect.Windows.Overlay;

/// <summary>
/// The faint word completion just after the caret (7.1, 7.6), in the field's own font where the app reports it, sized so its line height matches the caret's. It is only ever
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

    private static readonly FontFamily DefaultFont = new("Segoe UI Variable Text, Segoe UI");

    /// <param name="caret">Caret rectangle in physical pixels (x, y, width, height).</param>
    /// <param name="fontName">The field's font (UIA), or null to use Segoe UI.</param>
    /// <param name="caretIsFontHeight">D53: the app draws its caret one em tall (Chromium), not one line.</param>
    public void ShowAt(string completion, (double X, double Y, double W, double H) caret, string? fontName = null, bool caretIsFontHeight = false)
    {
        Completion = completion;
        _text.Text = completion;
        double h = Math.Max(caret.H, 12);
        var family = fontName is null ? DefaultFont : new FontFamily(fontName + ", Segoe UI");
        _text.FontFamily = family;
        _text.FontSize = FontSizeFor(h * PixelsToDipsSafe(), family.LineSpacing, caretIsFontHeight);
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = (_text.DesiredSize.Width + 8) / PixelsToDipsSafe();
        PlaceAt(caret.X + Math.Max(caret.W, 1) + 1, caret.Y, width, h);
    }

    /// <summary>
    /// The caret is as tall as a line of text, and a font's line is <paramref name="lineSpacing"/> times its size
    /// (Consolas about 1.17, Segoe UI about 1.33). Dividing gives the size the field is using, at any zoom; a fixed
    /// share of the caret drew text too small in some fonts and too big in others (user feedback).
    /// D53: Chromium (Edge, Chrome) draws its caret one em tall, so there the caret height is the font size; dividing it
    /// too drew the suggestion about 15% small (measured on the CI runner in CKEditor 5: 10 px letters next to 12 px).
    /// </summary>
    public static double FontSizeFor(double caretHeightDips, double lineSpacing, bool caretIsFontHeight = false) =>
        caretIsFontHeight ? caretHeightDips : caretHeightDips / Math.Clamp(lineSpacing is > 0 and < 10 ? lineSpacing : 1.33, 1.0, 1.6);

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
