using GlobalAutocorrect.Core.Ai;
using GlobalAutocorrect.Core.Instant;
using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Telemetry;
using GlobalAutocorrect.Core.Text;
using GlobalAutocorrect.Core.Undo;
using GlobalAutocorrect.Core.Write;

namespace GlobalAutocorrect.Core.Engine;

/// <summary>What the engine needs from outside. The personal dictionary is passed as two delegates.</summary>
public sealed record EngineDeps(
    IClock Clock,
    IKeyMonitor Keys,
    ISpellChecker Speller,
    ISuggester Suggester,
    ILlmClient? Llm,
    Func<string, bool> IsPersonalWord,
    Func<string, bool> AddPersonalWord,
    Counters Counters,
    Action<string>? ObserveWord = null,           // 7.3: prediction history, word counts only
    Func<string, bool>? NoteUnrecognised = null,  // 8.5: auto-learn after 3 uses; true when the word was added
    Action<string?, string?, string>? ObservePhrase = null,  // D40: (two back, one back, word) within a sentence
    Func<string, string, long>? PairCount = null,  // D37/D38: corpus count of a word pair
    Func<string, long>? WordCount = null);         // D37: corpus count of a word

/// <summary>
/// Feature A end to end (0.2): instant layer on each finished word, AI layer on sentence ends and pauses, all writes
/// through the write gate, plus undo. Not thread-safe: the host calls it from one thread. Typing never waits on it (4.6.e).
/// </summary>
public sealed class CorrectionEngine
{
    private readonly EngineOptions _o;
    private readonly EngineDeps _d;
    private readonly InstantCorrector _instant;
    private readonly TidyRules _tidy;
    private readonly AiTrigger? _ai;
    private readonly WriteGate _gate;
    private readonly UndoManager _undo = new();

    public TypingSession Session { get; } = new();
    public ITextTarget? Target { get; private set; }
    public bool Paused { get; set; }
    public Counters Counters => _d.Counters;
    public double LastAiLatencyMs => _ai?.LastLatencyMs ?? 0;
    public double TypicalAiLatencyMs => _ai?.TypicalLatencyMs ?? 0;
    public bool AiInFlight => _ai?.InFlight ?? false;
    public int QueuedWrites => _gate.Count;
    public string LastDropReason => _gate.LastDropReason;
    public CorrectionRecord? LastCorrection => _undo.Last;

    /// <summary>After each verified write: underline it (4.7).</summary>
    public event Action<CorrectionNotice>? Corrected;
    public event Action<string>? Desynced;

    public CorrectionEngine(EngineOptions options, EngineDeps deps)
    {
        _o = options.Validate();
        _d = deps;
        _instant = new InstantCorrector(deps.Speller, deps.Suggester, deps.IsPersonalWord);
        _tidy = new TidyRules(deps.Speller);
        if (deps.Llm is not null)
            _ai = new AiTrigger(_o, deps.Clock, deps.Llm, new EditRules(_o.Rules, deps.Speller, deps.IsPersonalWord) { PairCount = deps.PairCount, WordCount = deps.WordCount }, deps.Counters)
            {
                Sweep = deps.PairCount is null ? null : new NonWordSweep(deps.Speller, deps.Suggester, deps.IsPersonalWord, deps.PairCount),
                // D49: not in the dictionary, the personal dictionary or the UK word list.
                IsObviousNonWord = _o.RevisitAfterMs <= 0 ? null : w => !deps.Speller.IsValid(w) && !deps.IsPersonalWord(w)
                    && !(deps.Suggester.Lookup(w.ToLowerInvariant(), 1) is [{ Distance: 0 }]),
            };
        _gate = new WriteGate(_o, deps.Clock, deps.Keys, Session, deps.Counters);
        if (_ai is not null) _ai.Extended += _gate.Shift;   // D43
        _gate.Written += (notice, w, typedAfter) =>
        {
            if (w.Layer != Layer.Undo) _undo.Record(notice, w, typedAfter, Session.Generation);
            _ai?.NoteOwnEdit(w.Start, w.End, w.Replacement.Length);   // D45
            _pendingLearn.RemoveAll(p => w.Words.Contains(p.Word, StringComparer.OrdinalIgnoreCase));   // it was a typo
            Corrected?.Invoke(notice);
        };
        _gate.Desynced += id => { _undo.Clear(); Desynced?.Invoke(id); };
    }

    /// <summary>Focus moved to another field (or none). Breaks the typing session.</summary>
    public void SetTarget(ITextTarget? target)
    {
        _targetOk = null;
        Target = target;
        Session.Break(SessionBreakReason.FocusChange);
        _gate.Clear();
    }

    public void OnInput(KeyInput k)
    {
        var effect = Session.Apply(k);
        if (k.Kind is KeyKind.FocusChange or KeyKind.Click) _targetOk = null;   // possibly a different field now
        if (Paused || Target is null) return;

        string? fix = null;
        if (effect.Word is { } fw && _o.InstantEnabled)
        {
            fix = _instant.TryCorrect(fw.Word, fw.IsSentenceStart, fw.PreviousWord);   // 4.3
            if (fix is not null)
            {
                _gate.Enqueue(new PendingWrite(Layer.Instant, Session.Generation, fw.Start, fw.End, fw.Word, fix, _d.Clock.NowMs, k.Seq));
                _gate.TryWrite(Target);   // straight away if nothing else was typed (4.3.a)
            }
        }
        bool aiStartsNow = effect.SentenceEnded && _ai is not null && _o.AiEnabled;
        // D55: spacing and punctuation, decided at the space or Enter after them (only when no word was fixed on this key).
        // On a key that sends the sentence to the AI, the AI fixes it instead: an instant edit inside a request's text
        // would leave that request's positions out of date, and one of the two writes would be dropped.
        if (fix is null && !aiStartsNow && _o.InstantEnabled && _o.TidySpacing && k.Kind == KeyKind.Char && char.IsWhiteSpace(k.Char)
            && _tidy.Find(Session.Typed) is { } t)
        {
            _gate.Enqueue(new PendingWrite(Layer.Instant, Session.Generation, t.Start, t.End, t.Original, t.Replacement, _d.Clock.NowMs, k.Seq));
            _gate.TryWrite(Target);
        }
        if (effect.Word is { } learnt) Learn(learnt.Word, corrected: fix is not null, learnt.Start, fix);
        if (aiStartsNow)
            _ai!.Start(Session, Target, _gate.IsDesynced);   // 4.4.a sentence end
    }

    private bool? _targetOk;

    /// <summary>
    /// Words are learnt (prediction history 7.3, auto-learn 8.5) only from a field the app may read: never a password
    /// field, an excluded app or an elevated window (8.3, 8.4, 10.8), and never while paused. The field is checked once
    /// per focus, not per word.
    /// </summary>
    private (long Generation, int Start) _lastLearnt = (-1, -1);

    private void Learn(string word, bool corrected, int start, string? fix)
    {
        if (_d.ObserveWord is null && _d.NoteUnrecognised is null && _d.ObservePhrase is null) return;
        // A word is finished by its punctuation and again by the space after it ("sir? "): learn it once.
        if (_lastLearnt == (Session.Generation, start)) return;
        _lastLearnt = (Session.Generation, start);
        _targetOk ??= Target!.ReadWindow().CaretKnown;
        if (_targetOk != true) return;
        string meant = fix ?? word;   // learn what the user meant: the instant fix if there was one
        if (_d.Speller.IsValid(meant) || _d.IsPersonalWord(meant))
        {
            _d.ObserveWord?.Invoke(meant.ToLowerInvariant());
            if (_d.ObservePhrase is not null)
            {
                var (w1, w2) = WordsBefore(start);
                _d.ObservePhrase(w1, w2, meant);
            }
        }
        else if (!corrected && _d.NoteUnrecognised is not null) _pendingLearn.Add((word, _d.Clock.NowMs));
    }

    private static readonly System.Text.RegularExpressions.Regex WordOrStop =
        new(@"\p{L}+(?:['’]\p{L}+)*|[.!?\n]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// D40: the two words before <paramref name="start"/> in the same sentence of what was typed; a sentence start is
    /// <c>"&lt;s&gt;"</c>. Before what this session typed, nothing is known, so the context is shorter rather than guessed.
    /// </summary>
    private (string? W1, string? W2) WordsBefore(int start)
    {
        var tokens = WordOrStop.Matches(Session.Typed[..Math.Min(start, Session.Typed.Length)]).Select(m => m.Value).ToList();
        static bool Stop(string t) => t is "." or "!" or "?" or "\n";
        const string S = Prediction.UserHistory.SentenceStart;
        if (tokens.Count == 0) return (null, null);                     // start of what this session saw: unknown
        if (Stop(tokens[^1])) return (null, S);                         // first word of a sentence
        string? w1 = tokens.Count < 2 ? null : Stop(tokens[^2]) ? S : tokens[^2];
        return (w1, tokens[^1]);
    }

    /// <summary>
    /// An unrecognised word only counts towards auto-learn (8.5) once it has survived a few seconds without being
    /// corrected; a typo the AI keeps fixing must never end up in the dictionary.
    /// </summary>
    private readonly List<(string Word, long At)> _pendingLearn = [];
    private const int LearnAfterMs = 5000;

    private void SettleLearning()
    {
        long now = _d.Clock.NowMs;
        for (int i = _pendingLearn.Count - 1; i >= 0; i--)
        {
            if (now - _pendingLearn[i].At < LearnAfterMs) continue;
            _d.NoteUnrecognised!(_pendingLearn[i].Word);
            _pendingLearn.RemoveAt(i);
        }
    }

    /// <summary>Called every ~25 ms (or as virtual time advances): pause trigger, AI results, queued writes.</summary>
    public void Tick()
    {
        if (Paused || Target is null) return;
        if (_pendingLearn.Count > 0) SettleLearning();
        if (_ai is not null && _o.AiEnabled)
        {
            var ready = _ai.Poll(Session, out bool rerun);
            foreach (var w in ready) _gate.Enqueue(w);
            if (rerun || _ai.PauseDue(Session)) _ai.Start(Session, Target, _gate.IsDesynced);   // 4.4.a pause
            else if (_gate.Count == 0) _ai.TryRevisit(Session, Target, _gate.IsDesynced);       // D49: look back once paused
        }
        _gate.TryWrite(Target);
    }

    /// <summary>
    /// Types an accepted suggestion at the caret (7.4) as our own tagged input, and adds it to the typed buffer so the
    /// session stays in step with the field.
    /// </summary>
    public bool InsertAtCaret(string text)
    {
        if (Target is null || text.Length == 0 || Paused) return false;
        // Same gate as every write: never into a password field, excluded app or elevated window (8.3, 8.4, 10.8).
        if (!Target.ReadWindow().CaretKnown) return false;
        if (!Target.ApplyEdit(0, 0, text)) return false;
        Session.ApplyOwnEdit(Session.Length, Session.Length, text);
        return true;
    }

    /// <summary>
    /// Undo the most recent correction (4.8). Global = the shortcut, from anywhere in the same field (4.8.b, D9); the app
    /// only uses this. Local = straight after it, nothing typed and the caret unmoved (4.8.a): the app no longer takes
    /// Ctrl+Z (D34), so local is only reached from tests.
    /// </summary>
    public UndoResult Undo(bool global)
    {
        var rec = _undo.Last;
        if (rec is null) return UndoResult.NothingToUndo;
        if (Target is null) return UndoResult.Failed;
        if (global)
        {
            // D9: only the field that holds the correction; never act on whatever else has focus.
            var probe = Target.ReadWindow();
            if (!probe.CaretKnown) return UndoResult.Failed;
            if (probe.TargetId != rec.TargetId) return UndoResult.OtherField;
        }

        UndoResult result;
        string typed = Session.Typed;
        int end = rec.Start + rec.Replacement.Length;
        if (rec.Generation == Session.Generation && end <= typed.Length && typed[rec.Start..end] == rec.Replacement)
        {
            _gate.Enqueue(new PendingWrite(Layer.Undo, Session.Generation, rec.Start, end, rec.Replacement, rec.Original, _d.Clock.NowMs, _d.Keys.LatestKeySeq));
            result = _gate.TryWrite(Target) ? UndoResult.Reverted : UndoResult.Failed;
        }
        else if (!global)
        {
            return UndoResult.TextChanged;
        }
        else
        {
            var win = Target.ReadWindow();
            if (!win.CaretKnown) return UndoResult.Failed;
            if (win.TargetId != rec.TargetId) return UndoResult.OtherField;
            if (UndoManager.Locate(rec, win.Before) is not { } at) return UndoResult.TextChanged;
            if (!Target.ApplyEdit(at.CaretDistance, at.DeleteCount, rec.Original)) return UndoResult.Failed;
            string before = TextNorm.Normalise(win.Before);
            string expected = before[..at.ReplacementStart] + rec.Original + before[(at.ReplacementStart + rec.Replacement.Length)..];
            if (!Target.Verify(expected, win.After)) { _d.Counters.Desync(); return UndoResult.Failed; }
            Session.Break(SessionBreakReason.Navigation);   // the typed buffer no longer lines up with the field
            result = UndoResult.Reverted;
        }

        if (result == UndoResult.Reverted)
        {
            foreach (var w in UndoManager.WordsToLearn(rec)) _d.AddPersonalWord(w);   // 4.8.c
            _d.Counters.Undo();
            _undo.Clear();
        }
        return result;
    }
}
