using VsAcp.Core.Services;

namespace VsAcp.Core.Tests;

public class LineDiffTests
{
    static string L(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void Identical_texts_have_no_hunks()
    {
        Assert.Empty(LineDiff.Compute(L("a", "b"), L("a", "b")));
        Assert.Empty(LineDiff.Compute("", ""));
    }

    [Fact]
    public void A_changed_line_is_one_hunk_with_its_place_in_both_versions()
    {
        var hunk = Assert.Single(LineDiff.Compute(L("a", "b", "c"), L("a", "B", "c")));

        Assert.Equal((1, 1, 1, 1), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
        Assert.Equal(new[] { "b" }, hunk.DeletedLines);
    }

    [Fact]
    public void Inserted_and_removed_lines_have_a_zero_count_on_the_other_side()
    {
        var inserted = Assert.Single(LineDiff.Compute(L("a", "c"), L("a", "b", "c")));
        Assert.Equal((1, 0, 1, 1), (inserted.OldStart, inserted.OldCount, inserted.NewStart, inserted.NewCount));

        var removed = Assert.Single(LineDiff.Compute(L("a", "b", "c"), L("a", "c")));
        Assert.Equal((1, 1, 1, 0), (removed.OldStart, removed.OldCount, removed.NewStart, removed.NewCount));
        Assert.Equal(new[] { "b" }, removed.DeletedLines);
    }

    [Fact]
    public void Changes_in_different_places_are_separate_hunks()
    {
        var hunks = LineDiff.Compute(L("1", "2", "3", "4", "5", "6"), L("1", "two", "3", "4", "5", "six", "7"));

        Assert.Equal(2, hunks.Count);
        Assert.Equal((1, 1, 1, 1), (hunks[0].OldStart, hunks[0].OldCount, hunks[0].NewStart, hunks[0].NewCount));
        Assert.Equal((5, 1, 5, 2), (hunks[1].OldStart, hunks[1].OldCount, hunks[1].NewStart, hunks[1].NewCount));
    }

    [Fact]
    public void A_new_file_is_one_hunk_of_all_its_lines()
    {
        var hunk = Assert.Single(LineDiff.Compute("", L("a", "b")));
        Assert.Equal((0, 0, 0, 2), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
    }

    [Fact]
    public void Converting_line_endings_is_not_a_change()
    {
        Assert.Empty(LineDiff.Compute("a\r\nb\r\n", "a\nb\n"));
    }

    [Theory]
    [InlineData("a\nb", "a\nb\n")]
    [InlineData("a\nb\n", "a\nb")]
    public void Adding_or_removing_the_final_line_ending_is_a_change(string original, string current)
    {
        var hunk = Assert.Single(LineDiff.Compute(original, current));
        Assert.Equal(original, LineDiff.UndoHunk(original, current, hunk));
        Assert.Equal(current, LineDiff.KeepHunk(original, current, hunk));
    }

    [Fact]
    public void A_lone_carriage_return_ends_a_line_like_in_the_editor()
    {
        var hunk = Assert.Single(LineDiff.Compute("a\rb\rc", "a\rB\rc"));
        Assert.Equal(1, hunk.NewStart);
    }

    [Fact]
    public void Undoing_one_hunk_leaves_the_others()
    {
        var original = L("1", "2", "3", "4", "5", "6", "7");
        var current = L("one", "2", "3", "four", "5", "6", "seven");
        var hunks = LineDiff.Compute(original, current);
        Assert.Equal(3, hunks.Count);

        var after = LineDiff.UndoHunk(original, current, hunks[1]);

        Assert.Equal(L("one", "2", "3", "4", "5", "6", "seven"), after);
        var left = LineDiff.Compute(original, after);
        Assert.Equal(new[] { 0, 6 }, left.Select(h => h.NewStart));
    }

    [Fact]
    public void Keeping_one_hunk_takes_it_out_of_the_diff()
    {
        var original = L("1", "2", "3", "4", "5");
        var current = L("one", "2", "3", "4", "five", "6");
        var hunks = LineDiff.Compute(original, current);

        var kept = LineDiff.KeepHunk(original, current, hunks[0]);

        Assert.Equal(L("one", "2", "3", "4", "5"), kept);
        var left = Assert.Single(LineDiff.Compute(kept, current));
        Assert.Equal((4, 1, 4, 2), (left.OldStart, left.OldCount, left.NewStart, left.NewCount));
    }

    public static IEnumerable<object[]> Pairs() => new[]
    {
        new object[] { L("1", "2", "3", "4", "5", "6", "7", "8"), L("0", "1", "two", "3", "5", "6", "6b", "8", "9") },
        new object[] { "a\r\nb\r\nc\r\nd\r\n", "a\r\nB\r\nc\r\nd\r\ne" },
        new object[] { "", L("x", "y") },
        new object[] { L("x", "y"), "" },
        new object[] { "same\nlast", "same\nchanged" },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Undoing_every_hunk_one_by_one_gives_back_the_original(string original, string current)
    {
        var text = current;
        for (var hunks = LineDiff.Compute(original, text); hunks.Count > 0; hunks = LineDiff.Compute(original, text))
            text = LineDiff.UndoHunk(original, text, hunks[hunks.Count - 1]);

        Assert.Equal(original, text);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Keeping_every_hunk_one_by_one_makes_the_original_the_current_text(string original, string current)
    {
        var start = original;
        for (var hunks = LineDiff.Compute(start, current); hunks.Count > 0; hunks = LineDiff.Compute(start, current))
            start = LineDiff.KeepHunk(start, current, hunks[0]);

        Assert.Equal(current, start);
    }

    [Fact]
    public void Undoing_a_hunk_keeps_the_line_endings_of_the_original_lines()
    {
        var original = "a\r\nb\r\nc\r\n";
        var current = "a\r\nB\r\nc\r\n";

        var edit = LineDiff.UndoEdit(original, current, Assert.Single(LineDiff.Compute(original, current)));

        Assert.Equal(new TextReplacement(3, 3, "b\r\n"), edit);
    }

    [Fact]
    public void Above_the_size_cap_the_changed_middle_is_one_hunk()
    {
        var original = L("head", "1", "2", "3", "tail");
        var current = L("head", "one", "2", "three", "tail");

        var hunk = Assert.Single(LineDiff.Compute(original, current, maxLcsCells: 4));

        Assert.Equal((1, 3, 1, 3), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
        Assert.Equal(original, LineDiff.UndoHunk(original, current, hunk));
    }

    [Fact]
    public void A_hunk_from_other_texts_is_refused()
    {
        var hunk = new LineHunk { OldStart = 5, OldCount = 1, NewStart = 5, NewCount = 1 };
        Assert.Throws<ArgumentException>(() => LineDiff.KeepHunk(L("a"), L("b"), hunk));
    }
}
