using GlobalAutocorrect.Core.Undo;

namespace GlobalAutocorrect.Windows.Input;

/// <summary>
/// Undo is asked for while the user is still holding Ctrl (Ctrl+Z, 4.8.a) or Ctrl+Alt (the shortcut, 4.8.b). Its keys
/// must not arrive as Ctrl+T, Ctrl+Right and so on, so the request waits until the hook has seen the modifiers let go,
/// for at most <paramref name="maxWaitMs"/>; after that the writer's own release-and-restore takes over (found by E2E:
/// an undo in a WPF box deleted the word and lost the retyped one).
/// </summary>
public sealed class DeferredUndo(Func<bool> modifiersDown, Func<long> nowMs, int maxWaitMs = 1000)
{
    private (bool Global, Action<UndoResult>? Done, long Since)? _pending;

    public bool Pending => _pending is not null;

    public void Request(bool global, Action<UndoResult>? done = null) => _pending = (global, done, nowMs());

    /// <summary>Called on every engine-loop pass; runs the undo once the keys are up.</summary>
    public void Run(Func<bool, UndoResult> undo)
    {
        if (_pending is not { } p) return;
        if (modifiersDown() && nowMs() - p.Since < maxWaitMs) return;
        _pending = null;
        var result = undo(p.Global);   // not inside ?.Invoke(...): with no callback the argument would never run
        p.Done?.Invoke(result);
    }
}
