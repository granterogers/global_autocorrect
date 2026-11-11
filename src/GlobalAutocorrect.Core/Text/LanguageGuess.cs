namespace GlobalAutocorrect.Core.Text;

/// <summary>
/// D116, user request: "detecting when I'm speaking Spanish ... for now, just ignore any languages that aren't English"
/// (and 1.9: UK English only). Text that reads as another language gets no fixes at all: neither layer touches it and it is
/// not sent to the AI. The test is the words every sentence is full of: "the, and, with" against Spanish "el, que, para",
/// and the same for French, Portuguese, Italian and German, plus letters and marks English never uses (ñ, ¿, ¡, ß, accents).
/// Words that are also English ("a", "no", "me", "come", "son") don't count for the other language. One foreign word in an
/// English sentence ("we had a fiesta") leaves it English.
/// </summary>
public static class LanguageGuess
{
    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "to", "of", "in", "is", "it", "you", "that", "for", "on", "with", "this", "are", "be", "was", "have",
        "i", "we", "not", "but", "at", "your", "my", "can", "will", "do", "so", "if", "as", "all", "just", "from", "they",
        "or", "an", "our", "what", "there", "would", "about", "know", "please", "thanks", "hi", "hello", "dear", "regards",
        "could", "should", "been", "had", "has", "were", "which", "when", "how", "any", "some", "them", "their", "these",
        "those", "into", "than", "then", "also", "very", "here", "today", "tomorrow", "let", "get", "got", "it's", "i'm",
        "don't", "can't", "we're", "you're", "that's", "there's", "i'll", "we'll", "thank", "best", "kind",
    };

    // Each language's most common words that are NOT English words too.
    private static readonly Dictionary<string, HashSet<string>> Others = new()
    {
        ["es"] = Set("el", "la", "los", "las", "que", "de", "y", "en", "un", "una", "es", "por", "para", "con", "lo", "como",
            "más", "mas", "pero", "sus", "ya", "este", "esta", "esto", "porque", "entre", "cuando", "muy", "sin", "sobre",
            "también", "hasta", "hay", "donde", "quien", "desde", "todo", "nos", "durante", "todos", "uno", "les", "ni",
            "otros", "ese", "eso", "ellos", "qué", "unos", "yo", "otro", "otra", "él", "tanto", "esa", "estos", "mucho",
            "nada", "muchos", "cual", "poco", "ella", "estar", "estas", "algo", "nosotros", "mi", "mis", "tú", "te", "ti",
            "tu", "tus", "usted", "ustedes", "gracias", "hola", "buenos", "buenas", "días", "dias", "saludos", "señor",
            "por favor", "favor", "está", "estoy", "estamos", "son", "soy", "fue", "ser", "tengo", "tiene", "tenemos",
            "puede", "puedo", "hacer", "del", "al", "le", "se", "su", "muchas", "bien", "aquí", "aqui", "ahora", "mañana",
            "manana", "hoy", "ayer", "necesito", "quiero", "vamos", "llamar", "correo", "también", "tambien", "pues"),
        ["fr"] = Set("le", "les", "des", "et", "est", "une", "du", "dans", "qui", "pour", "pas", "sur", "avec", "ce", "il",
            "elle", "nous", "vous", "ils", "mais", "ou", "où", "donc", "très", "tres", "aussi", "bonjour", "merci", "je",
            "suis", "sont", "être", "avoir", "fait", "cette", "ces", "leur", "votre", "notre", "chez", "aujourd'hui"),
        ["pt"] = Set("o", "os", "da", "do", "das", "dos", "em", "um", "uma", "não", "nao", "com", "para", "por", "mais",
            "como", "mas", "foi", "ao", "ele", "ela", "das", "tem", "seu", "sua", "ou", "ser", "quando", "muito", "obrigado",
            "obrigada", "olá", "ola", "você", "voce", "está", "estou", "também", "nós", "isso", "este", "esta"),
        ["it"] = Set("il", "di", "che", "è", "e", "la", "per", "un", "una", "non", "sono", "con", "mi", "ma", "come",
            "anche", "questo", "questa", "della", "delle", "degli", "nel", "nella", "grazie", "ciao", "buongiorno", "sei",
            "molto", "perché", "perche", "cosa", "io", "lei", "lui", "noi", "voi", "loro"),
        ["de"] = Set("der", "die", "das", "und", "ist", "nicht", "ich", "sie", "es", "ein", "eine", "zu", "den", "mit",
            "auf", "für", "fur", "von", "sich", "dem", "auch", "wir", "ihr", "aber", "oder", "wie", "noch", "nach", "bei",
            "danke", "bitte", "hallo", "guten", "morgen", "heute", "sehr", "gut", "habe", "haben", "wird", "werden"),
    };

    private static HashSet<string> Set(params string[] words) =>
        new(words.Where(w => !English.Contains(w) && w is not ("a" or "no" or "me" or "come" or "son" or "pan" or "con" or "can" or "die")),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Letters and marks English text never needs.</summary>
    private static bool ForeignMark(char c) => c is 'ñ' or 'Ñ' or '¿' or '¡' or 'ß' or 'ã' or 'õ' or 'ç' or 'à' or 'è' or 'ì' or 'ò'
        or 'ù' or 'á' or 'í' or 'ó' or 'ú' or 'â' or 'ê' or 'î' or 'ô' or 'û' or 'ä' or 'ö' or 'ü' or 'Á' or 'É' or 'Í' or 'Ó' or 'Ú';

    /// <summary>
    /// True when <paramref name="text"/> reads as a language other than English: another language's common words
    /// (or its letters, counted as one word each, at most three) clearly outnumber English ones, with at least two of them.
    /// An "é" alone ("café", "résumé") is not enough.
    /// </summary>
    public static bool IsForeign(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var words = Tokeniser.Tokenise(text).Where(t => t.IsWord).Select(t => t.Text.ToLowerInvariant()).ToArray();
        int english = words.Count(English.Contains);
        int marks = Math.Min(3, text.Count(ForeignMark));
        int best = 0;
        foreach (var set in Others.Values) best = Math.Max(best, words.Count(set.Contains));
        int foreign = best + (best > 0 ? marks : text.Any(c => c is '¿' or '¡' or 'ñ' or 'Ñ') ? marks : 0);
        // A short line with no English word in it ("Hola Jose,") needs only one.
        return (foreign >= 2 && foreign > english * 1.5) || (english == 0 && foreign >= 1);
    }

    /// <summary>The sentence of <paramref name="text"/> that holds position <paramref name="at"/> (to the nearest . ! ? or line break).</summary>
    public static string SentenceAt(string text, int at)
    {
        at = Math.Clamp(at, 0, text.Length);
        int s = at, e = at;
        while (s > 0 && text[s - 1] is not ('.' or '!' or '?' or '\n')) s--;
        while (e < text.Length && text[e] is not ('.' or '!' or '?' or '\n')) e++;
        return text[s..Math.Min(text.Length, e + 1)];
    }
}
