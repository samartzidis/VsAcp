using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

public class ComposerTests
{
    private static readonly IReadOnlyList<ImageAttachment> NoImages = Array.Empty<ImageAttachment>();
    private static readonly IReadOnlyList<FileAttachment> NoFiles = Array.Empty<FileAttachment>();
    private static readonly IReadOnlyList<FolderAttachment> NoFolders = Array.Empty<FolderAttachment>();
    private static readonly IReadOnlyList<CodeSelection> NoSelections = Array.Empty<CodeSelection>();

    private static bool Exists(string path) => !path.Contains("missing");

    private static TurnSubmission Build(
        string text = "hello",
        string? command = null,
        IReadOnlyList<ImageAttachment>? images = null,
        IReadOnlyList<FileAttachment>? files = null,
        IReadOnlyList<FolderAttachment>? folders = null,
        IReadOnlyList<CodeSelection>? selections = null,
        string? active = null,
        bool activeEnabled = true) =>
        Composer.BuildSubmission(text, command, images ?? NoImages, files ?? NoFiles, folders ?? NoFolders,
            selections ?? NoSelections, active, activeEnabled, Exists);

    // ---- building a submission ----

    [Fact]
    public void A_plain_message_is_trimmed_and_has_no_attachments()
    {
        var s = Build("  hello there \n");

        Assert.Equal("hello there", s.Message);
        Assert.Null(s.Images);
        Assert.Null(s.ImageDataUris);
        Assert.Null(s.FileNames);
        Assert.Null(s.FilePaths);
        Assert.Null(s.DroppedFilePaths);
        Assert.Null(s.FolderNames);
        Assert.Null(s.FolderPaths);
        Assert.Null(s.Selections);
    }

    [Theory]
    [InlineData("review", "the diff", "/review the diff")]
    [InlineData("review", "  ", "/review")]
    [InlineData("review", "", "/review")]
    public void A_picked_command_goes_in_front_of_the_text(string command, string text, string expected)
    {
        Assert.Equal(expected, Build(text, command).Message);
    }

    [Fact]
    public void Dropped_files_are_sent_by_path_and_remembered_by_name()
    {
        var s = Build(files: new[] { new FileAttachment { FilePath = @"C:\a\one.txt" }, new FileAttachment { FilePath = @"C:\b\two.txt" } });

        Assert.Equal(new[] { @"C:\a\one.txt", @"C:\b\two.txt" }, s.FilePaths);
        Assert.Equal(new[] { @"C:\a\one.txt", @"C:\b\two.txt" }, s.DroppedFilePaths);
        Assert.Equal(new[] { "one.txt", "two.txt" }, s.FileNames);
    }

    [Fact]
    public void The_active_editor_file_is_sent_first_but_is_not_a_dropped_file()
    {
        var s = Build(active: @"C:\src\open.cs", files: new[] { new FileAttachment { FilePath = @"C:\a\one.txt" } });

        Assert.Equal(new[] { @"C:\src\open.cs", @"C:\a\one.txt" }, s.FilePaths);
        Assert.Equal(new[] { @"C:\a\one.txt" }, s.DroppedFilePaths);
    }

    [Fact]
    public void The_active_editor_file_alone_is_sent_without_any_dropped_files()
    {
        var s = Build(active: @"C:\src\open.cs");

        Assert.Equal(new[] { @"C:\src\open.cs" }, s.FilePaths);
        Assert.Null(s.DroppedFilePaths);
        Assert.Null(s.FileNames);
    }

    [Fact]
    public void The_active_editor_file_is_left_out_when_switched_off_or_gone()
    {
        Assert.Null(Build(active: @"C:\src\open.cs", activeEnabled: false).FilePaths);
        Assert.Null(Build(active: @"C:\src\missing.cs").FilePaths);
    }

    [Fact]
    public void Images_are_sent_and_stored_as_data_uris()
    {
        var image = new ImageAttachment { Data = new byte[] { 1, 2, 3 }, MimeType = "image/png" };

        var s = Build(images: new[] { image });

        Assert.Same(image, Assert.Single(s.Images!));
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(s.ImageDataUris!));
    }

    [Fact]
    public void Dropped_folders_are_sent_by_path_as_a_one_off_reference()
    {
        var s = Build(folders: new[] { new FolderAttachment { FolderPath = @"C:\a\assets" }, new FolderAttachment { FolderPath = @"C:\b\docs" } });

        Assert.Equal(new[] { @"C:\a\assets", @"C:\b\docs" }, s.FolderPaths);
        Assert.Equal(new[] { "assets", "docs" }, s.FolderNames);
    }

    [Fact]
    public void Selections_are_passed_through()
    {
        var selection = new CodeSelection { FilePath = @"C:\a.cs", StartLine = 1, EndLine = 2, Text = "x" };

        Assert.Same(selection, Assert.Single(Build(selections: new[] { selection }).Selections!));
    }

    [Fact]
    public void The_submission_is_a_copy_so_clearing_the_composer_does_not_affect_it()
    {
        var images = new List<ImageAttachment> { new() { Data = new byte[] { 1 }, MimeType = "image/png" } };
        var s = Build(images: images);

        images.Clear();

        Assert.Single(s.Images!);
    }

    // ---- putting a reverted message back ----

    private static RestoredDraft Restore(PersistedMessage message, string? active = null, params string[] knownCommands) =>
        Composer.Restore(message, knownCommands.Contains, active, Exists, Exists);

    [Fact]
    public void A_reverted_message_returns_its_text()
    {
        var draft = Restore(new PersistedMessage { Content = "fix the bug" });

        Assert.Equal("fix the bug", draft.Text);
        Assert.Null(draft.CommandName);
        Assert.Empty(draft.FilePaths);
        Assert.Empty(draft.FolderPaths);
        Assert.Empty(draft.Selections);
        Assert.Empty(draft.Images);
    }

    [Fact]
    public void A_leading_known_command_becomes_the_chip()
    {
        var draft = Restore(new PersistedMessage { Content = "/review the diff" }, knownCommands: "review");

        Assert.Equal("review", draft.CommandName);
        Assert.Equal("the diff", draft.Text);
    }

    [Fact]
    public void A_command_with_no_text_leaves_the_text_empty()
    {
        var draft = Restore(new PersistedMessage { Content = "/review" }, knownCommands: "review");

        Assert.Equal("review", draft.CommandName);
        Assert.Equal("", draft.Text);
    }

    [Fact]
    public void An_unknown_leading_slash_word_stays_in_the_text()
    {
        var draft = Restore(new PersistedMessage { Content = "/usr/bin is where it lives" }, knownCommands: "review");

        Assert.Null(draft.CommandName);
        Assert.Equal("/usr/bin is where it lives", draft.Text);
    }

    [Fact]
    public void A_command_in_the_middle_of_the_text_is_not_a_chip()
    {
        var draft = Restore(new PersistedMessage { Content = "please /review this" }, knownCommands: "review");

        Assert.Null(draft.CommandName);
    }

    [Fact]
    public void Files_come_back_once_each_if_they_still_exist_and_are_not_the_active_file()
    {
        var message = new PersistedMessage
        {
            Content = "x",
            FilePaths = new List<string> { @"C:\a\one.txt", @"C:\a\one.txt", @"C:\a\missing.txt", @"C:\src\OPEN.cs", @"C:\b\two.txt" }
        };

        var draft = Restore(message, active: @"C:\src\open.cs");

        Assert.Equal(new[] { @"C:\a\one.txt", @"C:\b\two.txt" }, draft.FilePaths);
    }

    [Fact]
    public void Folders_come_back_once_each_if_they_still_exist()
    {
        var message = new PersistedMessage
        {
            Content = "x",
            FolderPaths = new List<string> { @"C:\a\assets", @"C:\a\assets", @"C:\a\missing-dir", @"C:\b\docs" }
        };

        var draft = Restore(message);

        Assert.Equal(new[] { @"C:\a\assets", @"C:\b\docs" }, draft.FolderPaths);
    }

    [Fact]
    public void Snippets_come_back_as_they_were()
    {
        var selection = new CodeSelection { FilePath = @"C:\a.cs", StartLine = 3, EndLine = 5, Text = "int x;" };

        var draft = Restore(new PersistedMessage { Content = "x", Selections = new List<CodeSelection> { selection } });

        Assert.Same(selection, Assert.Single(draft.Selections));
    }

    [Fact]
    public void Images_come_back_and_undecodable_ones_are_dropped()
    {
        var message = new PersistedMessage
        {
            Content = "x",
            ImageDataUris = new List<string> { "data:image/png;base64,AQID", "data:image/png;base64,***", "not a data uri" }
        };

        var image = Assert.Single(Restore(message).Images);

        Assert.Equal(new byte[] { 1, 2, 3 }, image.Data);
        Assert.Equal("image/png", image.MimeType);
    }

    // ---- dropped files ----

    [Fact]
    public void Dropped_files_that_exist_are_attached_once()
    {
        var result = Composer.AcceptDroppedFiles(
            new[] { @"C:\a\one.txt", @"C:\a\one.txt", @"C:\a\missing.txt", @"C:\b\two.txt", @"C:\b\already.txt" },
            new[] { @"C:\b\already.txt" }, null, true, Exists);

        Assert.Equal(new[] { @"C:\a\one.txt", @"C:\b\two.txt" }, result.ToAttach);
        Assert.False(result.DisableActiveContext);
    }

    [Fact]
    public void Dropping_the_active_file_turns_off_its_automatic_attachment()
    {
        var result = Composer.AcceptDroppedFiles(new[] { @"C:\SRC\Open.cs" }, Array.Empty<string>(), @"C:\src\open.cs", true, Exists);

        Assert.Equal(new[] { @"C:\SRC\Open.cs" }, result.ToAttach);
        Assert.True(result.DisableActiveContext);
    }

    [Fact]
    public void Dropping_the_active_file_changes_nothing_when_its_attachment_is_already_off()
    {
        var result = Composer.AcceptDroppedFiles(new[] { @"C:\src\open.cs" }, Array.Empty<string>(), @"C:\src\open.cs", false, Exists);

        Assert.False(result.DisableActiveContext);
    }

    // ---- dropped folders ----

    [Fact]
    public void Dropped_folders_that_exist_are_attached_once()
    {
        var result = Composer.AcceptDroppedFolders(
            new[] { @"C:\a\assets", @"C:\a\assets", @"C:\a\missing-dir", @"C:\b\docs", @"C:\b\already" },
            new[] { @"C:\b\already" }, Exists);

        Assert.Equal(new[] { @"C:\a\assets", @"C:\b\docs" }, result);
    }

    // ---- data uris ----

    [Theory]
    [InlineData("data:image/png;base64,AQID", "image/png", "image.png")]
    [InlineData("DATA:image/jpeg;BASE64,AQID", "image/jpeg", "image.jpeg")]
    [InlineData("data:;base64,AQID", "image/png", "image.png")]
    public void Valid_data_uris_are_decoded(string uri, string mime, string fileName)
    {
        var image = Composer.TryParseImageDataUri(uri);

        Assert.NotNull(image);
        Assert.Equal(new byte[] { 1, 2, 3 }, image!.Data);
        Assert.Equal(mime, image.MimeType);
        Assert.Equal(fileName, image.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("image/png;base64,AQID")]
    [InlineData("data:image/png,AQID")]
    [InlineData("data:image/png;base64")]
    [InlineData("data:image/png;base64,not base64!")]
    public void Anything_else_is_not_an_image(string uri)
    {
        Assert.Null(Composer.TryParseImageDataUri(uri));
    }

    // ---- the round trip through a real chat ----

    [Fact]
    public async Task Reverting_puts_the_whole_message_back_in_the_composer_as_it_was_sent()
    {
        using var h = await ChatHarness.CreateAsync();
        h.WriteFile("notes.txt", "notes");
        h.WriteFile("open.cs", "class A {}");
        h.WriteFile("assets/readme.txt", "assets");
        var dropped = h.PathOf("notes.txt");
        var droppedFolder = h.PathOf("assets");
        var image = new ImageAttachment { Data = new byte[] { 9, 8, 7 }, MimeType = "image/png" };
        var selection = new CodeSelection { FilePath = h.PathOf("open.cs"), StartLine = 1, EndLine = 1, Text = "class A {}" };

        var submission = Composer.BuildSubmission(
            "the diff", "review", new[] { image }, new[] { new FileAttachment { FilePath = dropped } },
            new[] { new FolderAttachment { FolderPath = droppedFolder } },
            new[] { selection }, h.PathOf("open.cs"), activeContextEnabled: true);
        await h.SendAsync(submission);
        await h.SendAsync("something later");

        var reverted = await h.RevertToUserMessageAsync(0);
        var draft = Composer.Restore(reverted!, name => name == "review", activeContextFile: h.PathOf("open.cs"));

        Assert.Equal("review", draft.CommandName);
        Assert.Equal("the diff", draft.Text);
        Assert.Equal(new[] { dropped }, draft.FilePaths);
        Assert.Equal(new[] { droppedFolder }, draft.FolderPaths);
        Assert.Equal(new byte[] { 9, 8, 7 }, Assert.Single(draft.Images).Data);
        var restoredSelection = Assert.Single(draft.Selections);
        Assert.Equal(selection.Text, restoredSelection.Text);
        Assert.Equal(selection.StartLine, restoredSelection.StartLine);
    }
}
