using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws the app and tray icons in code (9.2.e) and writes multi-size .ico files (PNG-compressed entries) plus 256 px PNG previews.
// Usage: GlobalAutocorrect.Icons [repoRoot]   (default: walk up from the current directory until GlobalAutocorrect.slnx is found)
return IconTool.Run(args);

internal static class IconTool
{
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    // Palette: mirrors src/GlobalAutocorrect.App/Theme/Palette.cs (9.2.a).
    private static readonly Color AccentPrimary = Color.FromRgb(0x1F, 0x6F, 0xEB);
    private static readonly Color PausedGrey = Color.FromRgb(0x8A, 0x94, 0xA6);
    private static readonly Color BadgeBar = Color.FromRgb(0x4A, 0x55, 0x68);

    public static int Run(string[] args)
    {
        // WPF rendering needs an STA thread.
        int code = 1;
        var thread = new Thread(() => code = RunCore(args));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return code;
    }

    private static int RunCore(string[] args)
    {
        string root = args.Length > 0 && args[0] != "concepts" ? args[0] : FindRoot();
        string icons = Path.Combine(root, "assets", "icons");
        string previews = Path.Combine(root, "artifacts", "icons");
        Directory.CreateDirectory(icons);
        Directory.CreateDirectory(previews);
        if (args.Contains("concepts"))
        {
            // D75: candidates only; the installed icons are not touched.
            Concepts.WriteSheet(Path.Combine(previews, "concepts.png"), Render, EncodePng);
            Console.WriteLine($"Wrote {Path.Combine(previews, "concepts.png")}");
            return 0;
        }

        Write("app", AppTile, icons, previews);
        Write("tray-active", size => TrayGlyph(size, paused: false), icons, previews);
        Write("tray-paused", size => TrayGlyph(size, paused: true), icons, previews);

        // Contact sheet: every glyph on a light and a dark taskbar colour at the real tray sizes, enlarged 4x, so the
        // lead engineer can judge readability without installing anything.
        WriteContactSheet(Path.Combine(previews, "contact-sheet.png"));
        Console.WriteLine($"Wrote icons to {icons} and previews to {previews}");
        return 0;
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GlobalAutocorrect.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository, or pass the repository root.");
    }

    private static void Write(string name, Func<int, DrawingVisual> draw, string iconDir, string previewDir)
    {
        var pngs = new List<(int Size, byte[] Png)>();
        foreach (int size in Sizes) pngs.Add((size, EncodePng(Render(draw(size), size))));
        File.WriteAllBytes(Path.Combine(iconDir, name + ".ico"), BuildIco(pngs));
        File.WriteAllBytes(Path.Combine(previewDir, name + ".png"), pngs.Single(p => p.Size == 256).Png);
    }

    // ---- Drawings -------------------------------------------------------------------------------------------------
    // All shapes are described in a 0..1 unit square and scaled to the target size, so each size is drawn natively
    // (crisp) rather than shrunk from a big bitmap. Small sizes drop fine detail and thicken strokes.

    /// <summary>D75 (the user's choice of three): a rich blue tile with a white fountain-pen nib and, large, a spark.</summary>
    private static DrawingVisual AppTile(int size)
    {
        var v = new DrawingVisual();
        using var dc = v.RenderOpen();
        double s = size;
        Concepts.Tile(dc, s, size);
        Concepts.NibGlyph(dc, s, size, AccentPrimary);
        if (size >= 32) Concepts.Spark(dc, 0.25 * s, 0.27 * s, 0.09 * s, Colors.White);
        return v;
    }

    /// <summary>
    /// Tray glyph: solid AccentPrimary (or grey when paused) tile with a thin white outline, so it reads on light and
    /// dark taskbars alike. Paused swaps the check for a pause badge.
    /// </summary>
    private static DrawingVisual TrayGlyph(int size, bool paused)
    {
        var v = new DrawingVisual();
        using var dc = v.RenderOpen();
        double s = size;
        double outline = Math.Max(1.0, 0.06 * s);                   // thin white keyline
        double inset = outline / 2 + 0.02 * s;
        double radius = 0.24 * s;
        var tile = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);

        var fill = new SolidColorBrush(paused ? PausedGrey : AccentPrimary);
        dc.DrawRoundedRectangle(fill, new Pen(Brushes.White, outline) { LineJoin = PenLineJoin.Round }, tile, radius, radius);

        // D75: the app icon's nib, a little larger to fill the small tile; paused adds the badge.
        Concepts.NibGlyph(dc, s, size, paused ? PausedGrey : AccentPrimary, grow: paused ? 0.95 : 1.12, shift: paused ? 0.08 : 0);
        if (paused) DrawPauseBadge(dc, s, size);
        return v;
    }

    /// <summary>White disc with two dark bars, bottom-right, over the grey tile (paused).</summary>
    private static void DrawPauseBadge(DrawingContext dc, double s, int size)
    {
        double r = 0.23 * s;   // D75: smaller, so the nib stays whole
        var centre = new Point(s - r - 0.01 * s, s - r - 0.01 * s);
        double ring = Math.Max(1.0, 0.045 * s);
        dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(PausedGrey), ring), centre, r, r);
        double barW = Math.Max(1.5, 0.085 * s), barH = 0.27 * s, gap = 0.075 * s;
        var bar = new SolidColorBrush(BadgeBar);
        double rr = barW / 3;
        dc.DrawRoundedRectangle(bar, null, new Rect(centre.X - gap / 2 - barW, centre.Y - barH / 2, barW, barH), rr, rr);
        dc.DrawRoundedRectangle(bar, null, new Rect(centre.X + gap / 2, centre.Y - barH / 2, barW, barH), rr, rr);
    }

    // ---- Output ---------------------------------------------------------------------------------------------------

    private static BitmapSource Render(DrawingVisual visual, int size)
    {
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    internal static byte[] EncodePng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>ICO container with PNG-compressed entries (Vista and later). Width/height byte 0 means 256.</summary>
    internal static byte[] BuildIco(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);                 // reserved
        w.Write((ushort)1);                 // type: icon
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);               // palette colours
            w.Write((byte)0);               // reserved
            w.Write((ushort)1);             // colour planes
            w.Write((ushort)32);            // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
        return ms.ToArray();
    }

    private static void WriteContactSheet(string path)
    {
        int[] traySizes = [16, 20, 24, 32];
        const int scale = 4;
        var items = new (string Name, Func<int, DrawingVisual> Draw)[]
        {
            ("tray-active", s => TrayGlyph(s, false)),
            ("tray-paused", s => TrayGlyph(s, true)),
            ("app", AppTile),
        };
        var backgrounds = new[] { Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromRgb(0x20, 0x20, 0x20) };

        int cell = 32 * scale + 16;
        int width = cell * (traySizes.Length * items.Length) + 16;
        int height = cell * backgrounds.Length + 16;
        var sheet = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(sheet, BitmapScalingMode.NearestNeighbor);
        using (var dc = sheet.RenderOpen())
        {
            for (int row = 0; row < backgrounds.Length; row++)
            {
                dc.DrawRectangle(new SolidColorBrush(backgrounds[row]), null, new Rect(0, row * cell + 8, width, cell));
                int col = 0;
                foreach (var item in items)
                    foreach (int size in traySizes)
                    {
                        // Render at the real size, then enlarge without smoothing so single pixels stay visible.
                        var bmp = Render(item.Draw(size), size);
                        dc.DrawImage(bmp, new Rect(16 + col * cell, row * cell + 16, size * scale, size * scale));
                        col++;
                    }
            }
        }
        File.WriteAllBytes(path, EncodePng(Render(sheet, width, height)));
    }

    internal static BitmapSource Render(DrawingVisual visual, int width, int height)
    {
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
