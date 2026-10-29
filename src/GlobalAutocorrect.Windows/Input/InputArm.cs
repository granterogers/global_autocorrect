namespace GlobalAutocorrect.Windows.Input;

/// <summary>
/// A one-shot permission the hook checks synchronously: "if the very next key is Tab (or Ctrl+Z), take it" (7.4, 4.8.a).
/// Armed at a key sequence number; any other key, click or caret move in between makes it stale.
/// </summary>
public sealed class InputArm
{
    private long _armedAtSeq = -1;

    public void ArmAt(long seq) => Interlocked.Exchange(ref _armedAtSeq, seq);
    public void Disarm() => Interlocked.Exchange(ref _armedAtSeq, -1);
    public bool IsArmed => Interlocked.Read(ref _armedAtSeq) >= 0;

    /// <summary>True (and disarms) only if armed at exactly <paramref name="previousSeq"/>, i.e. nothing happened since.</summary>
    public bool TryConsume(long previousSeq) =>
        previousSeq >= 0 && Interlocked.CompareExchange(ref _armedAtSeq, -1, previousSeq) == previousSeq;
}
