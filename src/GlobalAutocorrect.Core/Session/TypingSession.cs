using System.Text;
using GlobalAutocorrect.Core.Targets;
using GlobalAutocorrect.Core.Text;

namespace GlobalAutocorrect.Core.Session;

/// <summary>A word the user has just finished: [Start, End) in typed-buffer coordinates.</summary>
public readonly record struct FinishedWord(int Start, int End, string Word, bool IsSentenceStart, string? PreviousWord = null);

public readonly record struct KeyEffect(FinishedWord? Word, bool SentenceEnded);

/// <summary>
/// The characters the user physically typed since the last break (D5, 4.2). Corrections may only touch text inside
/// this buffer, and only while the field's text before the caret still ends with it. Any break bumps
/// <see cref="Generation"/>, so plans made earlier are dropped.
/// </summary>
public sealed class TypingSession
{
    private const int MaxLength = 4000;
    private readonly StringBuilder _typed = new();

    public string Typed => _typed.ToString();
    public int Length => _typed.Length;
    public long Generation { get; private set; }
    public long LastKeySeq { get; private set; }
    public long LastKeyAtMs { get; private set; } = long.MinValue / 2;
    public SessionBreakReason LastBreak { get; private set; }

    /// <summary>True when the buffer ends a sentence ("... done. "), which lifts the 4.6.a protection (4.6.b).</summary>
    public bool EndsSentence => SentenceSplitter.EndsSentence(Typed);

    public KeyEffect Apply(KeyInput k)
    {
        LastKeySeq = Math.Max(LastKeySeq, k.Seq);
        LastKeyAtMs = k.AtMs;
        switch (k.Kind)
        {
            case KeyKind.Char:
                return OnChar(k.Char);
            case KeyKind.Backspace:
                if (_typed.Length == 0) { Break(SessionBreakReason.BackspaceIntoExisting); return default; }
                RemoveLastElement();
                return default;
            case KeyKind.Delete:
                return default;   // only the text after the caret changes
            case KeyKind.Navigation: Break(SessionBreakReason.Navigation); return default;
            case KeyKind.Shortcut: Break(SessionBreakReason.Shortcut); return default;
            case KeyKind.Paste: Break(SessionBreakReason.Paste); return default;
            case KeyKind.Foreign: Break(SessionBreakReason.Foreign); return default;
            case KeyKind.Click: Break(SessionBreakReason.Click); return default;
            case KeyKind.FocusChange: Break(SessionBreakReason.FocusChange); return default;
            default: return default;
        }
    }

    public void Break(SessionBreakReason reason)
    {
        _typed.Clear();
        Generation++;
        LastBreak = reason;
    }

    /// <summary>Keeps the buffer in step after one of our own (tagged) edits, which the hook never reports.</summary>
    public void ApplyOwnEdit(int start, int end, string replacement)
    {
        _typed.Remove(start, end - start).Insert(start, replacement);
    }

    /// <summary>
    /// D5 check against the field. Returns true if the field's text before the caret ends with the buffer. Host
    /// autocorrect that only changed case or quote style (same length) is adopted into the buffer instead of breaking.
    /// </summary>
    /// <summary>
    /// True when the field simply hasn't caught up with the last few keys yet: it ends with the buffer minus its last
    /// 1-8 characters. Browsers update their accessibility text a moment after the key, so a read straight after a
    /// sentence end can lag (found by E2E in Edge). That is "try again shortly", not a session break.
    /// </summary>
    public bool FieldBehind(string fieldBefore)
    {
        string f = TextNorm.Comparable(fieldBefore), t = TextNorm.Comparable(Typed);
        for (int k = 1; k <= Math.Min(8, t.Length - 1); k++)
            if (f.EndsWith(t[..^k], StringComparison.Ordinal)) return true;
        return false;
    }

    public bool MatchesField(string fieldBefore)
    {
        // D42: line-break runs compare as one, so a blank line reported differently by the editor still matches.
        string f = TextNorm.Comparable(fieldBefore), t = TextNorm.Comparable(Typed);
        if (t.Length != _typed.Length) return f.EndsWith(t, StringComparison.Ordinal);   // CRs or blank lines differ: compare only
        if (f.EndsWith(t, StringComparison.Ordinal)) return true;
        if (f.Length >= t.Length && f.EndsWith(t, StringComparison.OrdinalIgnoreCase))
        {
            _typed.Clear().Append(f, f.Length - t.Length, t.Length);
            return true;
        }
        return false;
    }

    /// <summary>
    /// D42: start again from what the field shows, instead of giving up. The buffer becomes the field's current
    /// paragraph before the caret (one paragraph: no line breaks whose caret steps could be miscounted). Pending plans
    /// are dropped (new generation), but correcting carries on.
    /// </summary>
    public void Resync(string fieldBefore)
    {
        string f = TextNorm.Normalise(fieldBefore);
        string para = f[(f.LastIndexOf('\n') + 1)..];
        if (para.Length > MaxLength) para = para[^MaxLength..];
        _typed.Clear().Append(para);
        Generation++;
        LastBreak = SessionBreakReason.Resync;
    }

    /// <summary>
    /// D43: the user wants every obvious mistake in the paragraph fixed, not only what was typed since the last click.
    /// When the buffer matches the field and the field's current paragraph starts earlier, the earlier part is taken
    /// in as if typed. Returns how many characters were added at the front (so queued writes can shift); 0 if none.
    /// </summary>
    public int ExtendToParagraph(string fieldBefore)
    {
        if (!MatchesField(fieldBefore)) return 0;
        string f = TextNorm.Normalise(fieldBefore), t = Typed;
        if (t.Contains('\n')) return 0;   // the buffer already reaches back past the paragraph start
        string para = f[(f.LastIndexOf('\n') + 1)..];
        int extra = para.Length - t.Length;
        if (extra <= 0 || !para.EndsWith(t, StringComparison.Ordinal) || para.Length > MaxLength) return 0;
        _typed.Insert(0, para[..extra]);
        return extra;
    }

    /// <summary>[start, end) of the last two words (the word being typed and the one before it), for 4.6.a.</summary>
    public (int Start, int End) LastTwoWords()
    {
        var words = Tokeniser.Tokenise(Typed).Where(t => t.IsWord).ToArray();
        if (words.Length == 0) return (_typed.Length, _typed.Length);
        return (words[Math.Max(0, words.Length - 2)].Start, _typed.Length);
    }

    private KeyEffect OnChar(char c)
    {
        if (_typed.Length >= MaxLength) Break(SessionBreakReason.Overflow);
        _typed.Append(c);
        if (!IsWordEnd(c)) return default;

        int end = _typed.Length - 1;   // before the delimiter
        int start = end;
        while (start > 0 && !char.IsWhiteSpace(_typed[start - 1])) start--;
        FinishedWord? word = null;
        if (end > start)
        {
            string chunk = _typed.ToString(start, end - start);
            var tokens = Tokeniser.Tokenise(chunk);
            // Only a chunk that is one plain word (plus surrounding punctuation) is a candidate; URLs, emails and
            // code-like chunks are left alone (4.3.c).
            var wordTokens = tokens.Where(t => t.IsWord).ToArray();
            if (wordTokens.Length == 1 && !wordTokens[0].IsProtected)
            {
                var w = wordTokens[0];
                word = new FinishedWord(start + w.Start, start + w.End, w.Text, IsSentenceStart(start + w.Start), PreviousPlainWord(start));
            }
        }
        // Enter finishes a line even without a full stop (D44: "...to see that" Enter, typing on, never reached the AI).
        bool sentenceEnded = c == '\n' || (char.IsWhiteSpace(c) && EndsSentence);
        return new KeyEffect(word, sentenceEnded);
    }

    /// <summary>
    /// True only when the buffer itself shows a sentence start before the word. If the word is the first thing in the
    /// buffer, the text before it is unknown, so it counts as mid-sentence (the conservative choice for 4.3.c).
    /// </summary>
    private bool IsSentenceStart(int wordStart)
    {
        int i = wordStart - 1;
        while (i >= 0 && (char.IsWhiteSpace(_typed[i]) || _typed[i] is '"' or '\'' or '(')) i--;
        if (i < 0) return false;
        return _typed[i] is '.' or '!' or '?' or '\n';
    }

    /// <summary>The plain word directly before <paramref name="chunkStart"/>, if exactly one space separates them.</summary>
    private string? PreviousPlainWord(int chunkStart)
    {
        if (chunkStart < 2 || _typed[chunkStart - 1] != ' ' || char.IsWhiteSpace(_typed[chunkStart - 2])) return null;
        int s = chunkStart - 1;
        while (s > 0 && !char.IsWhiteSpace(_typed[s - 1])) s--;
        string prev = _typed.ToString(s, chunkStart - 1 - s);
        return Tokeniser.Tokenise(prev) is [{ IsWord: true, IsProtected: false } t] && t.Length == prev.Length ? prev : null;
    }

    private static bool IsWordEnd(char c) => char.IsWhiteSpace(c) || c is ',' or ';' or '!' or '?' or ')' or '"';

    private void RemoveLastElement()
    {
        var info = new System.Globalization.StringInfo(Typed);
        int n = info.LengthInTextElements;
        int keep = n <= 1 ? 0 : info.SubstringByTextElements(0, n - 1).Length;
        _typed.Length = keep;
    }
}
