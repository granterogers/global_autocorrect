namespace GlobalAutocorrect.Core.Text;

/// <summary>One aligned group: ACount tokens of A correspond to BCount tokens of B (1:1, 1:0, 0:1, 2:1 merge, 1:2 split).</summary>
public readonly record struct AlignedUnit(int AStart, int ACount, int BStart, int BCount);

/// <summary>Needleman–Wunsch alignment with merge and split moves, used for word-level diffs and scoring.</summary>
public static class SequenceAligner
{
    private const byte Diag = 1, Del = 2, Ins = 3, Merge = 4, Split = 5;

    public static IReadOnlyList<AlignedUnit> Align(
        IReadOnlyList<string> a, IReadOnlyList<string> b, Func<string, string, double> pairCost,
        double gapCost = 0.7, double mergePenalty = 0.05)
    {
        int n = a.Count, m = b.Count;
        var d = new double[n + 1, m + 1];
        var back = new byte[n + 1, m + 1];
        for (int i = 1; i <= n; i++) { d[i, 0] = i * gapCost; back[i, 0] = Del; }
        for (int j = 1; j <= m; j++) { d[0, j] = j * gapCost; back[0, j] = Ins; }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                double best = d[i - 1, j - 1] + pairCost(a[i - 1], b[j - 1]);
                byte op = Diag;
                double v = d[i - 1, j] + gapCost;
                if (v < best) { best = v; op = Del; }
                v = d[i, j - 1] + gapCost;
                if (v < best) { best = v; op = Ins; }
                if (i >= 2)
                {
                    v = d[i - 2, j - 1] + pairCost(a[i - 2] + a[i - 1], b[j - 1]) + mergePenalty;
                    if (v < best) { best = v; op = Merge; }
                }
                if (j >= 2)
                {
                    v = d[i - 1, j - 2] + pairCost(a[i - 1], b[j - 2] + b[j - 1]) + mergePenalty;
                    if (v < best) { best = v; op = Split; }
                }
                d[i, j] = best;
                back[i, j] = op;
            }
        }

        var units = new List<AlignedUnit>();
        int x = n, y = m;
        while (x > 0 || y > 0)
        {
            switch (back[x, y])
            {
                case Diag: units.Add(new(x - 1, 1, y - 1, 1)); x--; y--; break;
                case Del: units.Add(new(x - 1, 1, y, 0)); x--; break;
                case Ins: units.Add(new(x, 0, y - 1, 1)); y--; break;
                case Merge: units.Add(new(x - 2, 2, y - 1, 1)); x -= 2; y--; break;
                case Split: units.Add(new(x - 1, 1, y - 2, 2)); x--; y -= 2; break;
                default: throw new InvalidOperationException("alignment backtrack failed");
            }
        }
        units.Reverse();
        return units;
    }
}
