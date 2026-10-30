using System.Drawing;
using System.IO;
using FlaUI.UIA3;
using GlobalAutocorrect.E2E.Support;
using Xunit;
using Path = System.IO.Path;

namespace GlobalAutocorrect.E2E;

/// <summary>
/// The suggestion's size on screen (7.6, "the suggestion uses the field's font"): it must sit just after the caret and
/// its letters must be as tall as the field's own. Measured from screen pixels, not from the app's numbers: the overlay
/// is found as the app's visible transparent no-activate window, captured, then hidden and captured again, so its ink is
/// exactly what changed; the field's ink is measured in the same line just before the caret.
/// </summary>
[Trait("Category", "E2E")]
[Collection("screen")]
public sealed class OverlayTests
{
    private static string Model(string s) => s;   // no AI edits: nothing but the suggestion changes on screen

    // "hea" -> "lth" (corpus n-gram, fresh history). Both the completion and the end of the typed line ("to hea") have
    // ascenders and no descenders, so the two ink heights are the same thing measured: ascender top to baseline.
    private const string Typed = "Looking forward to hea";
    private const string Expected = "lth";

    [Fact]
    public void R7_6_SuggestionIsTheFieldsTextSize_Notepad()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm, prediction: true);
        using var uia = new UIA3Automation();
        var (_, doc, close) = DesktopTests.OpenNotepad(uia);
        try
        {
            Desk.Type(Typed);
            Assert.True(Desk.WaitUntil(() => DesktopTests.Text(doc) == Typed, 3000), $"got \"{DesktopTests.Text(doc)}\"; {app.Diagnostics()}");
            Measure("notepad", app, uia);
        }
        finally { close(); }
    }

    [Fact]
    public async Task R7_6_SuggestionIsTheFieldsTextSize_CKEditor()
    {
        Desk.RequireIdle();
        using var llm = new MockLlm(Model);
        using var app = new AppUnderTest(llm, prediction: true);
        using var uia = new UIA3Automation();
        var (browser, p, pw) = await BrowserTests.Open("ckeditor");
        try
        {
            Desk.Type(Typed);
            string got = "";
            for (int i = 0; i < 20 && got != Typed; i++)
            {
                await Task.Delay(150, TestContext.Current.CancellationToken);
                got = (await p.EvaluateAsync<string>("window.getText()")).Replace(' ', ' ');
            }
            Assert.True(got == Typed, $"got \"{got}\"; {app.Diagnostics()}");
            Measure("ckeditor", app, uia);
        }
        finally
        {
            await browser.CloseAsync();
            pw.Dispose();
        }
    }

    private static void Measure(string name, AppUnderTest app, UIA3Automation uia)
    {
        using var dpi = Screen.PerMonitorDpi();
        (nint Hwnd, Rectangle Rect) overlay = default;
        bool shown = Desk.WaitUntil(() =>
        {
            var all = Screen.Overlays(app.ProcessId);
            if (all.Count == 0) return false;
            overlay = all.OrderByDescending(o => o.Rect.Height).First();
            return true;
        }, 5000);
        var caretOrNull = Screen.CaretRect(uia, out string caretSource);
        var systemCaret = Screen.SystemCaret();
        string font = Screen.FontName(uia);
        var full = Screen.Bounds;
        string Facts() => $"{name}: caret {caretOrNull}, overlay {(shown ? overlay.Rect.ToString() : "none")}, field font \"{font}\"; {app.Diagnostics()}";
        if (!shown)
        {
            using var whole = Screen.Capture(full);
            Screen.Save(whole, $"overlay-{name}-missing.png");
        }
        Assert.True(shown, $"7.1: no suggestion overlay appeared after \"{Typed}\"; {Facts()}");
        Assert.True(caretOrNull is { Height: > 0 }, $"UIA gave no caret rectangle; {Facts()}");
        var caret = caretOrNull!.Value;
        int h = caret.Height;

        // The area around the caret's line, captured with the overlay and again without it.
        var area = Rectangle.FromLTRB(caret.Left - 8 * h, caret.Top - 2 * h, Math.Max(overlay.Rect.Right, caret.Right) + 2 * h, caret.Bottom + 2 * h);
        area.Intersect(full);
        using var with = Screen.Capture(area);
        Screen.Hide(overlay.Hwnd);
        Assert.True(Desk.WaitUntil(() => Screen.Overlays(app.ProcessId).All(o => o.Hwnd != overlay.Hwnd), 2000), "the overlay could not be hidden for the second capture");
        Thread.Sleep(150);
        using var without = Screen.Capture(area);
        Screen.Save(with, $"overlay-{name}-with.png");
        Screen.Save(without, $"overlay-{name}-without.png");

        Rectangle Local(Rectangle r) => new(r.Left - area.Left, r.Top - area.Top, r.Width, r.Height);
        // Overlay ink: what changed inside the overlay's window, skipping its first 4 columns (the field's caret blinks
        // right next to it). Field ink: the line's own text just before the caret ("to hea"), not the caret itself.
        var ov = Local(overlay.Rect);
        var overlayInk = Screen.DiffInk(with, without, Rectangle.FromLTRB(ov.Left + 4, ov.Top, ov.Right, ov.Bottom));
        // Rows: the caret's and the overlay's together, so neither measurement is cut off by the other's box.
        var line = Local(Rectangle.FromLTRB(caret.Left, Math.Min(caret.Top, overlay.Rect.Top), caret.Right, Math.Max(caret.Bottom, overlay.Rect.Bottom)));
        var fieldInk = Screen.TextInk(without, Rectangle.FromLTRB(line.Left - (int)(2.5 * h), line.Top, line.Left - 2, line.Bottom));

        int? overlayInkH = overlayInk is { } o ? o.Bottom - o.Top + 1 : null;
        int? fieldInkH = fieldInk is { } f ? f.Bottom - f.Top + 1 : null;
        double windowRatio = overlay.Rect.Height / (double)h;
        double? inkRatio = overlayInkH is { } a && fieldInkH is { } b && b > 0 ? a / (double)b : null;
        string report = $"""
            app: {name}
            typed: "{Typed}" (expected suggestion "{Expected}")
            field font (UIA): "{font}"
            caret rect (UIA, px): {caret} from {caretSource}
            system caret (GetGUIThreadInfo, px): {(systemCaret?.ToString() ?? "none")}
            overlay window (px): {overlay.Rect}
            overlay height / caret height: {overlay.Rect.Height} / {h} = {windowRatio:F2}
            overlay ink rows: {(overlayInk is { } oi ? $"{oi.Top}..{oi.Bottom}" : "none")} = {overlayInkH?.ToString() ?? "-"} px
            field ink rows (before caret): {(fieldInk is { } fi ? $"{fi.Top}..{fi.Bottom}" : "none")} = {fieldInkH?.ToString() ?? "-"} px
            ink height ratio (overlay / field): {(inkRatio is { } r ? r.ToString("F2") : "-")}
            gap caret -> overlay (px): {overlay.Rect.Left - caret.Right}; top offset (px): {overlay.Rect.Top - caret.Top}
            """;
        Directory.CreateDirectory(Path.Combine(Desk.Repo, "artifacts", "screens"));
        File.WriteAllText(Path.Combine(Desk.Repo, "artifacts", "screens", $"overlay-{name}.txt"), report);
        TestContext.Current.TestOutputHelper?.WriteLine(report);

        // 7.6: next to the caret, on its line, and sized from the caret height.
        Assert.True(overlay.Rect.Left - caret.Right >= -3 && overlay.Rect.Left - caret.Right <= Math.Max(6, h / 2), $"7.6: the overlay is not just after the caret\n{report}");
        Assert.True(Math.Abs(overlay.Rect.Top - caret.Top) <= Math.Max(3, h / 4), $"7.6: the overlay is not on the caret's line\n{report}");
        Assert.True(windowRatio is >= 0.75 and <= 1.25, $"7.6: overlay height is not within 25% of the caret height\n{report}");
        // "The suggestion uses the field's font": its letters are as tall as the field's own (within 25%).
        Assert.True(overlayInkH is not null, $"no suggestion text was found in the overlay\n{report}");
        Assert.True(fieldInkH is not null, $"no field text was found before the caret\n{report}");
        // D53: within a pixel (10% for large text). ±25% let CKEditor's 10 px letters next to 12 px pass.
        Assert.True(Math.Abs(overlayInkH!.Value - fieldInkH!.Value) <= Math.Max(1.0, fieldInkH.Value * 0.1),
            $"7.6: the suggestion's letters are not the field's size (ink {overlayInkH} px vs {fieldInkH} px)\n{report}");
    }
}
