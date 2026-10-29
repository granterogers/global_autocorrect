using System.Diagnostics;

namespace GlobalAutocorrect.Core.Engine;

/// <summary>Monotonic milliseconds. Tests and the eval use a manual clock so timing rules run on virtual time.</summary>
public interface IClock
{
    long NowMs { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    public long NowMs => (long)Stopwatch.GetElapsedTime(0).TotalMilliseconds;
}

public sealed class ManualClock(long start = 0) : IClock
{
    public long NowMs { get; private set; } = start;
    public void Advance(long ms) => NowMs += ms;
    public void Set(long ms) => NowMs = ms;
}

/// <summary>
/// Live view of physical keys from the hook, ahead of what the engine has processed (D6). The write gate compares
/// <see cref="LatestKeySeq"/> with the session's processed sequence to know no key slipped in.
/// </summary>
public interface IKeyMonitor
{
    long LatestKeySeq { get; }
    long LastKeyAtMs { get; }
    bool ModifierHeld { get; }
}
