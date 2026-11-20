namespace GlobalAutocorrect.Core.Health;

/// <summary>
/// D153: how long since the last keyboard or mouse input, from Windows' 32-bit millisecond tick counts
/// (GetLastInputInfo's dwTime and Environment.TickCount). An unsigned difference read input stamped a hair after the clock
/// reading as about 49 days idle: v1.43.0 was installed (closing the running app) while the user was typing. The
/// app's quiet updater (D131), deploy.ps1 and brain-eval.ps1 all worked it out that way.
/// </summary>
public static class IdleTime
{
    /// <summary>
    /// The signed difference, so the tick count wrapping (every 49.7 days) still works and input newer than
    /// <paramref name="nowTick"/> counts as just now. Over 24.8 days idle also reads as just now: never too idle.
    /// </summary>
    public static TimeSpan Since(uint lastInputTick, uint nowTick)
    {
        int ms = unchecked((int)(nowTick - lastInputTick));
        return ms <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
    }
}
