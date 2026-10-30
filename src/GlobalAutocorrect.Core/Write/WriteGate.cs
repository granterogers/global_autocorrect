using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Telemetry;

namespace GlobalAutocorrect.Core.Write;

public enum Layer { Instant, Ai, Undo }

/// <summary>A queued replacement in typed-buffer coordinates (D5). Offsets shift as earlier writes land.</summary>
public sealed class PendingWrite(Layer layer, long generation, int start, int end, string original, string replacement, long createdAtMs, long keySeqAtDecision, IReadOnlyList<string>? words = null)
{
    /// <summary>The whole words this write corrects (Original may be just the changed letters).</summary>
    public IReadOnlyList<string> Words { get; } = words ?? [original];
    public Layer Layer { get; } = layer;
    public long Generation { get; } = generation;
    public int Start { get; set; } = start;
    public int End { get; set; } = end;
    public string Original { get; } = original;
    public string Replacement { get; } = replacement;
    public long CreatedAtMs { get; } = createdAtMs;
    public long KeySeqAtDecision { get; } = keySeqAtDecision;
}

/// <summary>Raised after a verified write: enough to underline the word (4.7). No text.</summary>
public sealed record CorrectionNotice(string TargetId, Layer Layer, int CaretDistance, int Length, long KeySeqAtWrite);

/// <summary>
/// Applies queued edits only when it is safe (4.6, §6.2). Typing is never blocked: only the write waits (4.6.e, 12.3).
/// </summary>
public sealed class WriteGate(EngineOptions options, IClock clock, IKeyMonitor keys, TypingSession session, Counters counters)
{
    private readonly List<PendingWrite> _queue = [];
    // 4.6.d: a field that desynced is left alone "for the rest of the session": until the next session break (a click,
    // focus change, arrow key...). The value is the session generation it applies to. It used to be for the life of the
    // app, so one desync in Edge silenced every later Intercom reply until a restart (user report).
    private readonly Dictionary<string, long> _desynced = new(StringComparer.Ordinal);

    public event Action<CorrectionNotice, PendingWrite, string>? Written;   // notice, write, typed text before it (for undo context)
    public event Action<string>? Desynced;

    public int Count => _queue.Count;
    public bool IsDesynced(string targetId) => _desynced.TryGetValue(targetId, out long g) && g == session.Generation;

    private readonly Dictionary<string, List<long>> _verifyFailures = new(StringComparer.Ordinal);

    public void Enqueue(PendingWrite w) => _queue.Add(w);

    /// <summary>D43: text was taken in at the front of the buffer; queued writes move with it.</summary>
    public void Shift(int by)
    {
        foreach (var w in _queue) { w.Start += by; w.End += by; }
    }
    public void Clear() => _queue.Clear();

    /// <summary>Tries the rightmost eligible write (so offsets to its left stay valid). Returns true if one landed.</summary>
    public bool TryWrite(ITextTarget? target)
    {
        long now = clock.NowMs;
        _queue.RemoveAll(w => w.Generation != session.Generation);
        _queue.RemoveAll(w =>
        {
            bool expired = now - w.CreatedAtMs > options.WriteDeadlineMs;
            if (expired) { counters.Dropped(); LastDropReason = "expired"; }
            return expired;
        });
        if (target is null || _queue.Count == 0) return false;

        // Keys the hook has seen but we haven't processed mean our view of the typed text is stale (D6).
        if (keys.LatestKeySeq != session.LastKeySeq) return false;

        foreach (var w in _queue.OrderByDescending(w => w.Start).ToArray())
        {
            if (!Ready(w, now)) continue;
            return Execute(w, target);
        }
        return false;
    }

    private bool Ready(PendingWrite w, long now)
    {
        if (w.Layer == Layer.Undo) return true;   // the user asked for it and is waiting
        bool gap = now - keys.LastKeyAtMs >= options.WriteGapMs && !keys.ModifierHeld;   // 4.6.c
        if (w.Layer == Layer.Instant)
            return (keys.LatestKeySeq == w.KeySeqAtDecision && now - keys.LastKeyAtMs >= options.InstantQuietMs) || gap;   // D6, D47: quiet since the word ended, or a gap

        if (!gap) return false;
        // 4.6.a/b: while actively typing, the word being typed and the word before it are off limits.
        bool active = now - session.LastKeyAtMs < options.ActiveTypingMs && !session.EndsSentence;
        if (active)
        {
            var (ps, _) = session.LastTwoWords();
            if (w.End > ps || w.Start >= ps) return false;
        }
        return true;
    }

    private bool Execute(PendingWrite w, ITextTarget target)
    {
        string typed = session.Typed;
        if (w.End > typed.Length || typed[w.Start..w.End] != w.Original) { Drop(w, "text-moved"); return false; }
        string tail = typed[w.End..];
        // D39 (was D8, "never"): a fix may reach back over a couple of line breaks the user typed in this session
        // ("...the popup." Enter Enter "Apologies"): every editor tested moves over a paragraph break in one Left, and the
        // field is checked against the typed text before writing and verified after. Fast typists start the next
        // paragraph before the AI's fix arrives, and those fixes were all dropped (user report, Intercom).
        if (tail.Count(c => c == '\n') > options.MaxLineBreaksCrossed) { Drop(w, "too-many-lines"); return false; }

        var win = target.ReadWindow();
        if (IsDesynced(win.TargetId)) { Drop(w, "desynced"); return false; }
        // A busy app sometimes can't be read for a moment: keep the edit and try again next tick; the deadline drops it
        // if the field never comes back (found by E2E: one failed read lost a correction).
        if (!win.CaretKnown) return false;
        if (!session.MatchesField(win.Before))
        {
            if (session.FieldBehind(win.Before)) return false;   // the app hasn't drawn the last keys yet: next tick
            // D42: the field changed in a way we didn't type (the app's own autocorrect, a paste). Start again from
            // what it shows instead of ending the session: this paragraph keeps being corrected.
            counters.FieldMismatch();
            session.Resync(win.Before);
            _queue.Clear();
            return false;
        }
        typed = session.Typed;   // may have adopted host autocorrect case/quotes
        if (typed[w.Start..w.End] != w.Original) { Drop(w, "changed-on-read"); return false; }
        if (keys.LatestKeySeq != session.LastKeySeq) return false;   // a key slipped in during the read

        int caretDistance = TextNorm.Elements(tail);
        int deleteCount = TextNorm.Elements(w.Original);
        long seqAtWrite = keys.LatestKeySeq;
        // D28: a short tail of spaces and punctuation (usually just the key that finished the word) is deleted and typed
        // again rather than stepped over with Left/Right. Edge applied the arrow keys out of step with a key typed
        // straight after the batch, leaving "thed og" (found by E2E). No arrow keys, nothing to fall out of step.
        bool retype = caretDistance is > 0 and <= 3 && tail.All(c => c is ' ' or '\t' || char.IsPunctuation(c));
        if (target.RetypeInsteadOfArrows && caretDistance > 0)
        {
            // D46: CKEditor (Teams) applies arrow keys after the edits that follow them, so delete back and retype. Never
            // across a line break: retyping it means pressing Enter, and Enter sends the message in Teams.
            if (tail.Contains('\n') || caretDistance > 300) { Drop(w, "retype-unsafe"); return false; }
            retype = true;
        }
        bool sent = retype
            ? target.ApplyEdit(0, caretDistance + deleteCount, w.Replacement + tail)
            : target.ApplyEdit(caretDistance, deleteCount, w.Replacement);

        // Compared the way the field reports text (D42): a blank line counts once however the editor shows it.
        string before = TextNorm.Comparable(win.Before);
        string newTyped = typed[..w.Start] + w.Replacement + tail;
        string expectedBefore = before[..^TextNorm.Comparable(typed).Length] + TextNorm.Comparable(newTyped);
        if (!sent)
        {
            // Nothing was sent (focus had moved to another app): nothing to verify, nothing damaged.
            Drop(w, "not-sent");
            session.Break(SessionBreakReason.FocusChange);
            return false;
        }
        if (!target.Verify(expectedBefore, win.After))   // 4.6.d
        {
            counters.Desync();
            _queue.Clear();
            Desynced?.Invoke(win.TargetId);
            // D42: a mismatch after writing is usually the app's own doing (autocorrect, how it stores a line break), not
            // damage, so start again from what the field now shows and keep correcting. Only a field that keeps
            // surprising us (three times in a minute) is left alone for the rest of the session, as 4.6.d says.
            var recent = _verifyFailures.TryGetValue(win.TargetId, out var list) ? list : _verifyFailures[win.TargetId] = [];
            recent.RemoveAll(at => clock.NowMs - at > 60_000);
            recent.Add(clock.NowMs);
            var fresh = target.ReadWindow();
            if (recent.Count >= 3 || !fresh.CaretKnown)
            {
                session.Break(SessionBreakReason.Desync);
                _desynced[win.TargetId] = session.Generation;   // the rest of this session only
            }
            else session.Resync(fresh.Before);
            return false;
        }

        _queue.Remove(w);
        int delta = w.Replacement.Length - (w.End - w.Start);
        foreach (var other in _queue.ToArray())
        {
            if (other.Start >= w.End) { other.Start += delta; other.End += delta; }
            else if (other.End > w.Start) _queue.Remove(other);   // overlapped the edit just written
        }
        session.ApplyOwnEdit(w.Start, w.End, w.Replacement);
        if (w.Layer == Layer.Instant) counters.Instant();
        else if (w.Layer == Layer.Ai) counters.Ai();

        var notice = new CorrectionNotice(win.TargetId, w.Layer, caretDistance, TextNorm.Elements(w.Replacement), seqAtWrite);
        Written?.Invoke(notice, w, newTyped);
        return true;
    }

    /// <summary>Why the last write was dropped (a reason code, never text): for tests and the diagnostics log.</summary>
    public string LastDropReason { get; private set; } = "";

    private void Drop(PendingWrite w, string reason)
    {
        _queue.Remove(w);
        counters.Dropped();
        LastDropReason = reason;
    }
}
