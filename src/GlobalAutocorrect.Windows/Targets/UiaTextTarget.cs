using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Windows.Native;
using GlobalAutocorrect.Windows.Uia;
using GlobalAutocorrect.Windows.Write;

namespace GlobalAutocorrect.Windows.Targets;

/// <summary>
/// Whatever text field currently has focus, read through UIA (10.6) and written with tagged SendInput (10.7).
/// Fields that don't expose text and caret, passwords, excluded apps and elevated windows are left alone (4.1.b, 8.3, 8.4, 10.8).
/// </summary>
public sealed class UiaTextTarget(UiaWorker worker, Func<UiaSnapshot, TargetStatus?>? gate = null) : ITextTarget
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
        return SendInputWriter.Tagged.ApplyEdit(caretDistance, deleteCount, replacement);
    }

    /// <summary>Characters the user may have typed after the edit while we wait for the app to show it.</summary>
    public const int MaxTypedDuringVerify = 64;

    public bool Verify(string expectedBefore, string expectedAfter)
    {
        // The app processes the injected keys asynchronously: poll briefly until it shows the result (4.6.d). The user
        // keeps typing meanwhile (4.6.e), so the expected text may be followed by a few new characters on the same line.
        string e = TextNorm.Normalise(expectedBefore), ea = TextNorm.Normalise(expectedAfter);
        string tail = e.Length > 48 ? e[^48..] : e;
        for (int i = 0; i < 12; i++)
        {
            var w = ReadWindow();
            if (w.CaretKnown)
            {
                string b = TextNorm.Normalise(w.Before), a = TextNorm.Normalise(w.After);
                int at = b.LastIndexOf(tail, StringComparison.Ordinal);
                bool beforeOk = at >= 0 && b.Length - (at + tail.Length) <= MaxTypedDuringVerify && !b.AsSpan(at + tail.Length).Contains('\n');
                int m = Math.Min(a.Length, ea.Length);
                if (beforeOk && a[..m] == ea[..m]) return true;
            }
            Thread.Sleep(35);
        }
        return false;
    }
}
