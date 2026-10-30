using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Windows.Native;
using GlobalAutocorrect.Windows.Uia;
using GlobalAutocorrect.Windows.Write;

namespace GlobalAutocorrect.Windows.Targets;

/// <summary>
/// Whatever text field currently has focus, read through UIA (10.6) and written with tagged SendInput (10.7).
/// Fields that don't expose text and caret, passwords, excluded apps and elevated windows are left alone (4.1.b, 8.3, 8.4, 10.8).
/// </summary>
/// <param name="diagnose">Gets one line per failed verify: app, kind of mismatch, unusual characters. Never typed text (8.6).</param>
public sealed class UiaTextTarget(UiaWorker worker, Func<UiaSnapshot, TargetStatus?>? gate = null, Action<string>? diagnose = null) : ITextTarget
{
    private int _lastPid;

    /// <summary>Latest successful read, for the overlay (caret rectangle) and the home page (app name).</summary>
    public UiaSnapshot? LastSnapshot { get; private set; }

    public TextWindow ReadWindow()
    {
        var s = worker.Invoke(r => r.Read(), UiaSnapshot.Fail(ReadStatus.Failed));
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
        if (status != TargetStatus.Ok) return TextWindow.Unavailable(status, id);
        _lastPid = s.ProcessId;
        LastSnapshot = s;
        return new TextWindow(TargetStatus.Ok, id, s.Before, s.After);
    }

    public bool ApplyEdit(int caretDistance, int deleteCount, string replacement)
    {
        // SendInput goes to whatever has focus now; make sure it's still the app we read (12.2).
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        if (pid != _lastPid) return false;
        _beforeWrite = LastSnapshot?.Before ?? "";
        return SendInputWriter.Tagged.ApplyEdit(caretDistance, deleteCount, replacement);
    }

    /// <summary>Characters the user may have typed after the edit while we wait for the app to show it.</summary>
    public const int MaxTypedDuringVerify = 64;

    public bool Verify(string expectedBefore, string expectedAfter)
    {
        // The app processes the injected keys asynchronously: poll briefly until it shows the result (4.6.d). The user
        // keeps typing meanwhile (4.6.e), so the expected text may be followed by a few new characters on the same line.
        string e = TextNorm.Comparable(expectedBefore), ea = TextNorm.Comparable(expectedAfter);   // D42
        string tail = e.Length > 48 ? e[^48..] : e;
        // The text after the caret only has to still be there: browsers can show their own suggestion (Edge's text
        // prediction) right after the caret, which used to count as a mismatch.
        string afterLine = ea.Split('\n')[0];
        string afterKey = afterLine.Length > 24 ? afterLine[..24] : afterLine;
        string b = "", a = "";
        bool read = false, beforeOk = false;
        // A heavy web app can take a while to show injected keys (found in Intercom): poll for up to about a second.
        for (int i = 0; i < 30; i++)
        {
            var w = ReadWindow();
            if (w.CaretKnown)
            {
                read = true;
                b = TextNorm.Comparable(w.Before); a = TextNorm.Comparable(w.After);
                int at = b.LastIndexOf(tail, StringComparison.Ordinal);
                // Typing may go on after the fix, Enter included (a fast typist starts the next line straight away).
                beforeOk = at >= 0 && b.Length - (at + tail.Length) <= MaxTypedDuringVerify;
                if (beforeOk && TextNorm.AfterConsistent(afterKey, a)) return true;
            }
            Thread.Sleep(35);
        }
        diagnose?.Invoke(Describe(read, beforeOk, b, a, e));
        return false;
    }

    private string _beforeWrite = "";

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
