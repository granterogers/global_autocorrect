using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// D75: candidate app icons for the user to choose from (`GlobalAutocorrect.Icons concepts`): each drawn large and at
// the real taskbar and tray sizes, on a light and a dark taskbar, in artifacts/icons/concepts.png.
internal static class Concepts
{
    private static readonly Color TileTop = Color.FromRgb(0x5C, 0xA2, 0xFF);
    private static readonly Color TileMid = Color.FromRgb(0x1F, 0x6F, 0xEB);   // the app's accent (Palette.AccentPrimary)
    private static readonly Color TileBottom = Color.FromRgb(0x17, 0x3F, 0xB0);
    private static readonly Color Coral = Color.FromRgb(0xFF, 0x6B, 0x6B);
    private static readonly Color Ice = Color.FromRgb(0xBF, 0xE3, 0xFF);

    public static readonly (string Name, Func<int, DrawingVisual> Draw)[] All =
    [
        ("A  Nib", Nib),
        ("B  Squiggle to tick", SquiggleTick),
        ("C  Aa", Letters),
    ];

    /// <summary>The shared tile: a rich blue gradient squircle with a soft glow at the top.</summary>
    public static void Tile(DrawingContext dc, double s, int size)
    {
        double m = 0.03 * s, r = 0.24 * s;
        var rect = new Rect(m, m, s - 2 * m, s - 2 * m);
        var fill = new LinearGradientBrush(new GradientStopCollection
        {
            new(TileTop, 0), new(TileMid, 0.55), new(TileBottom, 1),
        }, new Point(0.15, 0), new Point(0.85, 1));
        dc.DrawRoundedRectangle(fill, null, rect, r, r);
        if (size >= 32)
        {
            var glow = new RadialGradientBrush(Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
            {
                Center = new Point(0.3, 0.1), GradientOrigin = new Point(0.3, 0.1), RadiusX = 0.75, RadiusY = 0.55,
            };
            dc.DrawRoundedRectangle(glow, null, rect, r, r);
            // A hairline inner edge catches the light, as macOS and Windows 11 app icons do.
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Math.Max(1, 0.008 * s)),
                new Rect(rect.X + 0.004 * s, rect.Y + 0.004 * s, rect.Width - 0.008 * s, rect.Height - 0.008 * s), r, r);
        }
    }

    private static Pen RoundPen(Color c, double w) =>
        new(new SolidColorBrush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    /// <summary>Four-point spark.</summary>
    public static void Spark(DrawingContext dc, double cx, double cy, double outer, Color c)
    {
        double inner = outer * 0.26;
        var g = new StreamGeometry();
        using (var x = g.Open())
        {
            x.BeginFigure(new Point(cx, cy - outer), true, true);
            x.QuadraticBezierTo(new Point(cx + inner, cy - inner), new Point(cx + outer, cy), true, true);
            x.QuadraticBezierTo(new Point(cx + inner, cy + inner), new Point(cx, cy + outer), true, true);
            x.QuadraticBezierTo(new Point(cx - inner, cy + inner), new Point(cx - outer, cy), true, true);
            x.QuadraticBezierTo(new Point(cx - inner, cy - inner), new Point(cx, cy - outer), true, true);
        }
        dc.DrawGeometry(new SolidColorBrush(c), null, g);
    }

    /// <summary>A: a fountain-pen nib, tilted, writing; a spark beside it. The app icon (D75).</summary>
    private static DrawingVisual Nib(int size)
    {
        var v = new DrawingVisual();
        using var dc = v.RenderOpen();
        double s = size;
        Tile(dc, s, size);
        NibGlyph(dc, s, size, TileMid);
        if (size >= 32) Spark(dc, 0.25 * s, 0.27 * s, 0.09 * s, Colors.White);   // clear of the nib's top
        return v;
    }

    /// <summary>
    /// The white nib, tilted 40 degrees so it writes down to the left, with its slit, breather hole and collar cut in
    /// <paramref name="cut"/> (the tile's colour). Below 24 px only the slit stays, a pixel wide, so it still reads as a
    /// nib rather than a leaf.
    /// </summary>
    public static void NibGlyph(DrawingContext dc, double s, int size, Color cut, double grow = 1.0, double shift = 0)
    {
        dc.PushTransform(new TranslateTransform(-shift * s, -shift * s));   // up and left, clear of a badge
        dc.PushTransform(new RotateTransform(40, 0.47 * s, 0.53 * s));
        dc.PushTransform(new ScaleTransform(grow, grow, 0.47 * s, 0.53 * s));
        var nib = new StreamGeometry();
        using (var x = nib.Open())
        {
            x.BeginFigure(new Point(0.36 * s, 0.22 * s), true, true);
            x.LineTo(new Point(0.58 * s, 0.22 * s), true, true);
            x.BezierTo(new Point(0.66 * s, 0.42 * s), new Point(0.6 * s, 0.62 * s), new Point(0.47 * s, 0.86 * s), true, true);
            x.BezierTo(new Point(0.34 * s, 0.62 * s), new Point(0.28 * s, 0.42 * s), new Point(0.36 * s, 0.22 * s), true, true);
        }
        dc.DrawGeometry(Brushes.White, null, nib);
        var brush = new SolidColorBrush(cut);
        if (size >= 24)
        {
            double hole = (size >= 48 ? 0.045 : 0.06) * s;
            dc.DrawEllipse(brush, null, new Point(0.47 * s, 0.5 * s), hole, hole);
            dc.DrawLine(new Pen(brush, Math.Max(1.2, 0.03 * s)) { StartLineCap = PenLineCap.Round }, new Point(0.47 * s, 0.53 * s), new Point(0.47 * s, 0.84 * s));
            dc.DrawRectangle(brush, null, new Rect(0.36 * s, 0.3 * s, 0.22 * s, Math.Max(1, 0.022 * s)));   // collar line
        }
        else
        {
            dc.DrawLine(new Pen(brush, 1.0 / grow), new Point(0.47 * s, 0.42 * s), new Point(0.47 * s, 0.86 * s));   // the slit
        }
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }
    /// <summary>B: a red spelling squiggle that runs into a white tick, under two lines of "text".</summary>
    private static DrawingVisual SquiggleTick(int size)
    {
        var v = new DrawingVisual();
        using var dc = v.RenderOpen();
        double s = size;
        Tile(dc, s, size);
        double line = (size <= 24 ? 0.12 : 0.085) * s;
        if (size >= 24)
        {
            // Two lines of text above.
            var text = RoundPen(Color.FromArgb(235, 255, 255, 255), line);
            dc.DrawLine(text, new Point(0.24 * s, 0.3 * s), new Point(0.76 * s, 0.3 * s));
            dc.DrawLine(text, new Point(0.24 * s, 0.47 * s), new Point(0.56 * s, 0.47 * s));
        }
        // The squiggle under a misspelt word...
        double y = size >= 24 ? 0.7 : 0.6;
        var squiggle = new StreamGeometry();
        using (var x = squiggle.Open())
        {
            double x0 = 0.2, step = size >= 24 ? 0.065 : 0.09, amp = size >= 24 ? 0.045 : 0.06;
            x.BeginFigure(new Point(x0 * s, y * s), false, false);
            for (int i = 0; i < 4; i++)
                x.QuadraticBezierTo(new Point((x0 + step * (i + 0.5)) * s, (y + (i % 2 == 0 ? -amp : amp) * 2) * s), new Point((x0 + step * (i + 1)) * s, y * s), true, true);
        }
        dc.DrawGeometry(null, RoundPen(Coral, line * 0.8), squiggle);
        // ...that becomes a tick.
        var tick = new StreamGeometry();
        using (var x = tick.Open())
        {
            double tx = size >= 24 ? 0.47 : 0.56;
            x.BeginFigure(new Point(tx * s, y * s), false, false);
            x.LineTo(new Point((tx + 0.1) * s, (y + 0.1) * s), true, true);
            x.LineTo(new Point(0.82 * s, (y - 0.2) * s), true, true);
        }
        dc.DrawGeometry(null, RoundPen(Colors.White, line * 1.1), tick);
        return v;
    }

    /// <summary>C: "Aa" with a sweeping underline that ends in a spark.</summary>
    private static DrawingVisual Letters(int size)
    {
        var v = new DrawingVisual();
        using var dc = v.RenderOpen();
        double s = size;
        Tile(dc, s, size);
        string text = size >= 24 ? "Aa" : "A";
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            (size >= 24 ? 0.5 : 0.62) * s, Brushes.White, 1.0);
        dc.DrawText(ft, new Point((s - ft.Width) / 2, (size >= 24 ? 0.12 : 0.06) * s));
        var sweep = new StreamGeometry();
        using (var x = sweep.Open())
        {
            x.BeginFigure(new Point(0.2 * s, 0.76 * s), false, false);
            x.BezierTo(new Point(0.4 * s, 0.86 * s), new Point(0.62 * s, 0.84 * s), new Point(0.8 * s, 0.7 * s), true, true);
        }
        dc.DrawGeometry(null, RoundPen(Ice, (size <= 24 ? 0.1 : 0.065) * s), sweep);
        if (size >= 32) Spark(dc, 0.8 * s, 0.68 * s, 0.085 * s, Colors.White);
        return v;
    }

    /// <summary>All concepts: large, then 48/32/24/16 at real size and enlarged 3x, on light and dark taskbars.</summary>
    public static void WriteSheet(string path, Func<DrawingVisual, int, int, BitmapSource> render, Func<BitmapSource, byte[]> png)
    {
        int[] small = [48, 32, 24, 16];
        const int big = 256, zoom = 3, pad = 24;
        int rowH = big + 2 * pad;
        int smallW = small.Sum(z => z * zoom + pad);
        int width = pad + big + pad + smallW * 2 + pad;
        int height = rowH * All.Length + 40;
        var sheet = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(sheet, BitmapScalingMode.NearestNeighbor);
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < All.Length; i++)
            {
                var (name, draw) = All[i];
                double top = 40 + i * rowH;
                var label = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 18, Brushes.Black, 1.0);
                dc.DrawText(label, new Point(pad, top - 30));
                dc.DrawImage(render(draw(big), big, big), new Rect(pad, top, big, big));
                double x = pad + big + pad;
                foreach (var bg in new[] { Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromRgb(0x20, 0x20, 0x20) })
                {
                    dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(x - pad / 2, top, smallW, big));
                    foreach (int z in small)
                    {
                        dc.DrawImage(render(draw(z), z, z), new Rect(x, top + (big - z * zoom) / 2.0, z * zoom, z * zoom));
                        x += z * zoom + pad;
                    }
                }
            }
        }
        File.WriteAllBytes(path, png(render(sheet, width, height)));
    }
}
