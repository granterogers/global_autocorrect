namespace GlobalAutocorrect.Core.Text;

/// <summary>D96, user report (Intercom: "Are you able to jump in? IT shouldn't take long."): Shift held a moment too long.</summary>
public static class Capitals
{
    /// <summary>
    /// The word as meant when Shift caught the second letter too: "IT" → "It", "THe" → "The", "WHen" → "When". Null unless
    /// exactly the first two letters are capitals and the rest (if any) lower case.
    /// </summary>
    public static string? HeldShift(string w)
    {
        if (w.Length < 2 || !char.IsUpper(w[0]) || !char.IsUpper(w[1])) return null;
        if (w.Length == 3 && w[2] == 's') return null;   // a plural acronym: "IDs", "PCs", "TVs"
        for (int i = 2; i < w.Length; i++) if (!char.IsLower(w[i])) return null;
        return w[0] + w[1..].ToLowerInvariant();
    }
}
