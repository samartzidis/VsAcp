using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

public sealed class FileLinkResolverTests : IDisposable
{
    private readonly TempDirectory _solution = new();

    public void Dispose() => _solution.Dispose();

    private string Make(string relativePath)
    {
        _solution.Write(relativePath, "x");
        return _solution.Combine(relativePath);
    }

    private ResolvedFileLink? Resolve(string raw) => FileLinkResolver.Resolve(raw, _solution.Path);

    // ---- the shapes of path an agent writes ----

    [Fact]
    public void A_full_path_resolves_to_itself()
    {
        var file = Make(@"src\a.cs");

        var link = Resolve(file);

        Assert.Equal(file, link!.FilePath);
        Assert.Equal(0, link.Line);
    }

    [Fact]
    public void A_path_relative_to_the_solution_is_resolved_against_it()
    {
        var file = Make(@"src\a.cs");

        Assert.Equal(file, Resolve(@"src\a.cs")!.FilePath);
    }

    [Fact]
    public void Forward_slashes_are_accepted()
    {
        var file = Make(@"src\a.cs");

        Assert.Equal(file, Resolve("src/a.cs")!.FilePath);
    }

    [Theory]
    [InlineData(":42", 42)]
    [InlineData(":42-51", 42)]
    [InlineData(":1", 1)]
    public void A_line_suffix_gives_the_line_and_is_not_part_of_the_name(string suffix, int line)
    {
        var file = Make(@"src\a.cs");

        var link = Resolve(file + suffix);

        Assert.Equal(file, link!.FilePath);
        Assert.Equal(line, link.Line);
    }

    [Fact]
    public void A_colon_that_is_not_a_line_suffix_is_left_alone()
    {
        // "C:\..." has a colon too; only a trailing :digits counts.
        var file = Make(@"src\a.cs");

        Assert.Equal(0, Resolve(file)!.Line);
        Assert.Null(Resolve(file + ":abc"));
    }

    [Fact]
    public void A_git_bash_style_path_is_converted_to_a_windows_path()
    {
        var file = Make(@"src\a.cs");
        var drive = file[0];
        var rest = file.Substring(3).Replace('\\', '/');

        var link = Resolve($"/{char.ToLowerInvariant(drive)}/{rest}:7");

        Assert.Equal(file, link!.FilePath, ignoreCase: true);
        Assert.Equal(7, link.Line);
    }

    // ---- relative to a subfolder ----

    [Fact]
    public void A_path_relative_to_a_subfolder_is_found_by_its_unique_ending()
    {
        var file = Make(@"src\Lib.Core\README.md");

        var link = Resolve("Lib.Core/README.md");

        Assert.Equal(file, link!.FilePath);
    }

    [Fact]
    public void A_bare_file_name_that_exists_once_is_found()
    {
        var file = Make(@"deep\er\Unique.cs");

        Assert.Equal(file, Resolve("Unique.cs")!.FilePath);
    }

    [Fact]
    public void An_ambiguous_ending_is_rejected()
    {
        Make(@"one\Same.cs");
        Make(@"two\Same.cs");

        Assert.Null(Resolve("Same.cs"));
    }

    [Fact]
    public void A_direct_hit_beats_the_search_even_when_the_name_exists_elsewhere()
    {
        var direct = Make("Same.cs");
        Make(@"other\Same.cs");

        Assert.Equal(direct, Resolve("Same.cs")!.FilePath);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".git")]
    public void Build_output_and_tool_folders_are_not_searched(string folder)
    {
        Make($@"{folder}\Hidden.cs");

        Assert.Null(Resolve("Hidden.cs"));
    }

    [Fact]
    public void A_file_in_a_skipped_folder_does_not_make_a_real_match_ambiguous()
    {
        Make(@"obj\Twin.cs");
        var real = Make(@"src\Twin.cs");

        Assert.Equal(real, Resolve("Twin.cs")!.FilePath);
    }

    [Fact]
    public void The_ending_must_match_whole_path_segments()
    {
        Make(@"src\MyLib.Core\README.md");

        Assert.Null(Resolve("Lib.Core/README.md"));
    }

    // ---- not found ----

    [Fact]
    public void A_file_that_does_not_exist_is_not_resolved()
    {
        Assert.Null(Resolve(@"nope\missing.cs"));
        Assert.Null(Resolve(_solution.Combine("missing.cs")));
    }

    [Fact]
    public void A_relative_path_with_no_solution_is_not_resolved()
    {
        Make("a.cs");

        Assert.Null(FileLinkResolver.Resolve("a.cs", null));
    }

    [Fact]
    public void A_full_path_still_resolves_with_no_solution()
    {
        var file = Make("a.cs");

        Assert.Equal(file, FileLinkResolver.Resolve(file, null)!.FilePath);
    }

    [Fact]
    public void A_path_made_only_of_dots_finds_nothing()
    {
        Assert.Null(FileLinkResolver.FindUniqueFileBySuffix(_solution.Path, @"..\.."));
    }
}
