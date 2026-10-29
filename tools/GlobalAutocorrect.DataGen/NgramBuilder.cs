using System.Text;
using System.Text.RegularExpressions;
using GlobalAutocorrect.Core.Instant;

namespace GlobalAutocorrect.DataGen;

/// <summary>
/// Builds <c>assets/ngram.bin</c> (7.1, 7.2, D14) from the Leipzig sentences file. The binary format is documented on
/// <c>GlobalAutocorrect.Core.Prediction.NgramModel</c>.
/// </summary>
internal static class NgramBuilder
{
    private const int VocabSize = 50_000;
    private const int TopK = 5;
    private const string SentenceStart = "<s>";

    private static readonly Regex AnyToken = new(@"[\p{L}\p{N}_]+(?:'[\p{L}\p{N}_]+)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WordForm = new("^[a-z]+('[a-z]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private struct Entry
    {
        public long Context;
        public int Word;
        public int Count;
    }

    public static void Build(string sentencesPath, string assetsDir, string output, int minCount)
    {
        var speller = HunspellSpellChecker.FromAssets(assetsDir);
        var validCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        bool IsValid(string word)
        {
            if (validCache.TryGetValue(word, out bool v)) return v;
            v = speller.IsValid(word) || speller.IsValid(char.ToUpperInvariant(word[0]) + word[1..]);
            validCache[word] = v;
            return v;
        }

        // Pass 1: word counts over valid words.
        var wordCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        long sentences = 0;
        foreach (var sentence in Sentences(sentencesPath))
        {
            sentences++;
            foreach (var token in Tokens(sentence))
            {
                if (token is null || !IsValid(token)) continue;
                wordCounts[token] = wordCounts.GetValueOrDefault(token) + 1;
            }
        }

        var top = wordCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(VocabSize)
            .ToList();

        // Rank index (by count) is used while counting; the file uses the ordinal-sorted index.
        int sentenceRank = top.Count;
        var rankOf = new Dictionary<string, int>(top.Count, StringComparer.Ordinal);
        for (int i = 0; i < top.Count; i++) rankOf[top[i].Key] = i;

        var sorted = top.Select(kv => (Word: kv.Key, Count: kv.Value))
            .Append((Word: SentenceStart, Count: sentences))
            .OrderBy(x => x.Word, StringComparer.Ordinal)
            .ToList();
        var sortedIndex = new Dictionary<string, int>(sorted.Count, StringComparer.Ordinal);
        for (int i = 0; i < sorted.Count; i++) sortedIndex[sorted[i].Word] = i;
        var remap = new int[top.Count + 1];
        for (int i = 0; i < top.Count; i++) remap[i] = sortedIndex[top[i].Key];
        remap[sentenceRank] = sortedIndex[SentenceStart];

        // Pass 2: bigram and trigram counts over vocabulary words (anything else breaks the context).
        var bigrams = new Dictionary<long, int>();
        var trigrams = new Dictionary<long, int>();
        foreach (var sentence in Sentences(sentencesPath))
        {
            int p2 = -1, p1 = sentenceRank;
            foreach (var token in Tokens(sentence))
            {
                if (token is null || !rankOf.TryGetValue(token, out int w))
                {
                    p2 = -1;
                    p1 = -1;
                    continue;
                }

                if (p1 >= 0)
                {
                    long bk = ((long)p1 << 32) | (uint)w;
                    bigrams[bk] = bigrams.GetValueOrDefault(bk) + 1;
                    if (p2 >= 0)
                    {
                        long tk = ((long)p2 << 32) | ((long)p1 << 16) | (uint)w;
                        trigrams[tk] = trigrams.GetValueOrDefault(tk) + 1;
                    }
                }

                p2 = p1;
                p1 = w;
            }
        }

        var biEntries = new List<Entry>();
        foreach (var kv in bigrams)
        {
            if (kv.Value < minCount) continue;
            biEntries.Add(new Entry { Context = remap[(int)(kv.Key >> 32)], Word = remap[(int)(kv.Key & 0xFFFF_FFFF)], Count = kv.Value });
        }

        var triEntries = new List<Entry>();
        foreach (var kv in trigrams)
        {
            if (kv.Value < minCount) continue;
            long a = remap[(int)(kv.Key >> 32)];
            long b = remap[(int)((kv.Key >> 16) & 0xFFFF)];
            triEntries.Add(new Entry { Context = (a << 32) | (uint)b, Word = remap[(int)(kv.Key & 0xFFFF)], Count = kv.Value });
        }

        bigrams.Clear();
        trigrams.Clear();

        var biContexts = Group(biEntries);
        var triContexts = Group(triEntries);

        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs, new UTF8Encoding(false)))
        {
            w.Write((byte)'G');
            w.Write((byte)'A');
            w.Write((byte)'N');
            w.Write((byte)'G');
            w.Write(1);

            w.Write(sorted.Count);
            foreach (var (word, count) in sorted)
            {
                var bytes = Encoding.UTF8.GetBytes(word);
                w.Write((byte)bytes.Length);
                w.Write(bytes);
                w.Write(count);
            }

            w.Write(biContexts.Count);
            foreach (var (ctx, conts) in biContexts)
            {
                w.Write((int)ctx);
                w.Write((byte)conts.Count);
                foreach (var c in conts)
                {
                    w.Write(c.Word);
                    w.Write(c.Count);
                }
            }

            w.Write(triContexts.Count);
            foreach (var (ctx, conts) in triContexts)
            {
                w.Write((int)(ctx >> 32));
                w.Write((int)(ctx & 0xFFFF_FFFF));
                w.Write((byte)conts.Count);
                foreach (var c in conts)
                {
                    w.Write(c.Word);
                    w.Write(c.Count);
                }
            }
        }

        long size = new FileInfo(output).Length;
        Console.WriteLine($"ngram: {sentences} sentences, min count {minCount}");
        Console.WriteLine($"ngram: vocab {top.Count}, bigram contexts {biContexts.Count}, trigram contexts {triContexts.Count}");
        Console.WriteLine($"ngram: wrote {output} ({size / 1024.0 / 1024.0:F2} MB, {size} bytes)");
    }

    /// <summary>Groups entries by context (ascending), keeping the top continuations by count then word index.</summary>
    private static List<(long Context, List<Entry> Continuations)> Group(List<Entry> entries)
    {
        entries.Sort((x, y) =>
        {
            int c = x.Context.CompareTo(y.Context);
            if (c != 0) return c;
            c = y.Count.CompareTo(x.Count);
            return c != 0 ? c : x.Word.CompareTo(y.Word);
        });

        var result = new List<(long, List<Entry>)>();
        long current = long.MinValue;
        List<Entry>? list = null;
        foreach (var e in entries)
        {
            if (list is null || e.Context != current)
            {
                current = e.Context;
                list = new List<Entry>(TopK);
                result.Add((current, list));
            }

            if (list.Count < TopK) list.Add(e);
        }

        return result;
    }

    private static IEnumerable<string> Sentences(string path)
    {
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            int tab = line.IndexOf('\t');
            if (tab < 0) continue;
            yield return line[(tab + 1)..];
        }
    }

    /// <summary>Lower-case word tokens (<c>[a-z]+('[a-z]+)?</c>); null stands for any other token, which breaks the context.</summary>
    private static IEnumerable<string?> Tokens(string sentence)
    {
        string text = sentence.Replace('’', '\'').ToLowerInvariant();
        foreach (Match m in AnyToken.Matches(text))
        {
            yield return WordForm.IsMatch(m.Value) ? m.Value : null;
        }
    }
}
