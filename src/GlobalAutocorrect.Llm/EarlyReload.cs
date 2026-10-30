using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Telemetry;

namespace GlobalAutocorrect.Llm;

/// <summary>
/// D67 (5.6.b, 12.3): after an idle unload, starts reloading the model as soon as the user shows they are about to type,
/// so the first fix rarely waits the ~3 s a load takes. The signs: keyboard focus moving into a field the app would
/// correct, or a key, whichever comes first. Nothing loads while the app is paused, for a field the app would leave
/// alone, or for the app's own windows. Focus only ever starts a load (<see cref="LlmRuntime.Wake"/>): it never restarts
/// one, and focus changes while the model is loaded don't count as activity, so the idle unload still comes after the
/// full idle time and moving around fields can't cause start/stop cycles.
/// Also records, as durations only (D64): <c>model.load</c>, <c>model.reloadWait</c> (the first key after an unload to
/// the model being ready: 0 when the early load had finished) and <c>model.reloadKeyToFix</c> (that key to the first AI fix).
/// </summary>
public sealed class EarlyReload
{
    private readonly LlmRuntime _llm;
    private readonly IClock _clock;
    private readonly Func<bool> _paused, _ownWindowFocused, _focusedFieldCorrectable;
    private readonly Timings? _timings;
    private readonly Action<Action> _background;
    private readonly Lock _gate = new();
    private bool _probing, _probeAgain;
    private bool _awaitingKey;          // unloaded after the idle time; no key since
    private long _keyAtMs = -1;         // the first key after the unload, until the first AI fix
    private bool _waitOpen;             // that key is waiting for the model

    /// <param name="focusedFieldCorrectable">Reads the focused field (UIA): true when the app would correct it. Run off the caller's thread.</param>
    /// <param name="background">Runs the field check; the thread pool unless a test says otherwise.</param>
    public EarlyReload(LlmRuntime llm, IClock clock, Func<bool> paused, Func<bool> ownWindowFocused, Func<bool> focusedFieldCorrectable,
        Timings? timings = null, Action<Action>? background = null)
    {
        _llm = llm;
        _clock = clock;
        _paused = paused;
        _ownWindowFocused = ownWindowFocused;
        _focusedFieldCorrectable = focusedFieldCorrectable;
        _timings = timings;
        _background = background ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        llm.IdleUnloaded += OnIdleUnloaded;
        llm.Loaded += OnLoaded;
    }

    /// <summary>Focused fields read so far (tests: a loaded model costs no reads).</summary>
    public int FieldChecks { get; private set; }

    /// <summary>Focus moved (focus thread). Costs nothing while the model is loaded or loading.</summary>
    public void OnFocusChanged()
    {
        if (!_llm.WouldLoad || _paused()) return;
        lock (_gate)
        {
            // One read at a time. A focus change during a read asks for one more afterwards: the field focus ended on.
            if (_probing) { _probeAgain = true; return; }
            _probing = true;
        }
        _background(CheckFocusedField);
    }

    private void CheckFocusedField()
    {
        while (true)
        {
            try
            {
                if (_llm.WouldLoad && !_paused() && !_ownWindowFocused())
                {
                    FieldChecks++;
                    if (_focusedFieldCorrectable()) _llm.Wake();
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }   // a failed read means "not a field we know"
            lock (_gate)
            {
                if (!_probeAgain) { _probing = false; return; }
                _probeAgain = false;
            }
        }
    }

    /// <summary>A typed character (engine thread). Counts as activity and loads the model if needed (5.6.b).</summary>
    public void OnKey()
    {
        if (_paused() || _ownWindowFocused()) return;   // D67: no load while paused, or for typing in the app's own windows
        lock (_gate)
        {
            if (_awaitingKey) { _awaitingKey = false; _keyAtMs = _clock.NowMs; _waitOpen = true; }
        }
        _llm.NotifyActivity();
        lock (_gate)
        {
            if (_waitOpen && _llm.Host.IsAlive) CloseWait();   // loaded already (the early load won): no wait
        }
    }

    /// <summary>An AI fix landed (engine thread).</summary>
    public void OnAiFix()
    {
        lock (_gate)
        {
            if (_keyAtMs < 0) return;
            _timings?.Record("model.reloadKeyToFix", _clock.NowMs - _keyAtMs);
            _keyAtMs = -1;
        }
    }

    private void OnIdleUnloaded()
    {
        lock (_gate)
        {
            _awaitingKey = true;
            _keyAtMs = -1;
            _waitOpen = false;
        }
    }

    private void OnLoaded(double loadMs)
    {
        _timings?.Record("model.load", loadMs);
        lock (_gate)
        {
            if (_waitOpen && _llm.Host.IsAlive) CloseWait();   // not a late report of an earlier load
        }
    }

    private void CloseWait()
    {
        _timings?.Record("model.reloadWait", _clock.NowMs - _keyAtMs);
        _waitOpen = false;
    }
}
