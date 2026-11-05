namespace GlobalAutocorrect.Core.Prediction;

/// <summary>
/// D59 (7.1, 7.4): browsers update what UI Automation reports a moment after a key is drawn, so a read straight after a
/// key can still show the text before it. Predicting from that read suggested for the previous letter: nothing at all
/// for the last letter typed (found by the CI screen tests in CKEditor), or a completion that doesn't fit what is on
/// screen. The suggestion waits until the field shows the key just typed.
/// </summary>
public static class FieldCatchUp
{
    /// <summary>Re-reads while the field lags: 6 × 25 ms, then no suggestion for this key.</summary>
    public const int Retries = 6, RetryDelayMs = 25;

    /// <summary>
    /// D94, user report "sometimes it takes ages for letters to appear": every read is answered on the typed-in app's own
    /// thread, and a read per key (up to 7 with the retries above) slowed Edge and the Claude app while typing fast. The
    /// suggestion is predicted only once typing pauses this long; a suggestion during a burst was hidden by the next key
    /// anyway (7.5).
    /// </summary>
    public const int WaitForPauseMs = 90;

    /// <summary>D94: a pause long enough to predict in: nothing typed since the key, <paramref name="sinceKeyMs"/> ago.</summary>
    public static bool Paused(long keySeq, long latestSeq, long sinceKeyMs) => keySeq == latestSeq && sinceKeyMs >= WaitForPauseMs;

    /// <summary>True when the text before the caret ends with the character just typed.</summary>
    public static bool ShowsKey(string before, char key)
    {
        if (before.Length == 0) return false;
        char last = before[^1];
        if (last == key) return true;
        // Browsers store a space typed last as a non-breaking space until the next character (D42).
        return key == ' ' && last == ' ';
    }
}
