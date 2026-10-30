namespace GlobalAutocorrect.Core.Targets;

public enum TargetStatus { Ok, CaretUnknown, NoText, Password, Excluded, Elevated, Unavailable }

/// <summary>Text around the caret. Before/After are a window, never the whole document (10.6.b).</summary>
public sealed record TextWindow(TargetStatus Status, string TargetId, string Before, string After)
{
    public bool CaretKnown => Status == TargetStatus.Ok;
    public static TextWindow Unavailable(TargetStatus s, string id = "") => new(s, id, "", "");
}

/// <summary>
/// The focused text field (10.1). Implemented by the UIA/SendInput target on Windows and by
/// <c>SimulatedEditor</c> in tests (10.2). Counts are text elements (grapheme clusters), D8.
/// </summary>
public interface ITextTarget
{
    TextWindow ReadWindow();

    /// <summary>One atomic batch: move left <paramref name="caretDistance"/>, delete <paramref name="deleteCount"/>, type, move back (10.7.a).</summary>
    bool ApplyEdit(int caretDistance, int deleteCount, string replacement);

    /// <summary>
    /// D46: the editor handles a burst of arrow keys late (CKEditor 5, Teams' message box: it moves its own cursor from
    /// the browser's selection events, which arrive after the backspaces), so fixes delete back and retype instead.
    /// </summary>
    bool RetypeInsteadOfArrows => false;

    /// <summary>Re-reads and checks the field now shows <paramref name="expectedBefore"/> before the caret and <paramref name="expectedAfter"/> after it (4.6.d).</summary>
    bool Verify(string expectedBefore, string expectedAfter);
}

/// <summary>Normalises the small differences editors introduce so typed text and field text compare fairly.</summary>
public static class TextNorm
{
    public static string Normalise(string s)
    {
        if (s.Length == 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '\r':
                    sb.Append('\n');
                    if (i + 1 < s.Length && s[i + 1] == '\n') i++;
                    break;
                case ' ': sb.Append(' '); break;
                case '​' or '﻿': break;
                case '‘' or '’': sb.Append('\''); break;
                case '“' or '”': sb.Append('"'); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// D42: for comparing what was typed with what the field shows. On top of <see cref="Normalise"/>, a run of line
    /// breaks counts as one: rich editors report a blank line differently from the Enter presses that made it (Edge
    /// gave two characters fewer than typed, and every multi-paragraph reply then looked "changed under us").
    /// </summary>
    public static string Comparable(string s)
    {
        string n = Normalise(s);
        if (n.IndexOf('\n') < 0) return n;
        var sb = new System.Text.StringBuilder(n.Length);
        for (int i = 0; i < n.Length; i++)
        {
            if (n[i] == '\n' && sb.Length > 0 && sb[^1] == '\n') continue;
            sb.Append(n[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// D42: the text after the caret, before and after a write, is consistent: the same, or one has a suggestion the
    /// app shows after the caret (Edge's text prediction) that the other hasn't. That suggestion came and went between
    /// reads and made 23 of 25 realistic stress paragraphs "desync". The text before the caret is what proves the fix
    /// landed in the right place.
    /// </summary>
    public static bool AfterConsistent(string expectedFirstLine, string actualAfter)
    {
        string actual = actualAfter.Split('\n')[0];
        string key = actual.Length > 24 ? actual[..24] : actual;
        return expectedFirstLine.Length == 0 || actual.Contains(expectedFirstLine, StringComparison.Ordinal)
               || expectedFirstLine.Contains(key, StringComparison.Ordinal) || expectedFirstLine.EndsWith(key, StringComparison.Ordinal)
               || SameAfterAGhost(expectedFirstLine, actual) || SameAfterAGhost(actual, expectedFirstLine);
    }

    /// <summary>
    /// D61: <paramref name="withGhost"/> is <paramref name="other"/> with a suggestion in front: the suggestion was there
    /// when the field was read before the write and Edge cleared it when the text changed. Found by the random-typing
    /// test: the 24 characters compared then never lined up, so a correct fix counted as a desync.
    /// </summary>
    private static bool SameAfterAGhost(string withGhost, string other)
    {
        const int MaxGhost = 40, Probe = 8;
        if (other.Length < Probe) return false;
        string probe = other[..Probe];
        for (int i = 1; i <= Math.Min(MaxGhost, withGhost.Length - Probe); i++)
        {
            string rest = withGhost[i..];
            if (rest.StartsWith(probe, StringComparison.Ordinal) && (other.StartsWith(rest, StringComparison.Ordinal) || rest.StartsWith(other, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    public static int Elements(string s) => s.Length == 0 ? 0 : new System.Globalization.StringInfo(s).LengthInTextElements;
}
