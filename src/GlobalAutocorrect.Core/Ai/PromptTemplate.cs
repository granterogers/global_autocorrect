namespace GlobalAutocorrect.Core.Ai;

public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// The clean-up prompt (5.4). A setting (5.2): the fixed system prompt and examples come first so llama-server's
/// prompt cache is reused on every request; only the user's text changes. Examples are not from the eval data (6.1.e).
/// </summary>
public sealed record PromptTemplate(string System, IReadOnlyList<ChatTurn> Examples)
{
    public static PromptTemplate Default { get; } = new(
        """
        You fix typing mistakes in short messages written in British English.
        Fix: misspelt words; wrong, doubled, missing or swapped letters; words wrongly joined or split; missing or extra spaces; grammar slips; missing apostrophes; capital letters; punctuation. Work out the intended word from the surrounding words. Put back a small word (the, a, to, is) only when it was clearly dropped by mistake.
        Keep: the writer's own words, tone, slang and sentence structure. Do not rephrase, do not swap in synonyms, do not add or remove content, and do not change numbers, names, links or email addresses. Use British spelling (colour, organise, travelling).
        If the text has no mistakes, return it unchanged.
        Reply with the corrected text only: no quotes, no notes.
        """,
        [
            new("user", "cna you sned me teh report befor friday? thnaks"),
            new("assistant", "Can you send me the report before Friday? Thanks"),
            new("user", "im going to be late, the trian was cancled"),
            new("assistant", "I'm going to be late, the train was cancelled"),
            new("user", "Lovely to see you yesterday, let's do it again soon."),
            new("assistant", "Lovely to see you yesterday, let's do it again soon."),
            new("user", "we shoud organise a meeting nextweek to dicsuss the budjet and there plans"),
            new("assistant", "We should organise a meeting next week to discuss the budget and their plans"),
            new("user", "can you leave report on my desk, its for meeting at 3"),
            new("assistant", "Can you leave the report on my desk, it's for the meeting at 3"),
            new("user", "Fancy a quick coffee before the train?"),
            new("assistant", "Fancy a quick coffee before the train?"),
        ]);

    public IReadOnlyList<ChatTurn> BuildMessages(string text) =>
        [new("system", System), .. Examples, new("user", text)];

    /// <summary>Cap on generated tokens (5.4): the cleaned text is about as long as the input.</summary>
    public static int MaxTokensFor(string text) => Math.Min(512, (text.Length / 3) * 2 + 16);
}
