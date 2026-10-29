namespace GlobalAutocorrect.Core.Text;

public enum TokenKind { Word, Punct, Space, Newline }

[Flags]
public enum TokenFlags
{
    None = 0,
    Number = 1,
    Url = 2,
    Email = 4,
    CodeLike = 8,
}

/// <summary>A slice of text. Start/End are UTF-16 offsets into the source string.</summary>
public readonly record struct Token(TokenKind Kind, int Start, int End, string Text, TokenFlags Flags)
{
    public int Length => End - Start;

    /// <summary>Numbers, URLs, emails and code-like tokens are never edited (4.3.c, 4.5.d).</summary>
    public bool IsProtected => (Flags & (TokenFlags.Number | TokenFlags.Url | TokenFlags.Email | TokenFlags.CodeLike)) != 0;

    public bool IsWord => Kind == TokenKind.Word;
}
