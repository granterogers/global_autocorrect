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

    public static int Elements(string s) => s.Length == 0 ? 0 : new System.Globalization.StringInfo(s).LengthInTextElements;
}
