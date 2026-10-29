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

    /// <summary>Each physical key, after the engine has seen it (prediction 7.x, model reload 5.6.b).</summary>
    public event Action<KeyInput>? KeyProcessed;
    public event Action? SuggestionAccepted;

    public EngineLoop(CorrectionEngine engine, KeyboardHook hook)
    {
        _engine = engine;
        _hook = hook;
        // 4.8.a: Ctrl+Z may take back a correction only if nothing happens between the write and the key.
        engine.Corrected += n => hook.UndoArm.ArmAt(n.KeySeqAtWrite);
        _thread = new Thread(Run) { IsBackground = true, Name = "GA engine" };
    }

    public void Start() => _thread.Start();

    /// <summary>Swaps in an engine built from new settings; runs on the engine thread, so no call is ever split.</summary>
    public void Replace(CorrectionEngine engine) => Post(_ =>
    {
        engine.Corrected += n => _hook.UndoArm.ArmAt(n.KeySeqAtWrite);
        engine.SetTarget(_engine.Target);
        engine.Paused = _engine.Paused;
        _engine = engine;
    });

    public void Post(Action<CorrectionEngine> work)
    {
        _posted.Enqueue(work);
        _wake.Set();
    }

    private void Run()
    {
        var reader = _hook.Events;
        while (!_stop)
        {
            while (reader.TryRead(out var e)) Handle(e);
            while (_posted.TryDequeue(out var work)) work(_engine);
            _engine.Tick();
            // Wake as soon as input arrives, or at the next tick.
            var waitInput = reader.WaitToReadAsync().AsTask();
            WaitHandle.WaitAny([((IAsyncResult)waitInput).AsyncWaitHandle, _wake], 25);
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
                _engine.OnInput(new KeyInput(KeyKind.Click, '\0', e.Seq, e.AtMs));
                break;
            case RawKind.UndoLocal:
                _engine.Undo(global: false);
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
