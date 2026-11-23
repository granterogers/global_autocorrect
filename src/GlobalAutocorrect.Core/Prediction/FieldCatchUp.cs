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
    public static int Retries => Learning.Tunables.PredictReadRetries.Int;   // D155
    public static int RetryDelayMs => Learning.Tunables.PredictReadRetryMs.Int;

    /// <summary>
    /// D94, user report "sometimes it takes ages for letters to appear": every read is answered on the typed-in app's own
    /// thread, and a read per key (up to 7 with the retries above) slowed Edge and the Claude app while typing fast. The
    /// suggestion is predicted only once typing pauses this long; a suggestion during a burst was hidden by the next key
    /// anyway (7.5).
    /// </summary>
    public static int WaitForPauseMs => Learning.Tunables.PredictWaitForPauseMs.Int;   // D155

    /// <summary>D97: in a program whose reads are slow (<see cref="Telemetry.ReadCost"/>), a longer pause, and a shorter read.</summary>
    public static int LightWaitForPauseMs => Learning.Tunables.PredictLightWaitForPauseMs.Int;   // D155
    public const int LightBeforeChars = 120;

    public static int PauseFor(bool light) => light ? LightWaitForPauseMs : WaitForPauseMs;

    /// <summary>
    /// D176 (E2E, three editors: "no suggestion appeared", every key logged as still "typing", the last one too): the
    /// wait for the pause is a timer, which can end a little before the Stopwatch says the time is up (the Windows timer
    /// ticks every 15.6 ms), and the elapsed time was cut to whole milliseconds; 249 of 250 ms read as "still typing" and
    /// nothing was suggested for the last key. The time check allows one timer tick; a later key is still caught by its
    /// sequence number.
    /// </summary>
    public const int TimerSlackMs = 16;

    /// <summary>D94: a pause long enough to predict in: nothing typed since the key, <paramref name="sinceKeyMs"/> ago.</summary>
    public static bool Paused(long keySeq, long latestSeq, long sinceKeyMs, bool light = false) =>
        keySeq == latestSeq && sinceKeyMs + TimerSlackMs >= PauseFor(light);

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
