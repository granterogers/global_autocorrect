using System.Collections.Concurrent;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Session;

namespace GlobalAutocorrect.Windows.Input;

/// <summary>
/// The engine thread: drains hook events, runs work posted from elsewhere (focus changes, hotkeys, settings), and ticks
/// the engine every 25 ms. All engine calls happen here, so the engine needs no locking. The hook never waits on it.
/// </summary>
public sealed class EngineLoop : IDisposable
{
    private CorrectionEngine _engine;
    private readonly KeyboardHook _hook;
    private readonly KeyTranslator _translator = new();
    private readonly ConcurrentQueue<Action<CorrectionEngine>> _posted = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly DeferredUndo _undo;

    /// <summary>Each physical key, after the engine has seen it (prediction 7.x, model reload 5.6.b).</summary>
    public event Action<KeyInput>? KeyProcessed;
    public event Action? SuggestionAccepted;

    public EngineLoop(CorrectionEngine engine, KeyboardHook hook)
    {
        _engine = engine;
        _hook = hook;
        _undo = new DeferredUndo(() => hook.ModifiersDown, () => Environment.TickCount64);
        _thread = new Thread(Run) { IsBackground = true, Name = "GA engine" };
    }

    public void Start() => _thread.Start();

    /// <summary>Swaps in an engine built from new settings; runs on the engine thread, so no call is ever split.</summary>
    public void Replace(CorrectionEngine engine) => Post(_ =>
    {
        engine.SetTarget(_engine.Target);   // the new engine's paused state comes from the settings it was built with
        _engine = engine;
    });

    public void Post(Action<CorrectionEngine> work)
    {
        _posted.Enqueue(work);
        _wake.Set();
    }

    /// <summary>Undo (4.8.b shortcut) once the shortcut's modifiers are released; <paramref name="done"/> gets the result.</summary>
    public void Undo(bool global, Action<Core.Undo.UndoResult>? done = null) => Post(_ => _undo.Request(global, done));

    /// <summary>Steps that threw and were skipped. Counts only; the loop itself never dies (10.3.b).</summary>
    public int Errors { get; private set; }

    /// <summary>A focus change or other non-key input, fed in from another thread.</summary>
    public void Feed(KeyInput k) => Post(e =>
    {
        e.OnInput(k);
        KeyProcessed?.Invoke(k);
    });

    private void Run()
    {
        var reader = _hook.Events;
        Task<bool>? waitInput = null;
        while (!_stop)
        {
            while (reader.TryRead(out var e)) Guard(() => Handle(e));
            while (_posted.TryDequeue(out var work)) Guard(() => work(_engine));
            if (_undo.Pending) Guard(() => _undo.Run(_engine.Undo));
            Guard(_engine.Tick);
            // Wake as soon as input arrives, or at the next tick. One pending wait is reused until it completes.
            if (waitInput is null || waitInput.IsCompleted) waitInput = reader.WaitToReadAsync().AsTask();
            WaitHandle.WaitAny([((IAsyncResult)waitInput).AsyncWaitHandle, _wake], _undo.Pending ? 5 : 25);
        }
    }

    /// <summary>One bad event (a hung or odd app, a race) must never take the whole app and its keyboard hook down.</summary>
    private void Guard(Action step)
    {
        try { step(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Errors++;
        }
    }

    private void Handle(RawInput e)
    {
        switch (e.Kind)
        {
            case RawKind.Key:
                foreach (var k in _translator.Translate(e))
                {
                    _engine.OnInput(k);
                    KeyProcessed?.Invoke(k);
                }
                break;
            case RawKind.MouseDown:
                var click = new KeyInput(KeyKind.Click, '\0', e.Seq, e.AtMs);
                _engine.OnInput(click);
                KeyProcessed?.Invoke(click);   // e.g. the suggestion overlay hides on a click (7.5)
                break;

            case RawKind.AcceptSuggestion:
                SuggestionAccepted?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(1000);
        _wake.Dispose();
    }
}
