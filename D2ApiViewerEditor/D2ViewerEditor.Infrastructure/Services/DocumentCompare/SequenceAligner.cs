namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

public sealed class SequenceAligner
{
    private readonly long _lcsCellLimit;
    private readonly int _greedyWindow;

    public SequenceAligner(long lcsCellLimit, int greedyWindow)
    {
        _lcsCellLimit = lcsCellLimit;
        _greedyWindow = greedyWindow;
    }

    public IReadOnlyList<(int Left, int Right)> Align(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return [];
        }

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var a = left.Select(key => Intern(ids, key)).ToArray();
        var b = right.Select(key => Intern(ids, key)).ToArray();

        return (long)a.Length * b.Length <= _lcsCellLimit ? Lcs(a, b) : Greedy(a, b);
    }

    private static int Intern(Dictionary<string, int> ids, string key)
    {
        if (!ids.TryGetValue(key, out var id))
        {
            id = ids.Count;
            ids[key] = id;
        }

        return id;
    }

    private static List<(int, int)> Lcs(int[] a, int[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var table = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] = a[i] == b[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var pairs = new List<(int, int)>();
        int x = 0, y = 0;

        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                pairs.Add((x, y));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return pairs;
    }

    private List<(int, int)> Greedy(int[] a, int[] b)
    {
        var pairs = new List<(int, int)>();
        int i = 0, j = 0;

        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                pairs.Add((i, j));
                i++;
                j++;
                continue;
            }

            var skipLeft = FindAhead(a, i, b[j]);
            var skipRight = FindAhead(b, j, a[i]);

            if (skipLeft < 0 && skipRight < 0)
            {
                i++;
                j++;
            }
            else if (skipRight < 0 || (skipLeft >= 0 && skipLeft <= skipRight))
            {
                i += skipLeft;
            }
            else
            {
                j += skipRight;
            }
        }

        return pairs;
    }

    private int FindAhead(int[] sequence, int start, int key)
    {
        var limit = Math.Min(sequence.Length - 1, start + _greedyWindow);

        for (var index = start + 1; index <= limit; index++)
        {
            if (sequence[index] == key)
            {
                return index - start;
            }
        }

        return -1;
    }
}
