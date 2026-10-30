using System.Globalization;
using GlobalAutocorrect.Core.Engine;
using GlobalAutocorrect.Core.Session;
using GlobalAutocorrect.Core.Targets;

namespace GlobalAutocorrect.Core.Simulation;

/// <summary>
/// A text field for tests (10.2): models typing, caret moves, paste, foreign injection, host-app autocorrect and races.
/// It is also the key monitor: <see cref="LatestKeySeq"/> counts keys "the hook has seen", which can run ahead of what
/// the engine has been given (<see cref="TypeUndelivered"/>).
/// </summary>
public sealed class SimulatedEditor(IClock clock, string id = "sim-1") : ITextTarget, IKeyMonitor
{
    public string Text { get; set; } = "";
    public int Caret { get; set; }
    public string Id { get; set; } = id;
    public bool CaretKnown { get; set; } = true;

    /// <summary>Host autocorrect: called with the word just finished; return a replacement to apply it.</summary>
    public Func<string, string?>? HostAutocorrect { get; set; }

    /// <summary>Runs just before an injected batch lands, to model something changing the field at the worst moment.</summary>
    public Action<SimulatedEditor>? BeforeApply { get; set; }

    public int Edits { get; private set; }

    /// <summary>Left and Right presses our edits have used (D28: none for a word just finished).</summary>
    public int CaretMoves { get; private set; }
    public long LatestKeySeq { get; private set; }
    public long LastKeyAtMs { get; private set; } = -100_000;
    public bool ModifierHeld { get; set; }

    public KeyInput TypeChar(char c)
    {
        Insert(c.ToString());
        if ((char.IsWhiteSpace(c) || c is ',' or '.') && HostAutocorrect is not null) ApplyHostAutocorrect();
        return Key(KeyKind.Char, c);
    }

    /// <summary>A key the hook saw but the engine hasn't been given yet (D6 race).</summary>
    public KeyInput TypeUndelivered(char c) => TypeChar(c);

    public KeyInput Backspace()
    {
        if (Caret > 0)
        {
            int len = LastElementLength(Text[..Caret]);
            Text = Text.Remove(Caret - len, len);
            Caret -= len;
        }
        return Key(KeyKind.Backspace, '\0');
    }

    public KeyInput MoveCaret(int delta)
    {
        Caret = Math.Clamp(Caret + delta, 0, Text.Length);
        return Key(KeyKind.Navigation, '\0');
    }

    public KeyInput Paste(string s)
    {
        Insert(s);
        return Key(KeyKind.Paste, '\0');
    }

    /// <summary>Text inserted by another tool (dictation, expander); the hook reports it as foreign (4.2).</summary>
    public KeyInput ForeignInsert(string s)
    {
        Insert(s);
        return Key(KeyKind.Foreign, '\0');
    }

    /// <summary>The next N reads fail, as a busy app's UIA sometimes does for a moment.</summary>
    public int FailNextReads { get; set; }

    /// <summary>The next N reads miss the last two characters, as a browser that hasn't drawn the last keys yet.</summary>
    public int LagNextReads { get; set; }

    // D42: how real editors report text differently from the keys that made it (each one broke something in real use).
    /// <summary>A blank line (Enter Enter) reads as one line break, as Edge's rich editors report it.</summary>
    public bool ReportsBlankLinesOnce { get; set; }
    /// <summary>Two spaces read as a non-breaking space and a space, as browser editors store them.</summary>
    public bool ReportsDoubleSpaceAsNbsp { get; set; }
    /// <summary>Text the app shows after the caret that isn't in the document (Edge's text prediction), on some reads.</summary>
    public string GhostAfterCaret { get; set; } = "";
    /// <summary>Randomness for <see cref="GhostChance"/> and <see cref="LagChance"/>.</summary>
    public Random? Chance { get; set; }
    public double GhostChance { get; set; }
    public double LagChance { get; set; }

    private TextWindow Report(string before, string after)
    {
        if (ReportsBlankLinesOnce)
        {
            before = System.Text.RegularExpressions.Regex.Replace(before, "\n{2,}", "\n");
            after = System.Text.RegularExpressions.Regex.Replace(after, "\n{2,}", "\n");
        }
        if (ReportsDoubleSpaceAsNbsp) { before = before.Replace("  ", "  "); after = after.Replace("  ", "  "); }
        if (Chance is not null && GhostAfterCaret.Length > 0 && Chance.NextDouble() < GhostChance) after = GhostAfterCaret + after;
        if (Chance is not null && Chance.NextDouble() < LagChance && before.Length >= 2) before = before[..^2];
        return new TextWindow(TargetStatus.Ok, Id, before, after);
    }

    public TextWindow ReadWindow()
    {
        if (FailNextReads > 0) { FailNextReads--; return TextWindow.Unavailable(TargetStatus.Unavailable, Id); }
        if (!CaretKnown) return TextWindow.Unavailable(TargetStatus.CaretUnknown, Id);
        if (LagNextReads > 0 && Caret >= 2)
        {
            LagNextReads--;
            return new TextWindow(TargetStatus.Ok, Id, Text[Math.Max(0, Caret - 400)..(Caret - 2)], "");
        }
        return Report(Text[Math.Max(0, Caret - 400)..Caret], Text[Caret..Math.Min(Text.Length, Caret + 80)]);
    }

    public bool ApplyEdit(int caretDistance, int deleteCount, string replacement)
    {
        BeforeApply?.Invoke(this);
        // Exactly what the SendInput batch does: Left × d, Backspace × n, type, Right × d (10.7.a).
        int pos = Caret;
        for (int i = 0; i < caretDistance && pos > 0; i++) pos -= LastElementLength(Text[..pos]);
        int del = pos;
        for (int i = 0; i < deleteCount && del > 0; i++) del -= LastElementLength(Text[..del]);
        Text = Text[..del] + replacement + Text[pos..];
        Caret = del + replacement.Length + (Caret - pos);
        Edits++;
        CaretMoves += caretDistance * 2;
        return true;
    }

    /// <summary>Characters "typed" between the write and its verification (models a user who keeps typing, 4.6.e).</summary>
    public string TypedDuringVerify { get; set; } = "";

    public bool Verify(string expectedBefore, string expectedAfter)
    {
        if (TypedDuringVerify.Length > 0) { Insert(TypedDuringVerify); TypedDuringVerify = ""; }
        // As the Windows target compares (D42): blank lines count once, typing (Enter included) may follow the fix, and
        // the text after the caret only has to still be there. It also reads a few times, as the real one polls.
        string e = TextNorm.Comparable(expectedBefore);
        string tail = e.Length > 48 ? e[^48..] : e;
        string afterLine = TextNorm.Comparable(expectedAfter).Split('\n')[0];
        string afterKey = afterLine.Length > 24 ? afterLine[..24] : afterLine;
        for (int i = 0; i < 3; i++)
        {
            var w = ReadWindow();
            string b = TextNorm.Comparable(w.Before);
            int at = b.LastIndexOf(tail, StringComparison.Ordinal);
            bool beforeOk = at >= 0 && b.Length - (at + tail.Length) <= 64;
            if (w.CaretKnown && beforeOk && Targets.TextNorm.AfterConsistent(afterKey, TextNorm.Comparable(w.After))) return true;
        }
        return false;
    }

    private KeyInput Key(KeyKind kind, char c)
    {
        LatestKeySeq++;
        LastKeyAtMs = clock.NowMs;
        return new KeyInput(kind, c, LatestKeySeq, clock.NowMs);
    }

    private void Insert(string s)
    {
        Text = Text.Insert(Caret, s);
        Caret += s.Length;
    }

    private void ApplyHostAutocorrect()
    {
        int end = Caret - 1, start = end;
        while (start > 0 && char.IsLetter(Text[start - 1])) start--;
        if (end <= start) return;
        string? fix = HostAutocorrect!(Text[start..end]);
        if (fix is null) return;
        Text = Text[..start] + fix + Text[end..];
        Caret += fix.Length - (end - start);
    }

    private static int LastElementLength(string s)
    {
        if (s.Length == 0) return 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        int last = 0;
        while (e.MoveNext()) last = e.ElementIndex;
        return s.Length - last;
    }
}
