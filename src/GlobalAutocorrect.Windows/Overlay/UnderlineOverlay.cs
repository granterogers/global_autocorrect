using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace GlobalAutocorrect.Windows.Overlay;

/// <summary>A brief accent-coloured underline under a corrected word that fades within about a second (4.7).</summary>
public sealed class UnderlineOverlay : OverlayWindow
{
    private readonly Rectangle _line = new() { RadiusX = 1, RadiusY = 1, VerticalAlignment = VerticalAlignment.Bottom };

    public UnderlineOverlay(Brush accent)
    {
        _line.Fill = accent;
        Content = _line;
    }

    /// <param name="word">The corrected word's rectangle in physical pixels.</param>
    public void Flash((double X, double Y, double W, double H) word)
    {
        double thickness = Math.Max(2, word.H * 0.08);
        _line.Height = thickness * PixelsToDipsSafe();
        PlaceAt(word.X, word.Y + word.H - thickness * 0.5, word.W, thickness * 2);
        var fade = new DoubleAnimation(0.9, 0, new Duration(TimeSpan.FromMilliseconds(1000)))
        {
            BeginTime = TimeSpan.FromMilliseconds(250),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => Hide();
        _line.BeginAnimation(OpacityProperty, fade);
    }

    private double PixelsToDipsSafe()
    {
        try { return PixelsToDips; } catch (InvalidOperationException) { return 1; }
    }
}
