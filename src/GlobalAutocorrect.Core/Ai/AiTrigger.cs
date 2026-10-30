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
    private sealed record Pending(Task<LlmResult> Task, long RequestedAtMs, string Context, int ContextStartInTyped, long Generation, long KeySeq);

    private Pending? _pending;
    private long _lastTriggerSeq = -1;
    private bool _rerun;

    public bool InFlight => _pending is not null;

    /// <summary>D38: fixes obvious misspellings the model's reply left behind. Null turns it off ("Cautious").</summary>
    public NonWordSweep? Sweep { get; init; }

    /// <summary>4.4.a: true when the user has paused long enough and typed something since the last request.</summary>
    public bool PauseDue(TypingSession session) =>
        session.Length > 0 && session.LastKeySeq != _lastTriggerSeq && clock.NowMs - session.LastKeyAtMs >= options.PauseMs
        && clock.NowMs >= _retryAtMs;

    // A read that fails (a busy app) doesn't use up the trigger: retry shortly, a few times (found by E2E in ProseMirror).
    private long _retryAtMs = long.MinValue;
    private long _failedSeq = -1;
    private int _failedReads;
    private const int RetryAfterMs = 300, MaxReadRetries = 3;

    private void ReadFailed(TypingSession session)
    {
        if (_failedSeq != session.LastKeySeq) { _failedSeq = session.LastKeySeq; _failedReads = 0; }
        if (++_failedReads >= MaxReadRetries) _lastTriggerSeq = session.LastKeySeq;   // give up on this text: a password box etc.
        _retryAtMs = clock.NowMs + RetryAfterMs;
    }

    public void Start(TypingSession session, ITextTarget target, Func<string, bool> isDesynced)
    {
        if (_pending is not null) { _rerun = true; return; }
        var win = target.ReadWindow();
        if (!win.CaretKnown) { ReadFailed(session); return; }   // 4.1.b, 10.6.c (retried shortly)
        if (isDesynced(win.TargetId)) { _lastTriggerSeq = session.LastKeySeq; return; }
        if (!session.MatchesField(win.Before))
        {
            if (session.FieldBehind(win.Before)) { ReadFailed(session); return; }   // the app hasn't drawn the last keys yet
            _lastTriggerSeq = session.LastKeySeq;
            counters.FieldMismatch();
            session.Break(SessionBreakReason.FieldMismatch);
            return;
        }
        _lastTriggerSeq = session.LastKeySeq;

        string before = TextNorm.Normalise(win.Before);
        int typedStart = before.Length - TextNorm.Normalise(session.Typed).Length;
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
        _pending = new Pending(llm.CleanAsync(ctx, CancellationToken.None), clock.NowMs, ctx, ctxStart - typedStart,
            session.Generation, session.LastKeySeq);
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
        _recent.Enqueue(result.LatencyMs);
        if (_recent.Count > 20) _recent.Dequeue();
        if (p.Generation != session.Generation) return [];

        string cleaned = AlignToContext(p.Context, result.Text);
        var verdicts = rules.Filter(p.Context, cleaned, WordDiff.Compute(p.Context, cleaned));
        counters.Rejected(verdicts.Verdicts.Count(v => !v.Accepted));
        var writes = new List<PendingWrite>();
        foreach (var e in verdicts.Accepted)
        {
            int s = p.ContextStartInTyped + e.Start, en = p.ContextStartInTyped + e.End;
            if (s < 0) continue;   // D5: outside what the user typed this session
            writes.Add(new PendingWrite(Layer.Ai, p.Generation, s, en, p.Context[e.Start..e.End], e.Replacement, clock.NowMs, p.KeySeq, e.OriginalWords));
        }
        if (Sweep is not null && rules.Settings.LenientWithNonWords)
        {
            foreach (var f in Sweep.Find(p.Context, verdicts.Accepted, verdicts.Verdicts.Where(v => !v.Accepted).Select(v => v.Edit).ToArray()))
            {
                int s = p.ContextStartInTyped + f.Start;
                if (s < 0) continue;   // D5
                writes.Add(new PendingWrite(Layer.Ai, p.Generation, s, p.ContextStartInTyped + f.End, f.Original, f.Replacement, clock.NowMs, p.KeySeq, [f.Original]));
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
