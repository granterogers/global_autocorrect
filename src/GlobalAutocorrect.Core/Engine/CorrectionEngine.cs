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
    Func<string, long>? WordCount = null,          // D37: corpus count of a word
    Action<string?>? PhraseWord = null);           // D142: each plain word finished, null where a phrase must stop

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

    /// <summary>Tests only: queue a write as if a layer had decided it (D127: a late answer for a word already fixed).</summary>
    public void EnqueueForTest(PendingWrite w) => _gate.Enqueue(w);
    public string LastDropReason => _gate.LastDropReason;
    public CorrectionRecord? LastCorrection => _undo.Last;

    /// <summary>D81: what the app learns per program and how carefully it writes there.</summary>
    public Health.AppHealth? Health { get => _gate.Health; set => _gate.Health = value; }

    /// <summary>D81: a field was given up on as stuck (program name only).</summary>
    public event Action<string>? Stuck;

    /// <summary>D84: changes that turned out to be mistakes, never made again.</summary>
    public Health.MistakeMemory? Mistakes { get => _gate.Mistakes; set => _gate.Mistakes = value; }

    /// <summary>D84: something was learnt from a mistake: "undone", "typed-back", "guard-..." or "case-repaired" (never text).</summary>
    public event Action<string>? LearntFromMistake;

    /// <summary>
    /// D84: the user finished a word exactly where one of our recent fixes was, and it is the word as it was before the
    /// fix: they typed it back. That fix was a mistake, as surely as an undo (4.8.c), and is treated the same way.
    /// </summary>
    private void NoticeTypedBack(FinishedWord fw)
    {
        foreach (var rec in _undo.Recent.ToArray())
        {
            if (rec.Generation != Session.Generation || rec.WordsStart != fw.Start) continue;
            if (!string.Equals(fw.Word, rec.WordsBefore, StringComparison.OrdinalIgnoreCase) || string.Equals(fw.Word, rec.WordsAfter, StringComparison.Ordinal)) continue;
            _undo.Recent.Remove(rec);
            Mistakes?.Remember(rec.WordsBefore, rec.WordsAfter);
            foreach (var w in UndoManager.WordsToLearn(rec)) _d.AddPersonalWord(w);   // as an undo does (4.8.c)
            _d.Counters.Undo();
            Health?.Undone(Target?.AppName ?? "");
            LearntFromMistake?.Invoke("typed-back");
        }
    }

    /// <summary>After each verified write: underline it (4.7).</summary>
    public event Action<CorrectionNotice>? Corrected;
    public event Action<string>? Desynced;

    /// <summary>A queued write that was dropped and why (a reason code, never text).</summary>
    public event Action<Layer, string>? WriteDropped;

    /// <summary>D74: the field didn't match the typed record before a write (codes and lengths only).</summary>
    public event Action<string>? Mismatch;

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
        if (_ai is not null) _ai.Mismatch += d => Mismatch?.Invoke(d);   // D76
        _gate.Written += (notice, w, typedAfter) =>
        {
            if (w.Layer != Layer.Undo) _undo.Record(notice, w, typedAfter, Session.Generation);
            _ai?.NoteOwnEdit(w.Start, w.End, w.Replacement.Length);   // D45
            _pendingLearn.RemoveAll(p => w.Words.Contains(p.Word, StringComparer.OrdinalIgnoreCase));   // it was a typo
            Corrected?.Invoke(notice);
        };
        _gate.Desynced += id => { _undo.Clear(); Desynced?.Invoke(id); };
        _gate.WriteDropped += (layer, reason) => WriteDropped?.Invoke(layer, reason);
        _gate.Mismatch += d => Mismatch?.Invoke(d);
        _gate.Stuck += app => Stuck?.Invoke(app);   // D81
        _gate.CaseRepaired += _ => LearntFromMistake?.Invoke("case-repaired");   // D84
        _gate.ArrowsFailed += _ => LearntFromMistake?.Invoke("arrows-failed");   // D89
        _gate.CaretRepaired += (_, n) => LearntFromMistake?.Invoke($"caret-repaired short={n}");   // D99
        _gate.WriteDropped += (_, reason) => { if (reason.StartsWith("guard-", StringComparison.Ordinal) || reason == "known-mistake") LearntFromMistake?.Invoke(reason); };
        Session.Resynced += _gate.OnResynced;   // D74
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
        // D73: Chromium can announce focus on the field being typed in long after typing began (nightly CI: 22 characters
        // in, mid-word). If the focused field still shows exactly what was typed, it is the same field: carry on.
        if (k.Kind == KeyKind.FocusChange && Target is not null && Session.Length >= 3
            && Target.ReadWindow() is { CaretKnown: true } w && Session.MatchesField(w.Before))
            return;
        if (k.Kind == KeyKind.Char && k.Char == '\n') NoticeEnter();   // D154: before Apply, while the record still has the line
        var effect = Session.Apply(k);
        if (k.Kind is KeyKind.FocusChange or KeyKind.Click) _targetOk = null;   // possibly a different field now
        if (Paused || Target is null) return;

        if (effect.Word is { } typedBack && _undo.Recent.Count > 0) NoticeTypedBack(typedBack);   // D84: before it could be "fixed" again
        string? fix = null;
        // D116: a paragraph that reads as another language (Spanish...) is left as typed: no instant fixes, no tidying.
        bool foreign = (effect.Word is not null || (k.Kind == KeyKind.Char && char.IsWhiteSpace(k.Char))) && Text.LanguageGuess.IsForeign(CurrentParagraph());
        if (effect.Word is { } fw && _o.InstantEnabled && !foreign)
        {
            fix = InstantFixFor(fw);
            if (fix is not null)
            {
                _gate.Enqueue(new PendingWrite(Layer.Instant, Session.Generation, fw.Start, fw.End, fw.Word, fix, _d.Clock.NowMs, k.Seq) { WholeWord = true, CapitalIfSentenceStart = fw.Start == 0 && _o.TidySpacing });   // D167
                _gate.TryWrite(Target);   // straight away if nothing else was typed (4.3.a)
            }
        }
        bool aiStartsNow = effect.SentenceEnded && _ai is not null && _o.AiEnabled;
        // D55: spacing and punctuation, decided at the space or Enter after them (only when no word was fixed on this key).
        // On a key that sends the sentence to the AI, the AI fixes it instead: an instant edit inside a request's text
        // would leave that request's positions out of date, and one of the two writes would be dropped.
        if (fix is null && !aiStartsNow && !foreign && _o.InstantEnabled && _o.TidySpacing && k.Kind == KeyKind.Char && char.IsWhiteSpace(k.Char)
            && _tidy.Find(Session.Typed) is { } t)
        {
            _gate.Enqueue(new PendingWrite(Layer.Instant, Session.Generation, t.Start, t.End, t.Original, t.Replacement, _d.Clock.NowMs, k.Seq));
            _gate.TryWrite(Target);
        }
        if (effect.Word is { } learnt) Learn(learnt.Word, corrected: fix is not null, learnt.Start, fix, foreign);
        // D142: a phrase stops at a sentence end, a line break, and anything that isn't typing on (a click, a paste, a
        // shortcut, moving the caret, another field).
        if (effect.SentenceEnded || (k.Kind == KeyKind.Char && k.Char == '\n') || k.Kind is not (KeyKind.Char or KeyKind.Backspace))
            _d.PhraseWord?.Invoke(null);
        if (aiStartsNow)
            _ai!.Start(Session, Target, _gate.IsDesynced);   // 4.4.a sentence end
    }

    /// <summary>D116: the paragraph being typed, as far as the typed record goes.</summary>
    /// <summary>
    /// D141 (stress runs: the first word of a message, "fungers", stayed small): the typed record can't say what is before
    /// its first word, but the field can. A sentence starts there if nothing is before it, or a ".", "?", "!" or line break.
    /// </summary>
    /// <param name="wordEnd">Where the first word ends in the typed record: the field must show at least that much of it.</param>
    private bool FieldStartsASentenceHere(int wordEnd)
    {
        if (Target?.ReadWindow() is not { CaretKnown: true } w) return false;
        string before = TextNorm.Normalise(w.Before), typed = Session.Typed;
        // D149 (stress run: "fungers" small again, in the chat box): keys held during a placed write reach the field a
        // moment later, so the field can be a key or two behind the record. Any of the record's start that the field ends
        // with will do, as long as it includes the whole word.
        for (int shown = typed.Length; shown >= Math.Max(1, wordEnd); shown--)
        {
            if (!before.EndsWith(typed.AsSpan(0, shown), StringComparison.Ordinal)) continue;
            string pre = before[..^shown].TrimEnd(' ', '\t', '"', '\'', '(');
            return pre.Length == 0 || pre[^1] is '.' or '!' or '?' or '\n';
        }
        return false;
    }

    private string CurrentParagraph()
    {
        string t = Session.Typed;
        int end = t.Length > 0 && t[^1] == '\n' ? t.Length - 1 : t.Length;
        int start = end == 0 ? 0 : t.LastIndexOf('\n', end - 1) + 1;
        return t[start..end];
    }

    private bool? _targetOk;

    /// <summary>
    /// Words are learnt (prediction history 7.3, auto-learn 8.5) only from a field the app may read: never a password
    /// field, an excluded app or an elevated window (8.3, 8.4, 10.8), and never while paused. The field is checked once
    /// per focus, not per word.
    /// </summary>
    private (long Generation, int Start) _lastLearnt = (-1, -1);

    private void Learn(string word, bool corrected, int start, string? fix, bool foreign = false)
    {
        if (_d.ObserveWord is null && _d.NoteUnrecognised is null && _d.ObservePhrase is null && _d.PhraseWord is null) return;
        // A word is finished by its punctuation and again by the space after it ("sir? "): learn it once.
        if (_lastLearnt == (Session.Generation, start)) return;
        _lastLearnt = (Session.Generation, start);
        _targetOk ??= Target!.ReadWindow().CaretKnown;
        if (_targetOk != true) return;
        string meant = fix ?? word;   // learn what the user meant: the instant fix if there was one
        // D142: phrases are made of plain words only: a number, link, email, @mention, unknown word, pasted text or
        // another language stops the phrase there, so none of them is ever kept.
        bool plain = !foreign && start >= Session.ProtectedEnd && meant.All(c => char.IsLetter(c) || c is '\'' or '’' or '-')
                     && (_d.Speller.IsValid(meant) || _d.IsPersonalWord(meant));
        _d.PhraseWord?.Invoke(plain ? meant : null);
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
    private static int LearnAfterMs => Learning.Tunables.LearnAfterMs.Int;   // D155

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

    /// <summary>
    /// D154 (user: "hold Enter … until the fix is in"): Enter is held in a field that sends on Enter. Ask the AI about the
    /// line now, without waiting for the pause, so its fixes can go in before the message is sent.
    /// </summary>
    public void CheckLineNow()
    {
        if (Paused || Target is null || _ai is null || !_o.AiEnabled) return;
        if (_ai.Unchecked(Session) && !_ai.InFlight) _ai.Start(Session, Target, _gate.IsDesynced);
    }

    /// <summary>D154: an AI answer, a retry or a fix is still on its way for what has been typed.</summary>
    /// <summary>The instant fix for a finished word (4.3, D92), with a sentence start's capital (D141), or null.</summary>
    private string? InstantFixFor(FinishedWord fw)
    {
        string? fix = _instant.TryCorrect(fw.Word, fw.IsSentenceStart, fw.PreviousWord, Target!.NamesNearby);
        // D141 (stress runs: "fungers" at a line's start became "fingers"): D55 capitalises a sentence start only on a
        // key where no word was fixed, and the instant fix keeps the case as typed, so a fixed first word stayed small.
        // With tidying on, the fix carries the sentence start's capital itself.
        if (fix is { Length: > 0 } && _o.TidySpacing && char.IsLower(fix[0]) && (fw.IsSentenceStart || (fw.Start == 0 && FieldStartsASentenceHere(fw.End))))
            fix = char.ToUpperInvariant(fix[0]) + fix[1..];
        return fix;
    }

    private long _aheadSeq = -1, _aheadGeneration = -1;
    private bool _aheadFix;

    /// <summary>
    /// D156 (stress: "provkded." then Enter: Enter itself finished the word, so its fix was placed across the new line
    /// and the break was lost): would the word at the caret be fixed if it were finished now? Worked out once per key.
    /// </summary>
    public bool WordAtEndWouldBeFixed()
    {
        if (Paused || Target is null || !_o.InstantEnabled) return false;
        if (_aheadSeq == Session.LastKeySeq && _aheadGeneration == Session.Generation) return _aheadFix;
        _aheadSeq = Session.LastKeySeq; _aheadGeneration = Session.Generation;
        _aheadFix = Session.WordAtEnd() is { } fw && !Text.LanguageGuess.IsForeign(CurrentParagraph())
                    && _instant.TryCorrect(fw.Word, fw.IsSentenceStart, fw.PreviousWord, Target.NamesNearby) is not null;
        return _aheadFix;
    }

    /// <summary>
    /// D156: Enter is held. Its word is fixed now, on its own line, as the Enter would have had it fixed after; the
    /// Enter then goes once the fix is in. True if a fix was queued.
    /// </summary>
    public bool FinishWordForEnter()
    {
        if (Paused || Target is null || !_o.InstantEnabled || Session.WordAtEnd() is not { } fw) return false;
        if (Text.LanguageGuess.IsForeign(CurrentParagraph()) || InstantFixFor(fw) is not { } fix) return false;
        _gate.Enqueue(new PendingWrite(Layer.Instant, Session.Generation, fw.Start, fw.End, fw.Word, fix, _d.Clock.NowMs, _d.Keys.LatestKeySeq) { WholeWord = true, CapitalIfSentenceStart = fw.Start == 0 && _o.TidySpacing });   // D172
        _gate.TryWrite(Target);
        return true;
    }

    /// <summary>D156: an instant fix (the word just finished) is waiting to be written.</summary>
    public bool InstantFixPending => !Paused && Target is not null && _gate.Has(Layer.Instant);

    public bool LineFixesPending => !Paused && Target is not null && ((_ai is not null && _o.AiEnabled && (_ai.InFlight || _ai.RetryPending)) || _gate.Count > 0);

    // D154: just after Enter, one look at the field: empty means it sent the message (a chat box).
    private long _sendCheckAt;
    private bool _sendCheckHadText;

    private int _sendNewlineAt;

    private void NoticeEnter()
    {
        _sendCheckHadText = Session.Typed.TrimEnd('\n').Trim().Length > 0;
        _sendNewlineAt = Session.Length;   // where this Enter's line break goes in the record
        _sendCheckAt = _d.Clock.NowMs + Learning.Tunables.EnterSendCheckMs.Int;
    }

    private void CheckEnterSent()
    {
        if (_sendCheckAt == 0 || _d.Clock.NowMs < _sendCheckAt) return;
        _sendCheckAt = 0;
        if (!_sendCheckHadText || Health is null || Target is null) return;
        // D161: sent if the box shows only what was typed after this Enter, nothing of the message before it: empty, or
        // the start of the next message for a fast typist, who has usually begun it by the time of this look.
        string typed = Session.Typed;
        if (_sendNewlineAt >= typed.Length || typed[_sendNewlineAt] != '\n') return;   // the record has changed since
        string since = TextNorm.Comparable(typed[(_sendNewlineAt + 1)..]);
        if (Target.ReadWindow() is { CaretKnown: true } w && TextNorm.Comparable(w.Before) == since && TextNorm.Comparable(w.After).Trim().Length == 0)
        {
            Health.EnterSent(Target.FieldKey);
            // D161 (Teams, the user's PC: 201 "typed has a line break, the field a letter" mismatches in 3 days, up to 57
            // a minute): the message was sent and the box emptied, but the record kept the old text and every check
            // disagreed with the field. The record starts again from what the box shows.
            Session.Resync(w.Before);
        }
    }

    /// <summary>Called every ~25 ms (or as virtual time advances): pause trigger, AI results, queued writes.</summary>
    public void Tick()
    {
        if (Paused || Target is null) return;
        CheckEnterSent();
        if (_pendingLearn.Count > 0) SettleLearning();
        if (_ai is not null && _o.AiEnabled)
        {
            var ready = _ai.Poll(Session, out bool rerun);
            foreach (var w in ready) _gate.Enqueue(w);
            if (rerun || _ai.PauseDue(Session) || _ai.RetryDue(Session)) _ai.Start(Session, Target, _gate.IsDesynced);   // 4.4.a pause; D65 retry
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
            Health?.Undone(Target.AppName);   // D81: a fix the user didn't want
            Mistakes?.Remember(rec.WordsBefore, rec.WordsAfter);   // D84: and never that change again
            LearntFromMistake?.Invoke("undone");
            _undo.Clear();
        }
        return result;
    }
}
