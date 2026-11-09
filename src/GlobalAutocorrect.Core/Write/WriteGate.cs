using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Health;
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
    public long Generation { get; set; } = generation;   // moves on when a resync keeps the write (D74)
    public int Start { get; set; } = start;
    public int End { get; set; } = end;
    public string Original { get; } = original;
    public string Replacement { get; } = replacement;
    public long CreatedAtMs { get; } = createdAtMs;
    public long KeySeqAtDecision { get; } = keySeqAtDecision;

    /// <summary>D64: when the key that led to this fix was typed (the word's or the sentence's last key), for timings.</summary>
    public long KeyAtMs { get; init; } = createdAtMs;

    /// <summary>
    /// D61: the write replaces a whole word (the instant layer's word fix), so it only lands while those letters are still
    /// a whole word. "som " queued to become "some", then Backspace and "ething", must not become "someething".
    /// </summary>
    public bool WholeWord { get; init; }
}

/// <summary>Raised after a verified write: enough to underline the word (4.7). No text.</summary>
public sealed record CorrectionNotice(string TargetId, Layer Layer, int CaretDistance, int Length, long KeySeqAtWrite,
    string Original = "", string Replacement = "", bool Retyped = false);   // the last three for the screen tests' diagnostics only

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

    /// <summary>D81: what the app learns per program, and how carefully it writes there. Null: always normal.</summary>
    public AppHealth? Health { get; set; }

    /// <summary>
    /// D81: a field that disagrees with the typed record and can't be resynced this many times in a row, over at least
    /// <see cref="StuckMs"/>, is stuck: the queue is dropped and the field left alone for the rest of the session. Before,
    /// the gate retried every tick until the write expired (the Claude app: 340 identical mismatches in one day).
    /// </summary>
    public const int StuckLimit = 8;
    public const long StuckMs = 1500;

    /// <summary>D81: in a program in careful mode, an AI fix is written only this close to the caret and on its line.</summary>
    public const int CarefulReach = 40;

    private int _refusedResyncs;
    private long _refusedSinceMs;
    private long _refusedGeneration = -1;

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
            if (!Ready(w, now) || !QuietEnough(w, target.AppName, now)) continue;
            return Execute(w, target);
        }
        return false;
    }

    /// <summary>
    /// D98, user report (Intercom note, "didn't catch that"): in a program where a fix stepped to with the arrow keys has
    /// gone wrong, a fix with more than a few characters between it and the caret waits for a real pause, not a 150 ms gap
    /// between two keys. Every such failure in Edge had the user typing during the check: Edge applies a long run of
    /// arrow keys out of step with a key typed straight after it (D28), so the next key landed inside the fix. A short
    /// tail is deleted and typed again (D28), with no arrow keys, and is not held back. No program is named (4.1.a).
    /// </summary>
    private bool QuietEnough(PendingWrite w, string app, long now)
    {
        if (w.Layer == Layer.Undo || Health is null || session.Typed.Length - w.End <= 3) return true;
        return now - keys.LastKeyAtMs >= options.ShakyAppGapMs || !Health.ArrowsShaky(app);
    }

    private bool Ready(PendingWrite w, long now)
    {
        if (w.Layer == Layer.Undo) return true;   // the user asked for it and is waiting
        bool gap = now - keys.LastKeyAtMs >= options.WriteGapMs && !keys.ModifierHeld;   // 4.6.c
        // D100, user report "why all caps? stop doing that" (Intercom: "REPRESENTATIVE Mickey"): the instant fix went out
        // while Shift was down for the next word's capital, and was typed in capitals. Never while a modifier is held.
        if (w.Layer == Layer.Instant)
            return !keys.ModifierHeld && ((keys.LatestKeySeq == w.KeySeqAtDecision && now - keys.LastKeyAtMs >= options.InstantQuietMs) || gap);   // D6, D47

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
            if (_refusedGeneration != session.Generation) { _refusedResyncs = 0; _refusedGeneration = session.Generation; }
            if (_refusedResyncs == 0)   // D81: once per run of the same disagreement, not every tick
            {
                counters.FieldMismatch();
                Mismatch?.Invoke("write " + MismatchKind.Describe(session.Typed, win.Before));   // D74, D76: kinds of character only
            }
            // D61: refused if the read is behind the keys (next tick); D74: OnResynced keeps the queue
            if (session.Resync(win.Before)) _refusedResyncs = 0;
            else if (++_refusedResyncs == 1) _refusedSinceMs = clock.NowMs;
            else if (_refusedResyncs >= StuckLimit && clock.NowMs - _refusedSinceMs >= StuckMs) GiveUp(win.TargetId, target.AppName);
            return false;
        }
        _refusedResyncs = 0;
        typed = session.Typed;   // may have adopted host autocorrect case/quotes
        if (typed[w.Start..w.End] != w.Original) { Drop(w, "changed-on-read"); return false; }
        if (keys.LatestKeySeq != session.LastKeySeq) return false;   // a key slipped in during the read
        // D60: a fix that starts where the typed record starts must not be the tail of a longer word in the field. A
        // session that began mid-word ("Lo|oking") saw "oking" as a word and the instant layer made it "king", leaving
        // "Loking" (CI screen tests, CKEditor). The field is the truth: a letter right before means it's a fragment.
        if (w.Start == 0 && StartsMidWord(win.Before, typed)) { Drop(w, "fragment"); return false; }
        if (w.WholeWord && !IsWholeWord(typed, w.Start, w.End)) { Drop(w, "no-longer-a-word"); return false; }   // D61

        // D84: never the same mistake twice, and never a change that looks like one (capitals from nowhere, a doubled word).
        if (w.Layer != Layer.Undo)
        {
            var (wordsBefore, wordsAfter) = WriteGuard.WholeWords(typed, w.Start, w.End, w.Replacement);
            if (Mistakes?.IsKnown(wordsBefore, wordsAfter) == true) { Drop(w, "known-mistake"); return false; }
            if (WriteGuard.Problem(wordsBefore, wordsAfter) is { } why)
            {
                Mistakes?.Remember(wordsBefore, wordsAfter);
                Drop(w, "guard-" + why);
                return false;
            }
        }
        // D90, user report (Intercom): a fix with an "@name" mention, or an object the field shows in its place, between it
        // and the caret is never written. Editors turn a mention into one block: a single Left or Backspace covers all of
        // it, so arrows land in the wrong place and retyping deletes the tag (and "@" opens the people picker again).
        // D93: nor one that changes such an object itself (Intercom's mention reads as U+FFFC, without its "@").
        if (w.Layer != Layer.Undo && (tail.Contains('￼') || w.Original.Contains('￼') || w.Replacement.Contains('￼')
                                      || Text.Tokeniser.Tokenise(typed).Any(t => t.IsMention && t.End > w.Start)))
        {
            Drop(w, "mention-in-way");
            return false;
        }
        int caretDistance = TextNorm.Elements(tail);
        // D81: in a program whose fixes have gone wrong, an AI fix far back or on an earlier line is not risked.
        if (w.Layer == Layer.Ai && (tail.Contains('\n') || caretDistance > CarefulReach) && Health?.ModeFor(target.AppName) == AppMode.Careful)
        {
            Drop(w, "careful-app");
            return false;
        }
        int deleteCount = TextNorm.Elements(w.Original);
        long seqAtWrite = keys.LatestKeySeq;
        // D28: a short tail of spaces and punctuation (usually just the key that finished the word) is deleted and typed
        // again rather than stepped over with Left/Right. Edge applied the arrow keys out of step with a key typed
        // straight after the batch, leaving "thed og" (found by E2E). No arrow keys, nothing to fall out of step.
        bool retype = caretDistance is > 0 and <= 3 && tail.All(c => c is ' ' or '\t' || char.IsPunctuation(c));
        bool learntRetype = !target.RetypeInsteadOfArrows && Health?.RetypeIn(target.AppName) == true;   // D95
        if ((target.RetypeInsteadOfArrows || learntRetype) && caretDistance > 0)
        {
            // D46: CKEditor (Teams) applies arrow keys after the edits that follow them, so delete back and retype. D89:
            // so does any program where a fix stepped to with arrows has gone wrong (learnt, WhatsApp's editor). Never
            // across a line break: retyping it means pressing Enter, and Enter sends the message in Teams and WhatsApp.
            if (target.RetypeInsteadOfArrows && (tail.Contains('\n') || caretDistance > options.RetypeMaxDistance)) { Drop(w, "retype-unsafe"); return false; }
            // D101, user report "if I press a new line it stops correcting": a program that only learnt to retype (D89)
            // dropped every fix on an earlier line. Arrow keys work there often enough, and D99 puts a caret left short
            // back, so the fix is stepped to instead.
            retype = !(tail.Contains('\n') || caretDistance > options.RetypeMaxDistance);
        }
        // D100: Shift (or Ctrl, Alt) pressed during the read: wait for it to be let go, next tick.
        if (w.Layer != Layer.Undo && keys.ModifierHeld) return false;
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
        if (!target.Verify(expectedBefore, win.After) && !RepairedCase(target, w, expectedBefore, win.After, retype, caretDistance, tail, seqAtWrite)
            && !RepairedCaret(target, expectedBefore, win.After, retype ? 0 : caretDistance, seqAtWrite))   // 4.6.d, D84, D99
        {
            counters.Desync();
            Health?.VerifyFailed(target.AppName);   // D81
            // D95: which way the failed fix was written (no text), so a report like "not picking it up" can be answered.
            WriteDropped?.Invoke(w.Layer, $"verify-failed {(retype ? "retype" : "arrows")} distance={caretDistance} lines={tail.Count(c => c == '\n')}");
            if (!retype && caretDistance > 0 && Health is not null && !Health.RetypeIn(target.AppName))
            {
                Health.ArrowsFailed(target.AppName);   // D89: this program's fixes are retyped from now on
                ArrowsFailed?.Invoke(target.AppName);
            }
            // D95: retyping, chosen because arrows had failed here, failed too: count it, so the way that works wins.
            else if (retype && learntRetype && caretDistance > 0) Health?.RetypeFailed(target.AppName);
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
            else session.Resync(fresh.Before);   // D61: refused if this read lags; the next check finds the mismatch
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
        if (w.Layer != Layer.Undo) Health?.Written(target.AppName);   // D81
        // D64: what the user feels, from the key that finished the word or sentence to the fix being in the field.
        if (w.Layer is Layer.Instant or Layer.Ai && w.KeyAtMs >= 0)
            counters.Timings.Record(w.Layer == Layer.Instant ? "instant.keyToFix" : "ai.keyToFix", clock.NowMs - w.KeyAtMs);

        var notice = new CorrectionNotice(win.TargetId, w.Layer, caretDistance, TextNorm.Elements(w.Replacement), seqAtWrite, w.Original, w.Replacement, retype);
        Written?.Invoke(notice, w, newTyped);
        return true;
    }

    /// <summary>A write that was dropped and why (a reason code, never text).</summary>
    public event Action<Layer, string>? WriteDropped;

    /// <summary>
    /// D74: the record was resynced from the field. A queued fix whose text from its start to the caret is unchanged in
    /// the new record is moved to the same place in it; the rest are dropped, counted and logged. Before, every queued fix
    /// vanished silently on a resync (the endurance run lost 1 instant fix in 6 this way while typing fast in Notepad).
    /// </summary>
    public void OnResynced(string oldTyped, long oldGeneration)
    {
        string now = session.Typed;
        foreach (var w in _queue.Where(q => q.Generation == oldGeneration).ToArray())
        {
            string tail = w.Start <= oldTyped.Length ? oldTyped[w.Start..] : "";
            if (tail.Length > 0 && now.EndsWith(tail, StringComparison.Ordinal))
            {
                int delta = now.Length - oldTyped.Length;
                w.Start += delta;
                w.End += delta;
                w.Generation = session.Generation;
            }
            else Drop(w, "cleared-on-resync");
        }
    }

    /// <summary>D74, D76: the field didn't match the typed record before a write (kinds of character, never text).</summary>
    public event Action<string>? Mismatch;



    /// <summary>
    /// D61: no letter, digit or apostrophe right before typed[start..end], and the space or punctuation that ended the
    /// word still after it. A word fix is decided at that separator; if the word now touches the caret, the user deleted
    /// the separator and is still working on the word (and the field's text after the caret can be hidden behind Edge's
    /// suggestion, so it can't be trusted to show what follows).
    /// </summary>
    private static bool IsWholeWord(string typed, int start, int end)
    {
        static bool Joins(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’';
        return (start == 0 || !Joins(typed[start - 1])) && end < typed.Length && !Joins(typed[end]);
    }

    /// <summary>D60: true when the field shows a letter or digit right before the start of the typed record.</summary>
    private static bool StartsMidWord(string fieldBefore, string typed)
    {
        string f = TextNorm.Comparable(fieldBefore), t = TextNorm.Comparable(typed);
        int at = f.Length - t.Length - 1;
        return at >= 0 && t.Length > 0 && f.EndsWith(t, StringComparison.Ordinal) && char.IsLetterOrDigit(f[at]);
    }

    /// <summary>D84: changes that were mistakes, never made again. Null: none remembered.</summary>
    public Health.MistakeMemory? Mistakes { get; set; }

    /// <summary>D84: a fix landed in the wrong case and was typed again (program name only).</summary>
    public event Action<string>? CaseRepaired;

    /// <summary>D89: a fix stepped to with arrow keys failed its check; the program's fixes are retyped from now on.</summary>
    public event Action<string>? ArrowsFailed;

    /// <summary>
    /// D84, user report "things are all uppercase": the fix landed, but in capitals it wasn't sent in (Shift or Caps Lock
    /// caught our keys, or the app changed them). If the field shows exactly the expected text except for letter case,
    /// and nothing was typed since, the same edit is sent again once and checked again.
    /// </summary>
    private bool RepairedCase(ITextTarget target, PendingWrite w, string expectedBefore, string after, bool retype, int caretDistance, string tail, long seqAtWrite)
    {
        if (keys.LatestKeySeq != seqAtWrite) return false;
        var fresh = target.ReadWindow();
        if (!fresh.CaretKnown) return false;
        string b = TextNorm.Comparable(fresh.Before);
        int n = Math.Min(expectedBefore.Length, TextNorm.Comparable(w.Replacement + tail).Length + 16);
        string want = expectedBefore[^n..];
        if (!b.EndsWith(want, StringComparison.OrdinalIgnoreCase) || b.EndsWith(want, StringComparison.Ordinal)) return false;
        int replaced = TextNorm.Elements(w.Replacement);
        bool sent = retype ? target.ApplyEdit(0, caretDistance + replaced, w.Replacement + tail) : target.ApplyEdit(caretDistance, replaced, w.Replacement);
        if (!sent || !target.Verify(expectedBefore, after)) return false;
        CaseRepaired?.Invoke(target.AppName);
        return true;
    }

    /// <summary>D99: a fix landed but the caret was left short of the user's place (program name and count only).</summary>
    public event Action<string, int>? CaretRepaired;

    /// <summary>
    /// D99, user report (Intercom email, "DWGfFile"): the fix landed, but the editor lost some of the Right presses that
    /// step back to the user's place, so the caret sat inside the text and the next keys the user typed went there. If the
    /// field shows exactly the expected text with the caret a few characters short, and nothing was typed since, the
    /// missing Right presses are sent and the fix is checked again.
    /// </summary>
    private bool RepairedCaret(ITextTarget target, string expectedBefore, string after, int caretDistance, long seqAtWrite)
    {
        if (caretDistance == 0 || keys.LatestKeySeq != seqAtWrite) return false;
        var fresh = target.ReadWindow();
        if (!fresh.CaretKnown) return false;
        int shortBy = TextNorm.CaretShort(expectedBefore, fresh.Before, fresh.After, caretDistance);
        if (shortBy == 0 || keys.LatestKeySeq != seqAtWrite || !target.MoveCaretRight(shortBy) || !target.Verify(expectedBefore, after)) return false;
        CaretRepaired?.Invoke(target.AppName, shortBy);
        return true;
    }

    /// <summary>D81: the field is stuck disagreeing with the record: drop what's queued, leave it alone this session, learn it.</summary>
    private void GiveUp(string targetId, string app)
    {
        foreach (var w in _queue.ToArray()) Drop(w, "stuck");
        _desynced[targetId] = session.Generation;
        _refusedResyncs = 0;
        Health?.Stuck(app);
        Stuck?.Invoke(app);
    }

    /// <summary>D81: a field was given up on as stuck (program name only).</summary>
    public event Action<string>? Stuck;

    /// <summary>Why the last write was dropped (a reason code, never text): for tests and the diagnostics log.</summary>
    public string LastDropReason { get; private set; } = "";

    private void Drop(PendingWrite w, string reason)
    {
        _queue.Remove(w);
        counters.Dropped();
        LastDropReason = reason;
        WriteDropped?.Invoke(w.Layer, reason);
    }
}
