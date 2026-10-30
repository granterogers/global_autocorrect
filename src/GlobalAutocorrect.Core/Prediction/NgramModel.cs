using System.Text;

namespace GlobalAutocorrect.Core.Prediction;

/// <summary>
/// Read-only word n-gram model (7.1, 7.2) built by <c>DataGen ngram</c> from the Leipzig UK corpus (D14).
/// All words are valid in Hunspell en_GB-ise, so every suggestion is British (3.4). Immutable and thread-safe.
/// <para>
/// File format (little-endian; <c>assets/ngram.bin</c>):
/// <code>
/// magic    "GANG" (4 ASCII bytes)
/// version  int32 = 1
/// vocab    int32 n, then n times: byte length, UTF-8 bytes, int64 count
///          sorted by word with ordinal comparison; a word's index is its position in this table.
///          Entry "&lt;s&gt;" (sentence start, count = number of sentences) is included; it is never a prediction.
/// bigrams  int32 n, then n times: int32 w1, byte k, then k times (int32 w, int32 count)
///          sorted by w1; continuations sorted by count descending (k at most 5)
/// trigrams int32 n, then n times: int32 w1, int32 w2, byte k, then k times (int32 w, int32 count)
///          sorted by (w1, w2); continuations sorted by count descending (k at most 5)
/// </code>
/// </para>
/// </summary>
public sealed class NgramModel
{
    /// <summary>Vocabulary entry for a sentence start. Pass it as <c>w2</c> to <see cref="Next"/>.</summary>
    public const string SentenceStart = "<s>";

    private const int Version = 1;

    private readonly string[] _words;
    private readonly long[] _counts;

    private readonly int[] _biContext;
    private readonly int[] _biStart;
    private readonly int[] _biWord;
    private readonly int[] _biCount;

    private readonly long[] _triContext;
    private readonly int[] _triStart;
    private readonly int[] _triWord;
    private readonly int[] _triCount;

    private NgramModel(
        string[] words, long[] counts,
        int[] biContext, int[] biStart, int[] biWord, int[] biCount,
        long[] triContext, int[] triStart, int[] triWord, int[] triCount)
    {
        _words = words;
        _counts = counts;
        _biContext = biContext;
        _biStart = biStart;
        _biWord = biWord;
        _biCount = biCount;
        _triContext = triContext;
        _triStart = triStart;
        _triWord = triWord;
        _triCount = triCount;
    }

    /// <summary>Number of vocabulary entries (including the sentence-start marker).</summary>
    public int VocabularySize => _words.Length;

    public static NgramModel FromAssets(string assetsDir) => Load(Path.Combine(assetsDir, "ngram.bin"));

    public static NgramModel Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var r = new BinaryReader(stream, Encoding.UTF8);

        if (r.ReadByte() != (byte)'G' || r.ReadByte() != (byte)'A' || r.ReadByte() != (byte)'N' || r.ReadByte() != (byte)'G')
        {
            throw new InvalidDataException("Not an n-gram file.");
        }

        if (r.ReadInt32() != Version)
        {
            throw new InvalidDataException("Unsupported n-gram file version.");
        }

        int vocab = r.ReadInt32();
        if (vocab < 0 || vocab > 1_000_000)
        {
            throw new InvalidDataException("Bad vocabulary size.");
        }

        var words = new string[vocab];
        var counts = new long[vocab];
        for (int i = 0; i < vocab; i++)
        {
            int len = r.ReadByte();
            words[i] = Encoding.UTF8.GetString(r.ReadBytes(len));
            counts[i] = r.ReadInt64();
        }

        int biN = r.ReadInt32();
        var biContext = new int[biN];
        var biStart = new int[biN + 1];
        var biWord = new List<int>(biN * 4);
        var biCount = new List<int>(biN * 4);
        for (int i = 0; i < biN; i++)
        {
            biContext[i] = r.ReadInt32();
            biStart[i] = biWord.Count;
            int k = r.ReadByte();
            for (int j = 0; j < k; j++)
            {
                biWord.Add(r.ReadInt32());
                biCount.Add(r.ReadInt32());
            }
        }

        biStart[biN] = biWord.Count;

        int triN = r.ReadInt32();
        var triContext = new long[triN];
        var triStart = new int[triN + 1];
        var triWord = new List<int>(triN * 4);
        var triCount = new List<int>(triN * 4);
        for (int i = 0; i < triN; i++)
        {
            long w1 = r.ReadInt32();
            long w2 = r.ReadInt32();
            triContext[i] = (w1 << 32) | (uint)w2;
            triStart[i] = triWord.Count;
            int k = r.ReadByte();
            for (int j = 0; j < k; j++)
            {
                triWord.Add(r.ReadInt32());
                triCount.Add(r.ReadInt32());
            }
        }

        triStart[triN] = triWord.Count;

        return new NgramModel(
            words, counts,
            biContext, biStart, [.. biWord], [.. biCount],
            triContext, triStart, [.. triWord], [.. triCount]);
    }

    /// <summary>Corpus count of a vocabulary word (lower-case), or 0 when it isn't in the vocabulary.</summary>
    public long CountOf(string word)
    {
        int i = IndexOf(Normalise(word));
        return i < 0 ? 0 : _counts[i];
    }

    /// <summary>How often <paramref name="w2"/> followed <paramref name="w1"/> in the corpus; 0 if never or unknown (D38).</summary>
    public long PairCount(string w1, string w2)
    {
        int i1 = IndexOf(Normalise(w1)), i2 = IndexOf(Normalise(w2));
        if (i1 < 0 || i2 < 0) return 0;
        int b = Array.BinarySearch(_biContext, i1);
        if (b < 0) return 0;
        for (int j = _biStart[b]; j < _biStart[b + 1]; j++)
            if (_biWord[j] == i2) return _biCount[j];
        return 0;
    }

    /// <summary>
    /// Top <paramref name="k"/> vocabulary words that start with the lower-case <paramref name="prefix"/>, by corpus count.
    /// Binary search on the sorted vocabulary, then a scan of the matching range.
    /// </summary>
    public IReadOnlyList<(string Word, long Count)> Complete(string prefix, int k)
    {
        if (string.IsNullOrEmpty(prefix) || k <= 0)
        {
            return [];
        }

        prefix = Normalise(prefix);
        int lo = 0, hi = _words.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(_words[mid], prefix) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        var best = new List<(string Word, long Count)>(k + 1);
        for (int i = lo; i < _words.Length && _words[i].StartsWith(prefix, StringComparison.Ordinal); i++)
        {
            long c = _counts[i];
            if (best.Count == k && c <= best[^1].Count)
            {
                continue;
            }

            int pos = best.Count;
            while (pos > 0 && best[pos - 1].Count < c)
            {
                pos--;
            }

            best.Insert(pos, (_words[i], c));
            if (best.Count > k)
            {
                best.RemoveAt(k);
            }
        }

        return best;
    }

    /// <summary>
    /// Most likely next words: the trigram continuations of (<paramref name="w1"/>, <paramref name="w2"/>) if that context
    /// exists, else the bigram continuations of <paramref name="w2"/>, else empty. At a sentence start pass
    /// <c>w1 = null</c> and <c>w2 = "&lt;s&gt;"</c>.
    /// </summary>
    public IReadOnlyList<(string Word, long Count)> Next(string? w1, string? w2, int k)
    {
        if (w2 is null || k <= 0)
        {
            return [];
        }

        int i2 = IndexOf(Normalise(w2));
        if (i2 < 0)
        {
            return [];
        }

        if (w1 is not null)
        {
            int i1 = IndexOf(Normalise(w1));
            if (i1 >= 0)
            {
                long key = ((long)i1 << 32) | (uint)i2;
                int t = Array.BinarySearch(_triContext, key);
                if (t >= 0)
                {
                    return Take(_triStart[t], _triStart[t + 1], _triWord, _triCount, k);
                }
            }
        }

        int b = Array.BinarySearch(_biContext, i2);
        return b >= 0 ? Take(_biStart[b], _biStart[b + 1], _biWord, _biCount, k) : [];
    }

    private IReadOnlyList<(string Word, long Count)> Take(int from, int to, int[] words, int[] counts, int k)
    {
        int n = Math.Min(k, to - from);
        var result = new (string Word, long Count)[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = (_words[words[from + i]], counts[from + i]);
        }

        return result;
    }

    private int IndexOf(string word)
    {
        int lo = 0, hi = _words.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = string.CompareOrdinal(_words[mid], word);
            if (c == 0)
            {
                return mid;
            }

            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    private static string Normalise(string word) => word.Replace('’', '\'').ToLowerInvariant();
}
