using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Telemetry;
using GlobalAutocorrect.Core.Text;
using GlobalAutocorrect.Core.Write;

namespace GlobalAutocorrect.Core.Ai;

/// <summary>
/// The AI layer (4.4): at a sentence end or a pause, sends the current sentence plus up to two before it, diffs the
/// cleaned text, applies the "no rewrite" rules and queues the surviving edits. The request runs while the user keeps
/// typing (12.3); only the write waits.
/// </summary>
public sealed class AiTrigger(EngineOptions options, IClock clock, ILlmClient llm, EditRules rules, Counters counters)
{
    private sealed record Pending(Task<LlmResult> Task, long RequestedAtMs, string Context, int ContextStartInTyped, long Generation, long KeySeq, long KeyAtMs);   // KeyAtMs: D64

    private Pending? _pending;
    private int _pendingShift;   // D45: own fixes written earlier in the text since the request went out

    /// <summary>
    /// D45: one of our fixes was written at [<paramref name="start"/>, <paramref name="end"/>) while a request is out.
    /// If it lies before that request's context, the reply's positions move by the same amount. Two lines finished with
    /// Enter in a row lost every fix of the second: the first line's fixes landed first and moved the text under it.
    /// </summary>
    public void NoteOwnEdit(int start, int end, int replacementLength)
    {
        if (_pending is not null && end <= _pending.ContextStartInTyped + _pendingShift)
            _pendingShift += replacementLength - (end - start);
    }
    private long _lastTriggerSeq = -1;
    private bool _rerun;

    public bool InFlight => _pending is not null;

    /// <summary>D38: fixes obvious misspellings the model's reply left behind. Null turns it off ("Cautious").</summary>
    public NonWordSweep? Sweep { get; init; }

    /// <summary>D43: characters taken in at the front of the typed buffer, so queued writes can move with them.</summary>
    public event Action<int>? Extended;

    /// <summary>D49: true for a word that is obviously not a word (no dictionary, personal dictionary or UK word list).</summary>
    public Func<string, bool>? IsObviousNonWord { get; init; }

    private readonly HashSet<string> _revisited = new(StringComparer.Ordinal);
    private long _revisitGeneration = -1;
    private const int MaxRevisitsPerSession = 20;

    /// <summary>
    /// D49 (the user's idea): once the user has paused, look back over everything typed in this stretch (the typed
    /// buffer, up to 4,000 characters, across paragraphs) for a finished sentence that still holds an obvious
    /// misspelling, and send that sentence (with the one before it, for context) to the AI again. Each sentence once.
    /// The first pass can miss: the model's fix was refused, or arrived while the text was moving.
    /// </summary>
    public bool TryRevisit(TypingSession session, ITextTarget target, Func<string, bool> isDesynced)
    {
        if (IsObviousNonWord is null || _pending is not null || clock.NowMs - session.LastKeyAtMs < options.RevisitAfterMs) return false;
        if (_revisitGeneration != session.Generation) { _revisited.Clear(); _revisitGeneration = session.Generation; }
        if (_revisited.Count >= MaxRevisitsPerSession) return false;
        string typed = session.Typed;
        var spans = FinishedSentences(typed);
        for (int k = spans.Count - 1; k >= 0; k--)
        {
            var (s, e) = spans[k];
            string key = s + ":" + typed[s..e];
            if (_revisited.Contains(key) || !HasObviousNonWord(typed[s..e])) continue;
            _revisited.Add(key);
            var win = target.ReadWindow();
            if (!win.CaretKnown || isDesynced(win.TargetId) || !session.MatchesField(win.Before)) return false;
            int ctxStart = k > 0 && !typed.AsSpan(spans[k - 1].Start, s - spans[k - 1].Start).Contains('\n') ? spans[k - 1].Start : s;
            string ctx = typed[ctxStart..e];
            counters.AiRequest();
            _pendingShift = 0;
            _pending = new Pending(llm.CleanAsync(ctx, CancellationToken.None), clock.NowMs, ctx, ctxStart, session.Generation, session.LastKeySeq,
                KeyAtMs: -1);   // D64: a look-back, seconds after the last key by design: not a wait the user feels
            return true;
        }
        return false;
    }

    private bool HasObviousNonWord(string sentence) =>
        Tokeniser.Tokenise(sentence).Any(t => t.IsWord && !t.IsProtected && t.Length >= 2 && char.IsLower(t.Text[0])
                                              && t.Text.All(char.IsLetter) && IsObviousNonWord!(t.Text));

    /// <summary>[start, end) of each sentence that has been finished (a full stop, ! or ? then a space, or a line break).</summary>
    private static List<(int Start, int End)> FinishedSentences(string s)
    {
        var list = new List<(int, int)>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            bool stop = s[i] == '\n' || (s[i] is '.' or '!' or '?' && i + 1 < s.Length && char.IsWhiteSpace(s[i + 1]));
            if (!stop) continue;
            int end = s[i] == '\n' ? i : i + 1;
            if (end > start && s[start..end].Trim().Length > 0) list.Add((start, end));
            start = i + 1;
            while (start < s.Length && s[start] == ' ') start++;
        }
        return list;
    }

    /// <summary>4.4.a: true when the user has paused long enough and typed something since the last request.</summary>
    public bool PauseDue(TypingSession session) =>
        session.Length > 0 && session.LastKeySeq != _lastTriggerSeq && clock.NowMs - session.LastKeyAtMs >= options.PauseMs
        && clock.NowMs >= _retryAtMs;

    // A read that fails (a busy app) doesn't use up the trigger: retry shortly, a few times (found by E2E in ProseMirror).
    private long _retryAtMs = long.MinValue;
    private long _failedSeq = -1;
    private int _failedReads;
    private bool _retryPending;
    // D65: browsers draw a key tens of milliseconds after it arrives, so a read at the sentence end often hasn't caught
    // up. It used to wait for the 700 ms pause trigger (CI timings: a third of browser AI fixes took ~900 ms with an
    // instant model). Now it tries again every 40 ms, for up to 800 ms, while no new key arrives.
    private const int RetryAfterMs = 40, MaxReadRetries = 20;

    private void ReadFailed(TypingSession session)
    {
        if (_failedSeq != session.LastKeySeq) { _failedSeq = session.LastKeySeq; _failedReads = 0; }
        if (++_failedReads >= MaxReadRetries) _lastTriggerSeq = session.LastKeySeq;   // give up on this text: a password box etc.
        _retryAtMs = clock.NowMs + RetryAfterMs;
        _retryPending = _failedReads < MaxReadRetries;
    }

    /// <summary>D65: a read that failed or lagged is due another try (no key has arrived since).</summary>
    public bool RetryDue(TypingSession session) =>
        _retryPending && _pending is null && session.LastKeySeq == _failedSeq && clock.NowMs >= _retryAtMs;

    public void Start(TypingSession session, ITextTarget target, Func<string, bool> isDesynced)
    {
        if (_pending is not null) { _rerun = true; return; }
        _retryPending = false;   // set again below if this read fails too (D65)
        var win = target.ReadWindow();
        if (!win.CaretKnown) { ReadFailed(session); return; }   // 4.1.b, 10.6.c (retried shortly)
        if (isDesynced(win.TargetId)) { _lastTriggerSeq = session.LastKeySeq; return; }
        if (!session.MatchesField(win.Before))
        {
            if (session.FieldBehind(win.Before)) { ReadFailed(session); return; }   // the app hasn't drawn the last keys yet
            // D42: the field differs from what was typed (the app's own autocorrect, a paste): carry on from what it shows.
            counters.FieldMismatch();
            if (!session.Resync(win.Before)) { ReadFailed(session); return; }   // D61: the read is behind the keys
        }
        _lastTriggerSeq = session.LastKeySeq;
        // D43: the whole paragraph, not only what was typed since the last click or paste.
        if (options.FixWholeParagraph && session.ExtendToParagraph(win.Before) is > 0 and var added) Extended?.Invoke(added);

        // D42: the context comes from the typed buffer, which was just checked against the field (and reaches back to the
        // paragraph start, D43), so edit positions are in typed coordinates by construction. Taken from the field text,
        // they were off by one for every blank line an editor reports as a single break.
        string before = session.Typed;
        const int typedStart = 0;
        // D8: one paragraph only. If the caret is just after Enter, that's the paragraph that has just ended: it used to
        // be the empty new one, so a paragraph finished with Enter never reached the AI at all (user report, Intercom).
        int end = before.Length;
        while (end > 0 && before[end - 1] == '\n' && end > typedStart) end--;
        int paraStart = end == 0 ? 0 : before.LastIndexOf('\n', end - 1) + 1;
        int ctxStart = paraStart + SentenceSplitter.StartOfLastSentences(before[paraStart..end], options.ContextSentences);   // 4.4.b
        ctxStart = Math.Max(ctxStart, paraStart);
        string ctx = before[ctxStart..];
        if (ctx.Trim().Length == 0 || before.Length <= Math.Max(ctxStart, typedStart)) return;

        counters.AiRequest();
        _pendingShift = 0;
        _pending = new Pending(llm.CleanAsync(ctx, CancellationToken.None), clock.NowMs, ctx, ctxStart - typedStart,
            session.Generation, session.LastKeySeq, session.LastKeyAtMs);
    }

    /// <summary>
    /// Collects a finished result. It is only used once the clock has moved past request time plus the model's
    /// latency, which in production is always true and lets the eval run on virtual time with real model latency.
    /// </summary>
    public IReadOnlyList<PendingWrite> Poll(TypingSession session, out bool rerun)
    {
        rerun = false;
        var p = _pending;
        if (p is null || !p.Task.IsCompleted) return [];
        if (p.Task.IsFaulted || p.Task.IsCanceled)
        {
            _pending = null;
            counters.AiFailure();
            rerun = TakeRerun();
            return [];
        }
        var result = p.Task.Result;
        if (clock.NowMs < p.RequestedAtMs + (long)result.LatencyMs) return [];
        _pending = null;
        rerun = TakeRerun();
        LastLatencyMs = result.LatencyMs;
        counters.Timings.Record("ai.model", result.LatencyMs);   // D64
        _recent.Enqueue(result.LatencyMs);
        if (_recent.Count > 20) _recent.Dequeue();
        if (p.Generation != session.Generation) return [];

        string cleaned = AlignToContext(p.Context, result.Text);
        var verdicts = rules.Filter(p.Context, cleaned, WordDiff.Compute(p.Context, cleaned));
        counters.Rejected(verdicts.Verdicts.Count(v => !v.Accepted));
        var writes = new List<PendingWrite>();
        foreach (var e in verdicts.Accepted)
        {
            int s = p.ContextStartInTyped + _pendingShift + e.Start, en = p.ContextStartInTyped + _pendingShift + e.End;
            if (s < 0) continue;   // D5: outside what the user typed this session
            writes.Add(new PendingWrite(Layer.Ai, p.Generation, s, en, p.Context[e.Start..e.End], e.Replacement, clock.NowMs, p.KeySeq, e.OriginalWords) { KeyAtMs = p.KeyAtMs });
        }
        if (Sweep is not null && rules.Settings.LenientWithNonWords)
        {
            foreach (var f in Sweep.Find(p.Context, verdicts.Accepted, verdicts.Verdicts.Where(v => !v.Accepted).Select(v => v.Edit).ToArray()))
            {
                int s = p.ContextStartInTyped + _pendingShift + f.Start;
                if (s < 0) continue;   // D5
                writes.Add(new PendingWrite(Layer.Ai, p.Generation, s, p.ContextStartInTyped + _pendingShift + f.End, f.Original, f.Replacement, clock.NowMs, p.KeySeq, [f.Original]) { KeyAtMs = p.KeyAtMs });
            }
        }
        return writes;
    }

    /// <summary>
    /// The model returns trimmed text and tends to end every reply with a full stop. Keep the user's own leading and
    /// trailing whitespace (the space they just typed must survive), and don't add closing punctuation to a sentence the
    /// user hasn't finished: they are still typing after it.
    /// </summary>
    public static string AlignToContext(string context, string modelText)
    {
        string trimmed = context.TrimEnd();
        string lead = context[..(context.Length - context.TrimStart().Length)];
        string trail = context[trimmed.Length..];
        string text = modelText.Trim();
        bool userEnded = trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?';
        if (!userEnded && text.Length > 0 && text[^1] is '.' or '!' && !trimmed.EndsWith(text[^1]))
            text = text[..^1];
        return lead + text + trail;
    }

    private readonly Queue<double> _recent = new();

    /// <summary>Median of the last 20 clean-ups: the "typical clean-up time" on the Home page (9.3.a).</summary>
    public double TypicalLatencyMs
    {
        get
        {
            if (_recent.Count == 0) return 0;
            var s = _recent.Order().ToArray();
            return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
        }
    }

    /// <summary>Latency of the last completed request (6.3.d, 9.3.a).</summary>
    public double LastLatencyMs { get; private set; }

    private bool TakeRerun()
    {
        bool r = _rerun;
        _rerun = false;
        return r;
    }
}
