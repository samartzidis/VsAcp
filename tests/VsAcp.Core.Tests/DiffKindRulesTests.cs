using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

public class DiffKindRulesTests
{
    [Fact]
    public void DiffRequest_NormalisesSlashStyleSoOneFileIsOneEntry()
    {
        var a = new DiffRequest { RequestId = "1", FilePath = @"C:\ws\sub\IHello.cs", OriginalContent = "", ProposedContent = "" };
        var b = new DiffRequest { RequestId = "2", FilePath = "C:\\ws\\sub/IHello.cs", OriginalContent = "", ProposedContent = "" };
        var c = new DiffRequest { RequestId = "3", FilePath = "C:/ws/sub/../sub/IHello.cs", OriginalContent = "", ProposedContent = "" };

        Assert.Equal(a.FilePath, b.FilePath);
        Assert.Equal(a.FilePath, c.FilePath);
    }

    [Theory]
    [InlineData(DiffKind.Modified)]
    [InlineData(DiffKind.Added)]
    [InlineData(DiffKind.Deleted)]
    public void A_first_report_keeps_its_kind(DiffKind incoming)
    {
        Assert.Equal(incoming, DiffKindRules.Merge(null, incoming));
    }

    [Theory]
    [InlineData(DiffKind.Modified)]
    [InlineData(DiffKind.Added)]
    public void A_created_file_stays_created_however_often_it_is_edited(DiffKind incoming)
    {
        Assert.Equal(DiffKind.Added, DiffKindRules.Merge(DiffKind.Added, incoming));
    }

    [Fact]
    public void A_created_file_that_is_deleted_again_is_no_change()
    {
        Assert.Null(DiffKindRules.Merge(DiffKind.Added, DiffKind.Deleted));
    }

    [Theory]
    [InlineData(DiffKind.Modified)]
    [InlineData(DiffKind.Deleted)]
    public void An_existing_file_that_is_deleted_is_deleted(DiffKind existing)
    {
        Assert.Equal(DiffKind.Deleted, DiffKindRules.Merge(existing, DiffKind.Deleted));
    }

    [Theory]
    [InlineData(DiffKind.Modified)]
    [InlineData(DiffKind.Added)]
    public void A_deleted_file_that_comes_back_is_a_modification(DiffKind incoming)
    {
        Assert.Equal(DiffKind.Modified, DiffKindRules.Merge(DiffKind.Deleted, incoming));
    }

    [Fact]
    public void A_modified_file_stays_modified()
    {
        Assert.Equal(DiffKind.Modified, DiffKindRules.Merge(DiffKind.Modified, DiffKind.Modified));
    }

    [Theory]
    [InlineData(DiffSource.Agent)]
    [InlineData(DiffSource.Scan)]
    public void A_first_report_keeps_its_source(DiffSource incoming)
    {
        Assert.Equal(incoming, DiffKindRules.MergeSource(null, incoming));
    }

    [Theory]
    [InlineData(DiffSource.Agent)]
    [InlineData(DiffSource.Scan)]
    public void A_file_the_agent_reported_stays_the_agents_whatever_reports_it_next(DiffSource incoming)
    {
        Assert.Equal(DiffSource.Agent, DiffKindRules.MergeSource(DiffSource.Agent, incoming));
    }

    [Fact]
    public void A_scan_found_file_the_agent_then_reports_moves_to_the_agent()
    {
        Assert.Equal(DiffSource.Agent, DiffKindRules.MergeSource(DiffSource.Scan, DiffSource.Agent));
    }

    [Fact]
    public void A_scan_found_file_found_again_stays_a_scan_change()
    {
        Assert.Equal(DiffSource.Scan, DiffKindRules.MergeSource(DiffSource.Scan, DiffSource.Scan));
    }

    [Theory]
    [InlineData(true, "same", "same", null)]
    [InlineData(true, "orig", "changed", DiffKind.Modified)]
    [InlineData(true, "orig", null, DiffKind.Deleted)]
    [InlineData(false, "", null, null)]
    [InlineData(false, "", "made", DiffKind.Added)]
    [InlineData(false, "", "", DiffKind.Added)]
    public void Classify_compares_the_file_on_disk_with_how_it_started(bool existedAtStart, string original, string? current, DiffKind? expected)
    {
        Assert.Equal(expected, DiffKindRules.Classify(existedAtStart, original, current));
    }

    [Fact]
    public void Undoing_a_modification_restores_the_original_content()
    {
        using var dir = new TempDirectory();
        dir.Write("a.txt", "agent version");

        DiffKindRules.RestoreOriginal(DiffKind.Modified, dir.Combine("a.txt"), "original");

        Assert.Equal("original", dir.Read("a.txt"));
    }

    [Fact]
    public void Undoing_a_created_file_deletes_it()
    {
        using var dir = new TempDirectory();
        dir.Write("new.txt", "agent made this");

        DiffKindRules.RestoreOriginal(DiffKind.Added, dir.Combine("new.txt"), "");

        Assert.False(dir.Exists("new.txt"));
    }

    [Fact]
    public void Undoing_a_created_file_that_is_already_gone_is_fine()
    {
        using var dir = new TempDirectory();

        DiffKindRules.RestoreOriginal(DiffKind.Added, dir.Combine("never.txt"), "");

        Assert.False(dir.Exists("never.txt"));
    }

    [Fact]
    public void Undoing_a_deleted_file_recreates_it_with_its_original_content()
    {
        using var dir = new TempDirectory();

        DiffKindRules.RestoreOriginal(DiffKind.Deleted, dir.Combine("a.txt"), "was here");

        Assert.Equal("was here", dir.Read("a.txt"));
    }

    [Fact]
    public void Undoing_a_deleted_file_also_recreates_a_folder_that_is_gone()
    {
        using var dir = new TempDirectory();

        DiffKindRules.RestoreOriginal(DiffKind.Deleted, dir.Combine(@"gone\deeper\a.txt"), "was here");

        Assert.Equal("was here", dir.Read(@"gone\deeper\a.txt"));
    }
}
