using System.Collections.Generic;
using System.Text;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Phase E — line-level diff between a method's original source
    /// body (Phase C's snapshot in <see cref="MethodPatchSpec.OriginalBody"/>)
    /// and the user's current edited body (<see cref="MethodPatchSpec.PatchBody"/>).
    /// The Patches view renders the result as a side-by-side coloring;
    /// the same data also feeds the "Copy diff to clipboard" path as a
    /// unified-diff text blob.
    ///
    /// Implementation note: this is a straightforward LCS (longest
    /// common subsequence) line-pair walk. Not the most efficient
    /// O(N*M) algorithm in the world, but patch bodies are short
    /// (typically &lt;100 lines) and the cost is invisible at edit-
    /// time. Doing real Myers / patience-diff would be overkill for
    /// this scale and would just trade clarity for performance we
    /// don't need.
    /// </summary>
    public static class PatchSourceDiff
    {
        public enum LineKind { Same, Added, Removed }

        public class Line
        {
            public LineKind Kind;
            public string Text;
        }

        public class Result
        {
            public List<Line> Lines = new();
            public int AddedCount;
            public int RemovedCount;

            public bool HasChanges => AddedCount > 0 || RemovedCount > 0;
        }

        public static Result Compute(string original, string current)
        {
            var origLines = SplitLines(original);
            var currLines = SplitLines(current);
            var lcs = BuildLcs(origLines, currLines);
            return WalkBackwards(origLines, currLines, lcs);
        }

        // Format the diff as a `--- / +++ / @@` style blob suitable
        // for clipboard paste into a code review or IDE diff viewer.
        // Not a strictly RFC-conformant unified diff (no @@ hunk
        // headers, no a/ b/ paths) — just enough structure that
        // `pbpaste | git apply --check` would reject and a human
        // can still read it.
        public static string FormatUnified(Result diff, string label = null)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(label))
            {
                sb.AppendLine($"--- {label} (original)");
                sb.AppendLine($"+++ {label} (patch)");
            }
            else
            {
                sb.AppendLine("--- original");
                sb.AppendLine("+++ patch");
            }
            foreach (var ln in diff.Lines)
            {
                switch (ln.Kind)
                {
                    case LineKind.Same:    sb.Append(' ').AppendLine(ln.Text); break;
                    case LineKind.Added:   sb.Append('+').AppendLine(ln.Text); break;
                    case LineKind.Removed: sb.Append('-').AppendLine(ln.Text); break;
                }
            }
            return sb.ToString();
        }

        // ─── internals ─────────────────────────────────────────────

        // Split preserving empty trailing lines so a body that ends
        // with `\n` doesn't appear identical to one without it. Both
        // CR+LF and bare LF are treated as line separators.
        private static string[] SplitLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return new[] { string.Empty };
            return s.Replace("\r\n", "\n").Split('\n');
        }

        // Standard LCS DP table.
        private static int[,] BuildLcs(string[] a, string[] b)
        {
            int n = a.Length, m = b.Length;
            var t = new int[n + 1, m + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < m; j++)
                {
                    if (a[i] == b[j]) t[i + 1, j + 1] = t[i, j] + 1;
                    else t[i + 1, j + 1] = t[i + 1, j] >= t[i, j + 1] ? t[i + 1, j] : t[i, j + 1];
                }
            }
            return t;
        }

        // Reconstruct the diff by walking the LCS table backwards.
        // We collect lines in reverse and reverse the list at the end
        // so the output reads top-to-bottom.
        private static Result WalkBackwards(string[] a, string[] b, int[,] t)
        {
            var lines = new List<Line>();
            int added = 0, removed = 0;
            int i = a.Length, j = b.Length;
            while (i > 0 && j > 0)
            {
                if (a[i - 1] == b[j - 1])
                {
                    lines.Add(new Line { Kind = LineKind.Same, Text = a[i - 1] });
                    i--; j--;
                }
                else if (t[i, j - 1] >= t[i - 1, j])
                {
                    lines.Add(new Line { Kind = LineKind.Added, Text = b[j - 1] });
                    added++;
                    j--;
                }
                else
                {
                    lines.Add(new Line { Kind = LineKind.Removed, Text = a[i - 1] });
                    removed++;
                    i--;
                }
            }
            while (i > 0) { lines.Add(new Line { Kind = LineKind.Removed, Text = a[--i] }); removed++; }
            while (j > 0) { lines.Add(new Line { Kind = LineKind.Added,   Text = b[--j] }); added++;   }

            lines.Reverse();
            return new Result { Lines = lines, AddedCount = added, RemovedCount = removed };
        }
    }
}
