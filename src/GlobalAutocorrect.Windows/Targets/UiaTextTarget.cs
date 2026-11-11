using System.Diagnostics;
using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Telemetry;
using GlobalAutocorrect.Windows.Native;
using GlobalAutocorrect.Windows.Uia;
using GlobalAutocorrect.Windows.Write;

namespace GlobalAutocorrect.Windows.Targets;

/// <summary>
/// Whatever text field currently has focus, read through UIA (10.6) and written with tagged SendInput (10.7).
/// Fields that don't expose text and caret, passwords, excluded apps and elevated windows are left alone (4.1.b, 8.3, 8.4, 10.8).
/// </summary>
/// <param name="diagnose">Gets one line per failed verify: app, kind of mismatch, unusual characters. Never typed text (8.6).</param>
/// <param name="timings">D64: how long reads, writes and verifies take (durations only).</param>
public sealed class UiaTextTarget(UiaWorker worker, Func<UiaSnapshot, TargetStatus?>? gate = null, Action<string>? diagnose = null, Timings? timings = null) : ITextTarget
{
    private int _lastPid;

    /// <summary>Latest successful read, for the overlay (caret rectangle) and the home page (app name).</summary>
    public UiaSnapshot? LastSnapshot { get; private set; }

    /// <summary>D81: the program of the last read field.</summary>
    public string AppName => LastSnapshot?.ProcessName ?? "";

    /// <summary>D92: names shown near the last read field.</summary>
    public IReadOnlyList<string> NamesNearby => LastSnapshot?.Names ?? [];

    public TextWindow ReadWindow() => ReadWindow(400, 80);

    private string _lastStatus = "";

    /// <summary>D97: how long reads take per program, so the app can do less where they are slow. Null: not tracked.</summary>
    public GlobalAutocorrect.Core.Telemetry.ReadCost? Cost { get; init; }

    /// <summary>D97: in a program whose reads are slow, the suggestion reads only the last few words.</summary>
    public TextWindow ReadForSuggestion(bool light) =>
        ReadWindow(light ? GlobalAutocorrect.Core.Prediction.FieldCatchUp.LightBeforeChars : 400, light ? 0 : 80);

    private TextWindow ReadWindow(int beforeChars, int afterChars)
    {
        long started = Stopwatch.GetTimestamp();
        var s = worker.Invoke(r => r.Read(beforeChars, afterChars), UiaSnapshot.Fail(ReadStatus.Failed));
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        timings?.Record("read", ms);
        if (s is not null) Cost?.Record(s.ProcessName, ms);   // D97
        if (s is null) return TextWindow.Unavailable(TargetStatus.Unavailable);
        string id = $"{s.ProcessId}:{s.RuntimeId}";
        var status = s.Status switch
        {
            ReadStatus.Ok => TargetStatus.Ok,
            ReadStatus.Password => TargetStatus.Password,
            ReadStatus.CaretUnknown => TargetStatus.CaretUnknown,
            ReadStatus.NoTextPattern => TargetStatus.NoText,
            _ => TargetStatus.Unavailable,
        };
        if (status == TargetStatus.Ok && gate?.Invoke(s) is { } blocked) status = blocked;
        // D105: why a field is left alone, once per change (program name and a status, never text): in Windows Sandbox
        // the screen tests made no fixes at all, and nothing said why.
        string why = $"{s.ProcessName} {status}";
        if (why != _lastStatus) { _lastStatus = why; if (status != TargetStatus.Ok) diagnose?.Invoke($"read: app={s.ProcessName} status={status} (D105)"); }
        if (status != TargetStatus.Ok) return TextWindow.Unavailable(status, id);
        _lastPid = s.ProcessId;
        LastSnapshot = s;
        return new TextWindow(TargetStatus.Ok, id, s.Before, s.After);
    }

    /// <summary>
    /// D67: whether the focused field is one the app would correct, so the model can start loading before the first key.
    /// Changes nothing here (no <see cref="LastSnapshot"/>, no timings), so it may run off the engine thread.
    /// </summary>
    public bool FocusIsCorrectable() =>
        Correctable(worker.Invoke(r => r.Read(beforeChars: 1, afterChars: 0), UiaSnapshot.Fail(ReadStatus.Failed)), gate, Environment.ProcessId);

    /// <summary>
    /// D67: a field the app would correct: it reads with a known caret (not a password, 8.3), the gate lets it through
    /// (not excluded or full screen, 8.4; not elevated, 10.8), and it isn't one of the app's own windows.
    /// </summary>
    public static bool Correctable(UiaSnapshot? s, Func<UiaSnapshot, TargetStatus?>? gate, int ownProcessId) =>
        s is { Status: ReadStatus.Ok } && s.ProcessId != ownProcessId && gate?.Invoke(s) is null;

    /// <summary>D67: the foreground window is one of the app's own (settings, first run, tray menu).</summary>
    public static bool OwnWindowInForeground()
    {
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    public bool ApplyEdit(int caretDistance, int deleteCount, string replacement)
    {
        // SendInput goes to whatever has focus now; make sure it's still the app we read (12.2).
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        if (pid != _lastPid) return false;
        _beforeWrite = LastSnapshot?.Before ?? "";
        long started = Stopwatch.GetTimestamp();
        bool sent = SendInputWriter.Tagged.ApplyEdit(caretDistance, deleteCount, replacement);
        timings?.Record("write", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return sent;
    }

    public bool LastEditBlocked { get; private set; }

    /// <summary>
    /// D105: the edit batch, fenced: the hook lets it through only if no key reached the app since the gate's last look
    /// (<see cref="GlobalAutocorrect.Windows.Input.WriteFence"/>). Waits up to 150 ms for the hook's verdict; with no verdict (no hook) it counts as sent.
    /// </summary>
    public bool ApplyEdit(int caretDistance, int deleteCount, string replacement, long expectedKeySeq) =>
        ApplyFenced(caretDistance, deleteCount, replacement, expectedKeySeq, toEnd: false);

    /// <summary>D118: the fenced edit, back to the end of the field with Ctrl+End.</summary>
    public bool ApplyEditThenEnd(int caretDistance, int deleteCount, string replacement, long expectedKeySeq) =>
        ApplyFenced(caretDistance, deleteCount, replacement, expectedKeySeq, toEnd: true);

    private bool ApplyFenced(int caretDistance, int deleteCount, string replacement, long expectedKeySeq, bool toEnd)
    {
        LastEditBlocked = false;
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        if (pid != _lastPid) return false;
        _beforeWrite = LastSnapshot?.Before ?? "";
        long started = Stopwatch.GetTimestamp();
        nuint tag = GlobalAutocorrect.Windows.Input.WriteFence.Arm(expectedKeySeq);
        var writer = new SendInputWriter(tag);
        bool sent = toEnd ? writer.ApplyEditThenEnd(caretDistance, deleteCount, replacement) : writer.ApplyEdit(caretDistance, deleteCount, replacement);
        timings?.Record("write", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (!sent) return false;
        for (int i = 0; i < 150 && GlobalAutocorrect.Windows.Input.WriteFence.Blocked(tag) is null; i++) Thread.Sleep(1);
        if (GlobalAutocorrect.Windows.Input.WriteFence.Blocked(tag) == true)
        {
            LastEditBlocked = true;
            diagnose?.Invoke($"write blocked: app={LastSnapshot?.ProcessName ?? "?"} a key reached the app first (D105, no text)");
            return false;
        }
        return true;
    }

    /// <summary>D99: the caret was left short of the user's place after a fix: step it back, if focus hasn't moved.</summary>
    public bool MoveCaretRight(int count)
    {
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        return count > 0 && pid == _lastPid && SendInputWriter.Tagged.MoveRight(count);
    }

    /// <summary>D46: CKEditor 5 (Teams' message box) applies a burst of arrow keys after the edits that follow them.</summary>
    public bool RetypeInsteadOfArrows => LastSnapshot?.ClassName.Contains("ck-editor", StringComparison.Ordinal) == true;

    /// <summary>Characters the user may have typed after the edit while we wait for the app to show it.</summary>
    public const int MaxTypedDuringVerify = 64;

    /// <summary>D99: how far short of the user's place the caret may be found and still be put back.</summary>
    public const int MaxCaretShort = 300;

    public bool Verify(string expectedBefore, string expectedAfter) => Verify(expectedBefore, expectedAfter, 0);

    /// <param name="reach">D120: how far back before the caret the check must see (the fix and what follows it).</param>
    public bool Verify(string expectedBefore, string expectedAfter, int reach)
    {
        // The app processes the injected keys asynchronously: poll briefly until it shows the result (4.6.d). The user
        // keeps typing meanwhile (4.6.e), so the expected text may be followed by a few new characters on the same line.
        string e = TextNorm.Comparable(expectedBefore), ea = TextNorm.Comparable(expectedAfter);   // D42
        int want = Math.Max(48, reach);   // D120: the fix itself, not only what follows it
        string tail = e.Length > want ? e[^want..] : e;
        // The text after the caret only has to still be there: browsers can show their own suggestion (Edge's text
        // prediction) right after the caret, which used to count as a mismatch.
        string afterLine = ea.Split('\n')[0];
        string afterKey = afterLine.Length > 64 ? afterLine[..64] : afterLine;   // D61: room for a ghost suggestion and the text after it
        string b = "", a = "";
        bool read = false, beforeOk = false;
        long started = Stopwatch.GetTimestamp();
        int caretShortReads = 0;
        string lastShort = "";
        // A heavy web app can take a while to show injected keys (found in Intercom): poll for up to about a second.
        // D120: a long fix takes a heavy page longer to apply: wait up to about 5 s for it, 1 s for a short one.
        int polls = Math.Clamp(30 + reach / 2, 30, 150);
        for (int i = 0; i < polls; i++)
        {
            var w = ReadWindow();
            if (w.CaretKnown)
            {
                read = true;
                // D99: the fix is there but the caret stopped short and has stayed put for three reads: stop waiting, so
                // the caller can put it back before the user's next key lands in the wrong place.
                string now = w.Before + "\u0001" + w.After;
                if (TextNorm.CaretShort(expectedBefore, w.Before, w.After, MaxCaretShort) > 0)
                {
                    caretShortReads = now == lastShort ? caretShortReads + 1 : 1;
                    lastShort = now;
                    if (caretShortReads >= 3) { diagnose?.Invoke(Describe(true, false, TextNorm.Comparable(w.Before), TextNorm.Comparable(w.After), e) + " caretShort=yes"); return false; }
                }
                else caretShortReads = 0;
                b = TextNorm.Comparable(w.Before); a = TextNorm.Comparable(w.After);
                int at = b.LastIndexOf(tail, StringComparison.Ordinal);
                // Typing may go on after the fix, Enter included (a fast typist starts the next line straight away).
                beforeOk = at >= 0 && b.Length - (at + tail.Length) <= MaxTypedDuringVerify;
                if (beforeOk && TextNorm.AfterConsistent(afterKey, a))
                {
                    timings?.Record("verify", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return true;
                }
            }
            Thread.Sleep(35);
        }
        diagnose?.Invoke(Describe(read, beforeOk, b, a, e));
        // D108: screen tests only (GA_E2E=1, synthetic text, never the user's): what was expected next to what the field
        // showed, as code points, so a failure can be traced to where the fix or the keys went.
        if (Environment.GetEnvironmentVariable("GA_E2E") == "1")
            diagnose?.Invoke($"verify-detail: expected=[{Codes(e.Length > 60 ? e[^60..] : e)}] got=[{Codes(b.Length > 76 ? b[^76..] : b)}] after=[{Codes(a.Length > 20 ? a[..20] : a)}]");
        return false;
    }

    private string _beforeWrite = "";

    private static string Codes(string s) => string.Join(' ', s.Select(c => ((int)c).ToString("X4")));

    /// <summary>What kind of mismatch, for the diagnostics log: never the text itself (8.6).</summary>
    private string Describe(bool read, bool beforeOk, string b, string a, string expected)
    {
        string app = LastSnapshot?.ProcessName ?? "?";
        string kind = !read ? "unreadable"
            : beforeOk ? "after-caret-changed"
            : b.EndsWith(TextNorm.Normalise(_beforeWrite) is { Length: > 24 } pre ? pre[^24..] : TextNorm.Normalise(_beforeWrite), StringComparison.Ordinal) ? "edit-not-shown"
            : "before-caret-differs";
        static string Odd(string s) => string.Join(' ', s.Where(c => !char.IsLetterOrDigit(c) && c != ' ' && !(c < 128 && char.IsPunctuation(c)))
                                                       .Distinct().Select(c => $"U+{(int)c:X4}"));
        return $"verify failed: app={app} kind={kind} source={LastSnapshot?.Source} lengthDelta={b.Length - expected.Length} " +
               $"oddBefore=[{Odd(b.Length > 80 ? b[^80..] : b)}] oddAfter=[{Odd(a.Length > 40 ? a[..40] : a)}]";
    }
}
