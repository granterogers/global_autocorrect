using System.Globalization;
using System.Windows.Media;

namespace GlobalAutocorrect.App.Theme;

/// <summary>
/// The one colour palette (9.2.a). <c>Tokens.xaml</c> mirrors these values and a test keeps the two in step
/// (R9_2_a). Hex strings are the source of truth so that tests and tools can use them without WPF.
/// </summary>
public static class Palette
{
    // Accent: one primary blue plus a lighter and a darker shade (9.2.a, 9.2.b).
    public const string AccentPrimary = "#1F6FEB";
    public const string AccentLight = "#5B9BFF";
    public const string AccentDark = "#134FB0";

    /// <summary>Neutral colours for one of the two modes.</summary>
    public sealed record Neutrals(string SurfaceBase, string SurfaceCard, string BorderSoft, string TextPrimary, string TextSecondary);

    public static Neutrals Light { get; } = new("#F7F9FC", "#FFFFFF", "#E3E8F0", "#0B1220", "#4A5568");
    public static Neutrals Dark { get; } = new("#0F1622", "#172131", "#26324A", "#E8EEF8", "#A9B6CC");

    /// <summary>Muted grey of the paused tray icon (9.2.e).</summary>
    public const string PausedGrey = "#8A94A6";

    public static Neutrals For(bool dark) => dark ? Dark : Light;

    public static Color ToColor(string hex)
    {
        string h = hex.TrimStart('#');
        return Color.FromRgb(
            byte.Parse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}

/// <summary>WCAG 2.x contrast maths for the token pairs (9.2.d).</summary>
public static class ContrastMath
{
    public static double RelativeLuminance(string hex)
    {
        var c = Palette.ToColor(hex);
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    public static double Ratio(string foregroundHex, string backgroundHex)
    {
        double a = RelativeLuminance(foregroundHex), b = RelativeLuminance(backgroundHex);
        double hi = Math.Max(a, b), lo = Math.Min(a, b);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Linear(byte channel)
    {
        double s = channel / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
