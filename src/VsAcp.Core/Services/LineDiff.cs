namespace VsAcp.Core.Services;

/// <summary>
/// One changed region between two versions of a file, in whole lines (0-based): <see cref="OldCount"/> lines of
/// the original from <see cref="OldStart"/> became <see cref="NewCount"/> lines of the current text from
/// <see cref="NewStart"/>. A count of 0 is a pure insertion or deletion at that position.
/// </summary>
public sealed class LineHunk
{
    public int OldStart { get; init; }
    public int OldCount { get; init; }
    public int NewStart { get; init; }
    public int NewCount { get; init; }

    /// <summary>The original lines this hunk replaced, without line endings (what an Undo brings back).</summary>
    public IReadOnlyList<string> DeletedLines { get; init; } = Array.Empty<string>();
}

/// <summary>A text replacement in the current text: <see cref="Length"/> characters from <see cref="Start"/> become <see cref="Text"/>.</summary>
public readonly record struct TextReplacement(int Start, int Length, string Text);

/// <summary>
/// Line diff for the pending-changes list and the editor's inline diff, plus keeping or undoing one hunk.
/// <para>
/// Lines are split the way the editor counts them (<c>\r\n</c>, <c>\n</c>, <c>\r</c>) so hunk line numbers index
/// straight into an editor snapshot. Lines are compared without their endings, so converting a file's line endings
/// is not a change; the one exception is a last line without an ending, which differs from the same text with one
/// (otherwise adding or removing the final newline could not be kept or undone exactly). Keeping or undoing a hunk
/// copies whole lines, endings included, from the side they come from.
/// </para>
/// </summary>
public static class LineDiff
{
    /// <summary>
    /// Above this many cells (changed lines of one version times those of the other, after the common start and end
    /// are trimmed) the changed middle is reported as one hunk instead of being diffed line by line, which needs a
    /// table of that size (4 bytes a cell).
    /// </summary>
    public const long DefaultMaxLcsCells = 2_000_000;

    public static IReadOnlyList<LineHunk> Compute(string original, string current, long maxLcsCells = DefaultMaxLcsCells)
    {
        var a = Lines.Split(original);
        var b = Lines.Split(current);

        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a.Key(prefix) == b.Key(prefix)) prefix++;
        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix
               && a.Key(a.Count - 1 - suffix) == b.Key(b.Count - 1 - suffix))
            suffix++;

        int m = a.Count - prefix - suffix, n = b.Count - prefix - suffix;
        var hunks = new List<LineHunk>();
        if (m == 0 && n == 0) return hunks;
        if (m == 0 || n == 0 || (long)m * n > maxLcsCells)
        {
            hunks.Add(Hunk(a, prefix, m, prefix, n));
            return hunks;
        }

        // dp[i, j] = length of the longest common subsequence of a[prefix + i ..] and b[prefix + j ..].
        var dp = new int[m + 1, n + 1];
        for (int i = m - 1; i >= 0; i--)
            for (int j = n - 1; j >= 0; j--)
                dp[i, j] = a.Key(prefix + i) == b.Key(prefix + j)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        // Walk forward; each run of unmatched lines between two matches is one hunk.
        int x = 0, y = 0, gapX = 0, gapY = 0;
        while (x < m || y < n)
        {
            if (x < m && y < n && a.Key(prefix + x) == b.Key(prefix + y))
            {
                if (x > gapX || y > gapY) hunks.Add(Hunk(a, prefix + gapX, x - gapX, prefix + gapY, y - gapY));
                x++;
                y++;
                gapX = x;
                gapY = y;
            }
            else if (y >= n || (x < m && dp[x + 1, y] >= dp[x, y + 1]))
                x++;
            else
                y++;
        }
        if (x > gapX || y > gapY) hunks.Add(Hunk(a, prefix + gapX, x - gapX, prefix + gapY, y - gapY));
        return hunks;
    }

    /// <summary>The original with <paramref name="hunk"/> applied: keeping it makes it part of the starting point, so it is no longer a change.</summary>
    public static string KeepHunk(string original, string current, LineHunk hunk)
    {
        var a = Lines.Split(original);
        var b = Lines.Split(current);
        Check(a, b, hunk);
        return a.Join(0, hunk.OldStart) + b.Join(hunk.NewStart, hunk.NewCount) + a.Join(hunk.OldStart + hunk.OldCount, a.Count - hunk.OldStart - hunk.OldCount);
    }

    /// <summary>The edit to <paramref name="current"/> that puts the original lines of <paramref name="hunk"/> back.</summary>
    public static TextReplacement UndoEdit(string original, string current, LineHunk hunk)
    {
        var a = Lines.Split(original);
        var b = Lines.Split(current);
        Check(a, b, hunk);
        return new TextReplacement(b.Offset(hunk.NewStart), b.Join(hunk.NewStart, hunk.NewCount).Length, a.Join(hunk.OldStart, hunk.OldCount));
    }

    /// <summary><paramref name="current"/> with the original lines of <paramref name="hunk"/> put back.</summary>
    public static string UndoHunk(string original, string current, LineHunk hunk)
    {
        var edit = UndoEdit(original, current, hunk);
        return current.Substring(0, edit.Start) + edit.Text + current.Substring(edit.Start + edit.Length);
    }

    static LineHunk Hunk(Lines a, int oldStart, int oldCount, int newStart, int newCount)
    {
        var deleted = new string[oldCount];
        for (int i = 0; i < oldCount; i++) deleted[i] = a.Content(oldStart + i);
        return new LineHunk { OldStart = oldStart, OldCount = oldCount, NewStart = newStart, NewCount = newCount, DeletedLines = deleted };
    }

    static void Check(Lines a, Lines b, LineHunk hunk)
    {
        if (hunk.OldStart < 0 || hunk.OldCount < 0 || hunk.OldStart + hunk.OldCount > a.Count
            || hunk.NewStart < 0 || hunk.NewCount < 0 || hunk.NewStart + hunk.NewCount > b.Count)
            throw new ArgumentException("The hunk does not belong to these texts.", nameof(hunk));
    }

    /// <summary>A text cut into lines, each with its own line ending (the last one may have none).</summary>
    sealed class Lines
    {
        readonly string _text;
        readonly List<int> _starts = new();   // start of each line, plus the text length at the end
        readonly List<int> _contentEnds = new(); // end of each line's text, before its line ending

        Lines(string text) => _text = text;

        public int Count => _contentEnds.Count;

        public static Lines Split(string text)
        {
            var lines = new Lines(text);
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\n' && c != '\r') continue;
                lines._starts.Add(start);
                lines._contentEnds.Add(i);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            if (start < text.Length)
            {
                lines._starts.Add(start);
                lines._contentEnds.Add(text.Length);
            }
            lines._starts.Add(text.Length);
            return lines;
        }

        public string Content(int line) => _text.Substring(_starts[line], _contentEnds[line] - _starts[line]);

        /// <summary>What lines are compared by: the text without its ending, marked when it is a last line with no ending.</summary>
        public string Key(int line)
        {
            var content = Content(line);
            return _contentEnds[line] == _text.Length ? content + "\0<no line ending>" : content;
        }

        public int Offset(int line) => _starts[line];

        /// <summary>Lines <paramref name="first"/> .. <paramref name="first"/> + <paramref name="count"/> - 1, endings included.</summary>
        public string Join(int first, int count) => _text.Substring(_starts[first], _starts[first + count] - _starts[first]);
    }
}
