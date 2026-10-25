using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

public class ProjectFoldersTests
{
    private static string[] Outside(string solution, params string?[] projects) =>
        ProjectFolders.Outside(solution, projects, _ => true);

    [Fact]
    public void Folders_outside_the_solution_are_shared_without_a_trailing_separator()
    {
        var result = Outside(@"C:\src\app", @"C:\libs\shared\", @"C:\other");

        Assert.Equal(new[] { @"C:\libs\shared", @"C:\other" }, result);
    }

    [Fact]
    public void Folders_inside_the_solution_are_not_shared_again()
    {
        var result = Outside(@"C:\src\app", @"C:\src\app\Web", @"C:\src\app\", @"C:\src\app");

        Assert.Empty(result);
    }

    [Fact]
    public void A_folder_whose_name_only_starts_like_the_solution_folder_is_outside()
    {
        var result = Outside(@"C:\src\app", @"C:\src\app2", @"C:\src\application");

        Assert.Equal(new[] { @"C:\src\app2", @"C:\src\application" }, result);
    }

    [Fact]
    public void The_comparison_ignores_case_and_a_trailing_separator_on_the_solution_folder()
    {
        Assert.Empty(Outside(@"C:\SRC\App\", @"c:\src\app\web"));
    }

    [Fact]
    public void Duplicates_are_listed_once_ignoring_case()
    {
        var result = Outside(@"C:\src\app", @"C:\libs\Shared", @"c:\libs\shared\", @"C:\libs\SHARED");

        Assert.Equal(new[] { @"C:\libs\Shared" }, result);
    }

    [Fact]
    public void Relative_segments_are_normalized_before_comparing()
    {
        var result = Outside(@"C:\src\app", @"C:\src\app\..\lib", @"C:\src\app\Web\..\Api");

        Assert.Equal(new[] { @"C:\src\lib" }, result);
    }

    [Fact]
    public void Blank_missing_and_unusable_entries_are_skipped()
    {
        var result = Outside(@"C:\src\app", null, "", "   ", "bad|path?", @"C:\libs");

        Assert.Equal(new[] { @"C:\libs" }, result);
    }

    [Fact]
    public void A_blank_solution_folder_shares_nothing()
    {
        Assert.Empty(Outside("", @"C:\libs"));
        Assert.Empty(Outside("  ", @"C:\libs"));
    }

    [Fact]
    public void Folders_that_do_not_exist_are_not_shared()
    {
        var result = ProjectFolders.Outside(@"C:\src\app", new[] { @"C:\libs\there", @"C:\libs\gone" },
            dir => dir.EndsWith("there"));

        Assert.Equal(new[] { @"C:\libs\there" }, result);
    }

    [Fact]
    public void Real_folders_beside_the_solution_are_shared()
    {
        using var root = new TempDirectory();
        var solution = Path.Combine(root.Path, "app");
        var sibling = Path.Combine(root.Path, "lib");
        Directory.CreateDirectory(solution);
        Directory.CreateDirectory(sibling);
        Directory.CreateDirectory(Path.Combine(solution, "Web"));

        var result = ProjectFolders.Outside(solution, new[] { solution, Path.Combine(solution, "Web"), sibling, Path.Combine(root.Path, "missing") });

        Assert.Equal(new[] { sibling }, result);
    }

    [Fact]
    public void RelativeTo_shows_a_path_inside_the_root_with_forward_slashes()
    {
        Assert.Equal("src/Bridge/A.cs", ProjectFolders.RelativeTo(@"C:\repo", @"C:\repo\src\Bridge\A.cs"));
        Assert.Equal("A.cs", ProjectFolders.RelativeTo(@"C:\REPO\", @"c:\repo\A.cs"));
        Assert.Equal("B.cs", ProjectFolders.RelativeTo(@"C:\repo", @"C:\repo\src\..\B.cs"));
    }

    [Fact]
    public void RelativeTo_returns_the_path_unchanged_when_it_is_outside_or_unusable()
    {
        Assert.Equal(@"C:\repo2\A.cs", ProjectFolders.RelativeTo(@"C:\repo", @"C:\repo2\A.cs"));
        Assert.Equal(@"D:\x\A.cs", ProjectFolders.RelativeTo(@"C:\repo", @"D:\x\A.cs"));
        Assert.Equal(@"C:\repo", ProjectFolders.RelativeTo(@"C:\repo", @"C:\repo"));
        Assert.Equal(@"C:\repo\A.cs", ProjectFolders.RelativeTo("", @"C:\repo\A.cs"));
        Assert.Equal("bad|path?", ProjectFolders.RelativeTo(@"C:\repo", "bad|path?"));
    }

    [Fact]
    public void Normalize_gives_a_full_path_with_one_trailing_separator()
    {
        Assert.Equal(@"C:\a\b\", ProjectFolders.Normalize(@"C:\a\b"));
        Assert.Equal(@"C:\a\b\", ProjectFolders.Normalize(@"C:\a\b\\"));
        Assert.Equal(@"C:\a\b\", ProjectFolders.Normalize(@"C:\a\x\..\b"));
        Assert.Null(ProjectFolders.Normalize(""));
    }
}
