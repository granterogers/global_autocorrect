using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Telemetry;

namespace GlobalAutocorrect.Core.Write;

public enum Layer { Instant, Ai, Undo }

/// <summary>A queued replacement in typed-buffer coordinates (D5). Offsets shift as earlier writes land.</summary>
public sealed class PendingWrite(Layer layer, long generation, int start, int end, string original, string replacement, long createdAtMs, long keySeqAtDecision)
{
    public Layer Layer { get; } = layer;
    public long Generation { get; } = generation;
    public int Start { get; set; } = start;
    public int End { get; set; } = end;
    public string Original { get; } = original;
    public string Replacement { get; } = replacement;
    public long CreatedAtMs { get; } = createdAtMs;
    public long KeySeqAtDecision { get; } = keySeqAtDecision;
}

/// <summary>Raised after a verified write: enough to underline the word (4.7) and arm Ctrl+Z (4.8.a). No text.</summary>
public sealed record CorrectionNotice(string TargetId, Layer Layer, int CaretDistance, int Length, long KeySeqAtWrite);

/// <summary>
/// Applies queued edits only when it is safe (4.6, §6.2). Typing is never blocked: only the write waits (4.6.e, 12.3).
/// </summary>
public sealed class WriteGate(EngineOptions options, IClock clock, IKeyMonitor keys, TypingSession session, Counters counters)
{
    private readonly List<PendingWrite> _queue = [];
    private readonly HashSet<string> _desynced = new(StringComparer.Ordinal);

    public event Action<CorrectionNotice, PendingWrite, string>? Written;   // notice, write, typed text before it (for undo context)
    public event Action<string>? Desynced;

    public int Count => _queue.Count;
    public bool IsDesynced(string targetId) => _desynced.Contains(targetId);

    public void Enqueue(PendingWrite w) => _queue.Add(w);
    public void Clear() => _queue.Clear();

    /// <summary>Tries the rightmost eligible write (so offsets to its left stay valid). Returns true if one landed.</summary>
    public bool TryWrite(ITextTarget? target)
    {
        long now = clock.NowMs;
        _queue.RemoveAll(w => w.Generation != session.Generation);
        _queue.RemoveAll(w =>
        {
            bool expired = now - w.CreatedAtMs > options.WriteDeadlineMs;
            if (expired) counters.Dropped();
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
            return keys.LatestKeySeq == w.KeySeqAtDecision || gap;   // D6: nothing typed since the word ended, or a gap

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
        if (w.End > typed.Length || typed[w.Start..w.End] != w.Original) { Drop(w); return false; }
        string tail = typed[w.End..];
        if (tail.Contains('\n')) { Drop(w); return false; }   // D8: never move across a line break

        var win = target.ReadWindow();
        if (!win.CaretKnown || _desynced.Contains(win.TargetId)) { Drop(w); return false; }
        if (!session.MatchesField(win.Before))
        {
            session.Break(SessionBreakReason.FieldMismatch);
            _queue.Clear();
            return false;
        }
        typed = session.Typed;   // may have adopted host autocorrect case/quotes
        if (typed[w.Start..w.End] != w.Original) { Drop(w); return false; }
        if (keys.LatestKeySeq != session.LastKeySeq) return false;   // a key slipped in during the read

        int caretDistance = TextNorm.Elements(tail);
        int deleteCount = TextNorm.Elements(w.Original);
        long seqAtWrite = keys.LatestKeySeq;
        bool sent = target.ApplyEdit(caretDistance, deleteCount, w.Replacement);

        string before = TextNorm.Normalise(win.Before);
        string newTyped = typed[..w.Start] + w.Replacement + tail;
        string expectedBefore = before[..^TextNorm.Normalise(typed).Length] + TextNorm.Normalise(newTyped);
        if (!sent || !target.Verify(expectedBefore, win.After))   // 4.6.d
        {
            _desynced.Add(win.TargetId);
            counters.Desync();
            session.Break(SessionBreakReason.Desync);
            _queue.Clear();
            Desynced?.Invoke(win.TargetId);
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

    private void Drop(PendingWrite w)
    {
        _queue.Remove(w);
        counters.Dropped();
    }
}
