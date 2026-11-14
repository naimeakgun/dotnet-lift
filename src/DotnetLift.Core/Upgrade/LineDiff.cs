using System.Text;

namespace DotnetLift.Core.Upgrade;

/// <summary>Minimal line-based unified diff (LCS). Good enough for project and config files.</summary>
public static class LineDiff
{
    public readonly record struct Stats(int Added, int Removed);

    private enum Op { Same, Add, Remove }

    public static Stats Count(string? before, string? after)
    {
        var ops = Compute(Split(before), Split(after));
        return new Stats(ops.Count(o => o.Op == Op.Add), ops.Count(o => o.Op == Op.Remove));
    }

    public static string Unified(string? before, string? after, string label, int context = 2)
    {
        var ops = Compute(Split(before), Split(after));
        var sb = new StringBuilder();
        sb.AppendLine($"--- {label}");
        sb.AppendLine($"+++ {label}");

        var changed = ops.Select((o, i) => (o, i)).Where(x => x.o.Op != Op.Same).Select(x => x.i).ToList();
        if (changed.Count == 0)
        {
            return sb.ToString();
        }

        // Group changed lines into hunks, keeping `context` unchanged lines around each.
        var hunks = new List<(int Start, int End)>();
        foreach (var i in changed)
        {
            var start = Math.Max(0, i - context);
            var end = Math.Min(ops.Count - 1, i + context);
            if (hunks.Count > 0 && start <= hunks[^1].End + 1)
            {
                hunks[^1] = (hunks[^1].Start, Math.Max(hunks[^1].End, end));
            }
            else
            {
                hunks.Add((start, end));
            }
        }

        foreach (var (start, end) in hunks)
        {
            sb.AppendLine("@@");
            for (var i = start; i <= end; i++)
            {
                var (op, line) = ops[i];
                sb.Append(op switch { Op.Add => '+', Op.Remove => '-', _ => ' ' });
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    private static string[] Split(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static List<(Op Op, string Line)> Compute(string[] a, string[] b)
    {
        // Standard LCS table; files handled here are small (project files, configs).
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<(Op, string)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                result.Add((Op.Same, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add((Op.Remove, a[x++]));
            }
            else
            {
                result.Add((Op.Add, b[y++]));
            }
        }

        while (x < a.Length) result.Add((Op.Remove, a[x++]));
        while (y < b.Length) result.Add((Op.Add, b[y++]));
        return result;
    }
}
