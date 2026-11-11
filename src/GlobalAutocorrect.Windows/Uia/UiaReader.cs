using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace GlobalAutocorrect.Windows.Uia;

public enum ReadStatus { Ok, NoFocus, Password, NoTextPattern, CaretUnknown, Failed }

/// <summary>What UIA reports around the caret. Before/After are only a window, never the whole document (10.6.b).</summary>
public sealed record UiaSnapshot(
    ReadStatus Status,
    string Before,
    string After,
    string Source,          // "TextPattern2" or "Selection" (10.6.a)
    int ProcessId,
    string ProcessName,
    string ControlType,
    string RuntimeId,
    (double X, double Y, double W, double H)? CaretRect,
    int ControlTypeId = 0,   // UIA control type id: language-independent, unlike ControlType
    string? FontName = null, // the field's font where the app reports it, so the suggestion can match it (7.6)
    string ClassName = "",   // D46: tells CKEditor 5 (Teams) apart, which needs fixes retyped rather than arrowed to
    string FrameworkId = "", // D53: UIA's UI framework (Win32, WPF, the Chromium engine...), not the app
    double? FontSizePt = null,  // D79: the field's font size where the app reports it
    IReadOnlyList<string>? Names = null)  // D92: names shown near the field (memory only, 8.6)
{
    public bool CaretKnown => Status == ReadStatus.Ok;

    /// <summary>D53: Chromium draws the caret as tall as the font size; Win32 and WPF fields draw it a whole line tall.</summary>
    public bool CaretIsFontHeight => FrameworkId == "Chrome";

    public static UiaSnapshot Fail(ReadStatus s, int pid = 0, string proc = "", string type = "") =>
        new(s, "", "", "", pid, proc, type, "", null);
}

/// <summary>
/// Reads text around the caret through UI Automation (10.6). Must be called from one thread only;
/// <c>UiaWorker</c> (T7.2) provides isolation and timeouts (10.3.b).
/// </summary>
public sealed class UiaReader
{
    private const int TextPatternId = 10014;    // UIA_TextPatternId
    private const int TextPattern2Id = 10024;   // UIA_TextPattern2Id

    private readonly IUIAutomation2 _uia;
    private string _infoFor = "", _class = "", _framework = "";
    private readonly FieldStyleCache _style = new();   // D83
    private readonly HostedBrowser _hosted = new();    // D87
    private readonly NearbyNames _nearby = new();      // D92

    public UiaReader(uint timeoutMs = 250)
    {
        _uia = (IUIAutomation2)new CUIAutomation8();
        _uia.ConnectionTimeout = timeoutMs;
        _uia.TransactionTimeout = timeoutMs;
    }

    public UiaSnapshot Read(int beforeChars = 400, int afterChars = 80)
    {
        IUIAutomationElement? el;
        try { el = _uia.GetFocusedElement(); }
        catch (Exception e) when (IsUiaFailure(e)) { return UiaSnapshot.Fail(ReadStatus.NoFocus); }
        if (el is null) return UiaSnapshot.Fail(ReadStatus.NoFocus);

        int pid = 0;
        string proc = "", type = "";
        try
        {
            pid = el.CurrentProcessId;
            // D105: UI Automation reported process 0 for Edge's focused field in Windows Sandbox, which read as the "Idle"
            // process, taken as elevated, so nothing was corrected. The field has focus, so its window is in front.
            if (pid == 0) { GlobalAutocorrect.Windows.Native.NativeMethods.GetWindowThreadProcessId(GlobalAutocorrect.Windows.Native.NativeMethods.GetForegroundWindow(), out uint fg); pid = (int)fg; }
            proc = ProcessName(pid);
            type = el.CurrentLocalizedControlType ?? "";
            if (el.CurrentIsPassword != 0) return UiaSnapshot.Fail(ReadStatus.Password, pid, proc, type);   // 8.3

            object? p2 = el.GetCurrentPattern(TextPattern2Id);
            object? p1 = p2 is null ? el.GetCurrentPattern(TextPatternId) : null;
            // D87: focus that stops at a frame hosting a browser engine (WinUI 3 WebView2): read the engine's focused field.
            // The process stays the app's, so it is the app that is learnt about and gated (8.3, D81).
            if (p2 is null && p1 is null && _hosted.FocusedIn(_uia, pid) is { } inner)
            {
                el = inner;
                type = el.CurrentLocalizedControlType ?? "";
                if (el.CurrentIsPassword != 0) return UiaSnapshot.Fail(ReadStatus.Password, pid, proc, type);
                p2 = el.GetCurrentPattern(TextPattern2Id);
            }

            IUIAutomationTextRange? caret = null;
            string source = "";
            if (p2 is IUIAutomationTextPattern2 tp2)
            {
                caret = tp2.GetCaretRange(out _);
                source = "TextPattern2";
            }
            if (caret is null && (p1 ??= el.GetCurrentPattern(TextPatternId)) is IUIAutomationTextPattern tp)
            {
                var sel = tp.GetSelection();
                if (sel is not null && sel.Length == 1)
                {
                    var r = sel.GetElement(0);
                    if (r.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, r, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) == 0)
                    {
                        caret = r;
                        source = "Selection";
                    }
                }
                if (caret is null) return UiaSnapshot.Fail(ReadStatus.CaretUnknown, pid, proc, type);   // 10.6.c
            }
            if (caret is null) return UiaSnapshot.Fail(ReadStatus.NoTextPattern, pid, proc, type);

            var before = caret.Clone();
            before.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -beforeChars);
            var after = caret.Clone();
            after.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, afterChars);

            string b = before.GetText(-1) ?? "";
            string a = after.GetText(-1) ?? "";
            (b, a) = InField(caret, (p2 ?? p1) as IUIAutomationTextPattern, b, a, beforeChars, afterChars);   // D88
            string rid = string.Join('.', (int[]?)el.GetRuntimeId() ?? []);
            // Class and framework only change with the field: read them once per field, not on every read, so reads
            // stay as quick as they were (each is another call into the app).
            if (rid != _infoFor) { _infoFor = rid; _class = el.CurrentClassName ?? ""; _framework = el.CurrentFrameworkId ?? ""; }
            // D83: the font is read from the character before the caret, so not from an empty field: asked again later.
            if (_style.ShouldRead(rid, b)) _style.Store(FontName(caret), FontSize(caret));
            // D85: where Windows knows the caret too (Win32, WinUI), its height is the truth; Notepad's UIA caret wasn't.
            var snapshotFramework = new UiaSnapshot(ReadStatus.Ok, "", "", "", 0, "", "", "", null, FrameworkId: _framework);
            var caretRect = snapshotFramework.CaretIsFontHeight ? CaretRect(caret) : SystemCaret.Pick(CaretRect(caret), SystemCaret.Read(pid));   // Chromium: no system caret
            return new UiaSnapshot(ReadStatus.Ok, b, a, source, pid, proc, type, rid, caretRect, el.CurrentControlType, _style.Font, _class, _framework, _style.SizePt, _nearby.For(_uia, el, rid));
        }
        catch (Exception e) when (IsUiaFailure(e))
        {
            return UiaSnapshot.Fail(ReadStatus.Failed, pid, proc, type);
        }
    }

    /// <summary>
    /// Screen rectangle (physical pixels) of the text that ends <paramref name="caretDistance"/> characters before the
    /// caret and is <paramref name="length"/> long: where a correction just landed (4.7). Null if it can't be measured.
    /// </summary>
    public (double X, double Y, double W, double H)? RangeRect(int caretDistance, int length)
    {
        try
        {
            var el = _uia.GetFocusedElement();
            if (el is null) return null;
            if (el.GetCurrentPattern(TextPatternId) is null && _hosted.FocusedIn(_uia, el.CurrentProcessId) is { } inner) el = inner;   // D87
            IUIAutomationTextRange? caret = (el.GetCurrentPattern(TextPattern2Id) as IUIAutomationTextPattern2)?.GetCaretRange(out _);
            if (caret is null && el.GetCurrentPattern(TextPatternId) is IUIAutomationTextPattern tp && tp.GetSelection() is { Length: 1 } sel)
                caret = sel.GetElement(0);
            if (caret is null) return null;
            var r = caret.Clone();
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -(caretDistance + length));
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, -caretDistance);
            var rects = (double[]?)r.GetBoundingRectangles();
            if (rects is not { Length: >= 4 }) return null;
            // A word can wrap: use the last line's rectangle (where the user is looking).
            int last = rects.Length - 4;
            return (rects[last], rects[last + 1], rects[last + 2], rects[last + 3]);
        }
        catch (Exception e) when (IsUiaFailure(e)) { return null; }
    }

    // D122: a fix on an earlier line, written by selecting it through UI Automation. One at a time, on this thread.
    private IUIAutomationTextPattern? _placePattern;
    private IUIAutomationTextRange? _placeCaret, _placeFix;
    private string _placeTail = "";
    private int _placeTailUnits;

    private (IUIAutomationTextPattern? Pattern, IUIAutomationTextRange? Caret) FocusedCaret()
    {
        var el = _uia.GetFocusedElement();
        if (el is null) return (null, null);
        if (el.GetCurrentPattern(TextPatternId) is null && _hosted.FocusedIn(_uia, el.CurrentProcessId) is { } inner) el = inner;   // D87
        var pattern = el.GetCurrentPattern(TextPatternId) as IUIAutomationTextPattern;
        IUIAutomationTextRange? caret = (el.GetCurrentPattern(TextPattern2Id) as IUIAutomationTextPattern2)?.GetCaretRange(out _);
        if (caret is null && pattern?.GetSelection() is { Length: 1 } sel) caret = sel.GetElement(0);
        return (pattern, caret);
    }

    private static string Text(IUIAutomationTextRange r) => GlobalAutocorrect.Core.Targets.TextNorm.Normalise(r.GetText(-1) ?? "");

    /// <summary>
    /// D122, step 1: finds <paramref name="original"/> right before one of <paramref name="tails"/> (how the field may show
    /// what was typed after it), counted back from the caret, and selects it. Only where the field's own text there is
    /// exactly that; otherwise nothing is selected and false comes back.
    /// </summary>
    public bool PlaceSelectFix(string original, IReadOnlyList<string> tails)
    {
        _placePattern = null; _placeCaret = _placeFix = null;
        try
        {
            var (pattern, caret) = FocusedCaret();
            if (pattern is null || caret is null) return false;
            string want = GlobalAutocorrect.Core.Targets.TextNorm.Normalise(original);
            foreach (string tail in tails)
            {
                // Characters as the field counts them: text elements, or UTF-16 units where an emoji counts twice.
                foreach (var (tu, ou) in new[] { (Elements(tail), Elements(original)), (tail.Length, original.Length) }.Distinct())
                {
                    var whole = caret.Clone();
                    if (whole.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -(tu + ou)) != -(tu + ou)) continue;
                    if (Text(whole) != GlobalAutocorrect.Core.Targets.TextNorm.Normalise(original + tail)) continue;
                    var fix = whole.Clone();
                    if (tu > 0 && fix.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, -tu) != -tu) continue;
                    if (Text(fix) != want) continue;
                    _placePattern = pattern; _placeCaret = caret.Clone(); _placeFix = fix;
                    _placeTail = GlobalAutocorrect.Core.Targets.TextNorm.Normalise(tail); _placeTailUnits = tu;
                    fix.Select();
                    return true;
                }
            }
            return false;
        }
        catch (Exception e) when (IsUiaFailure(e)) { return false; }
    }

    /// <summary>D122, step 2: the field shows the fix selected (the browser has applied the selection).</summary>
    public bool PlaceSelectionIsFix()
    {
        try
        {
            if (_placePattern?.GetSelection() is not { Length: 1 } sel || _placeFix is null) return false;
            var r = sel.GetElement(0);
            return r.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, _placeFix, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start) == 0
                && r.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, _placeFix, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) == 0;
        }
        catch (Exception e) when (IsUiaFailure(e)) { return false; }
    }

    /// <summary>D122, step 3: the replacement is in, the caret right after it, and the text typed after the fix follows.</summary>
    public bool PlaceTyped(string replacement)
    {
        try
        {
            var (_, caret) = FocusedCaret();
            if (caret is null) return false;
            var back = caret.Clone();
            int n = Elements(replacement);
            if (n > 0 && back.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -n) != -n) return false;
            if (Text(back) != GlobalAutocorrect.Core.Targets.TextNorm.Normalise(replacement)) return false;
            var on = caret.Clone();
            on.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, _placeTailUnits);
            return Text(on) == _placeTail;
        }
        catch (Exception e) when (IsUiaFailure(e)) { return false; }
    }

    /// <summary>
    /// D122, step 4: the caret back where the user was. First by stepping over the typed tail from where typing left it
    /// (counted the way the field counted it in step 1); <paramref name="attempt"/> 1 uses the caret position kept from
    /// before the fix instead. With <paramref name="fixUntouched"/> (nothing was typed) that kept position is used at once.
    /// </summary>
    public bool PlaceReturn(int attempt, bool fixUntouched = false)
    {
        try
        {
            if (fixUntouched || attempt > 0) { _placeCaret?.Select(); return _placeCaret is not null; }
            var (_, caret) = FocusedCaret();
            if (caret is null) return false;
            var r = caret.Clone();
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, _placeTailUnits);
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, _placeTailUnits);
            r.Select();
            return true;
        }
        catch (Exception e) when (IsUiaFailure(e)) { return false; }
    }

    /// <summary>D122: the caret is back: collapsed, with <paramref name="beforeTail"/> and the typed tail right before it.</summary>
    public bool PlaceReturned(string beforeTail)
    {
        try
        {
            var (_, caret) = FocusedCaret();
            if (caret is null || caret.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, caret, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) != 0) return false;
            var r = caret.Clone();
            int n = Elements(beforeTail) + _placeTailUnits;
            if (r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -n) != -n) return false;
            return Text(r) == GlobalAutocorrect.Core.Targets.TextNorm.Normalise(beforeTail) + _placeTail;
        }
        catch (Exception e) when (IsUiaFailure(e)) { return false; }
    }

    private static int Elements(string s) => GlobalAutocorrect.Core.Targets.TextNorm.Elements(s);

    /// <summary>Touches the focused element's text pattern so Chromium/Electron turn on full accessibility (10.6.d).</summary>
    public void Warm()
    {
        try { _ = _uia.GetFocusedElement()?.GetCurrentPattern(TextPatternId); }
        catch (Exception e) when (IsUiaFailure(e)) { }
    }

    public IUIAutomation2 Automation => _uia;

    /// <summary>
    /// What a target app can throw at us through UIA: COM errors, UIA timeouts (TimeoutException), access denied,
    /// an element or process that just went away. All mean "can't read here", never "crash" (10.3.b).
    /// </summary>
    private static bool IsUiaFailure(Exception e) => e is COMException or TimeoutException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or InvalidCastException or NullReferenceException;

    /// <summary>D88: the window either side of the caret, kept to the field's own text (<see cref="CaretWindow"/>).</summary>
    private static (string, string) InField(IUIAutomationTextRange caret, IUIAutomationTextPattern? pattern, string b, string a, int beforeChars, int afterChars)
    {
        if (pattern is null) return (b, a);
        try
        {
            var field = pattern.DocumentRange;
            var toStart = caret.Clone();
            toStart.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, field, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            var toEnd = caret.Clone();
            toEnd.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, field, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
            return CaretWindow.InField(b, a, toStart.GetText(beforeChars + 1), toEnd.GetText(afterChars), beforeChars);
        }
        catch (Exception e) when (IsUiaFailure(e)) { return (b, a); }
    }

    private const int FontNameAttributeId = 40005;   // UIA_FontNameAttributeId

    /// <summary>The font of the character just before the caret; null if the app doesn't say (or it's mixed).</summary>
    private static string? FontName(IUIAutomationTextRange caret)
    {
        try
        {
            var r = caret.Clone();
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -1);
            return r.GetAttributeValue(FontNameAttributeId) is string s && s.Length > 0 ? s : null;
        }
        catch (Exception e) when (IsUiaFailure(e)) { return null; }
    }

    private const int FontSizeAttributeId = 40006;   // UIA_FontSizeAttributeId (points)

    /// <summary>D79: the size in points of the character just before the caret; null if the app doesn't say.</summary>
    private static double? FontSize(IUIAutomationTextRange caret)
    {
        try
        {
            var r = caret.Clone();
            r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -1);
            return r.GetAttributeValue(FontSizeAttributeId) is double d && d is > 2 and < 200 ? d : null;
        }
        catch (Exception e) when (IsUiaFailure(e)) { return null; }
    }

    private static (double, double, double, double)? CaretRect(IUIAutomationTextRange caret)
    {
        try
        {
            var rects = (double[]?)caret.GetBoundingRectangles();
            if (rects is { Length: >= 4 }) return (rects[0], rects[1], rects[2], rects[3]);
            var one = caret.Clone();   // a collapsed range often has no rectangle: measure the character before it
            one.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -1);
            rects = (double[]?)one.GetBoundingRectangles();
            return rects is { Length: >= 4 } ? (rects[0] + rects[2], rects[1], 0, rects[3]) : null;
        }
        catch (Exception e) when (IsUiaFailure(e)) { return null; }
    }

    private static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ""; }
    }
}
