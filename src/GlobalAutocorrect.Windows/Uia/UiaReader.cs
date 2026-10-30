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
    string ClassName = "")   // D46: tells CKEditor 5 (Teams) apart, which needs fixes retyped rather than arrowed to
{
    public bool CaretKnown => Status == ReadStatus.Ok;

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
    private string _infoFor = "", _class = "";
    private string? _font;

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
            proc = ProcessName(pid);
            type = el.CurrentLocalizedControlType ?? "";
            if (el.CurrentIsPassword != 0) return UiaSnapshot.Fail(ReadStatus.Password, pid, proc, type);   // 8.3

            IUIAutomationTextRange? caret = null;
            string source = "";
            if (el.GetCurrentPattern(TextPattern2Id) is IUIAutomationTextPattern2 tp2)
            {
                caret = tp2.GetCaretRange(out _);
                source = "TextPattern2";
            }
            if (caret is null && el.GetCurrentPattern(TextPatternId) is IUIAutomationTextPattern tp)
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
            string rid = string.Join('.', (int[]?)el.GetRuntimeId() ?? []);
            // Font and class name only change with the field: read them once per field, not on every read, so reads
            // stay as quick as they were (each is another call into the app).
            if (rid != _infoFor) { _infoFor = rid; _font = FontName(caret); _class = el.CurrentClassName ?? ""; }
            return new UiaSnapshot(ReadStatus.Ok, b, a, source, pid, proc, type, rid, CaretRect(caret), el.CurrentControlType, _font, _class);
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
