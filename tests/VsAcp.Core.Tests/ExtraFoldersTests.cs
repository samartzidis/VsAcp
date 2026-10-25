using System.Text.Json.Nodes;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>The extra folders shared with the agent: how they combine, what may be added, how choices persist and reach the agent.</summary>
public class ExtraFoldersTests
{
    private static string[] Paths(IEnumerable<ExtraFolder> folders) => folders.Select(f => f.Path).ToArray();

    private static string Folder(TempDirectory root, string name)
    {
        var path = Path.Combine(root.Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---- Merge ----

    [Fact]
    public void Auto_folders_come_first_then_the_ones_the_user_added()
    {
        var result = ExtraFolders.Merge(@"C:\src\app", new[] { @"C:\libs\a" }, new[] { @"C:\more\b" }, [], _ => true);

        Assert.Equal(new[] { @"C:\libs\a", @"C:\more\b" }, Paths(result));
        Assert.Equal(new[] { ExtraFolderSource.Auto, ExtraFolderSource.Manual }, result.Select(f => f.Source).ToArray());
    }

    [Fact]
    public void An_auto_folder_the_user_turned_off_is_not_shared_but_a_manual_one_is_unaffected()
    {
        var result = ExtraFolders.Merge(@"C:\src\app", new[] { @"C:\libs\a", @"C:\libs\b" }, new[] { @"C:\more\c" },
            new[] { @"c:\LIBS\A\" }, _ => true);

        Assert.Equal(new[] { @"C:\libs\b", @"C:\more\c" }, Paths(result));
    }

    [Fact]
    public void A_folder_that_is_both_auto_and_added_is_listed_once_as_auto()
    {
        var result = ExtraFolders.Merge(@"C:\src\app", new[] { @"C:\libs\a" }, new[] { @"c:\libs\a\" }, [], _ => true);

        var only = Assert.Single(result);
        Assert.Equal(ExtraFolderSource.Auto, only.Source);
    }

    [Fact]
    public void Added_folders_inside_the_solution_or_that_no_longer_exist_are_dropped()
    {
        var result = ExtraFolders.Merge(@"C:\src\app", [], new[] { @"C:\src\app\Web", @"C:\gone", @"C:\there" },
            [], dir => !dir.EndsWith("gone"));

        Assert.Equal(new[] { @"C:\there" }, Paths(result));
    }

    // ---- Validate ----

    [Fact]
    public void A_real_folder_beside_the_solution_is_accepted()
    {
        using var root = new TempDirectory();
        var solution = Folder(root, "app");
        var other = Folder(root, "lib");

        Assert.Null(ExtraFolders.Validate(other, solution));
    }

    [Fact]
    public void Blank_relative_and_missing_paths_are_refused_with_a_reason()
    {
        using var root = new TempDirectory();
        var solution = Folder(root, "app");

        Assert.NotNull(ExtraFolders.Validate(null, solution));
        Assert.NotNull(ExtraFolders.Validate("   ", solution));
        Assert.Contains("absolute", ExtraFolders.Validate(@"lib\x", solution));
        Assert.Contains("does not exist", ExtraFolders.Validate(Path.Combine(root.Path, "nope"), solution));
        Assert.NotNull(ExtraFolders.Validate("bad|path?", solution));
    }

    [Fact]
    public void Network_folders_are_refused()
    {
        using var root = new TempDirectory();
        var solution = Folder(root, "app");
        var other = Folder(root, "lib");

        Assert.Contains("Network", ExtraFolders.Validate(other, solution, isNetworkPath: _ => true));
    }

    [Fact]
    public void A_whole_drive_and_the_user_profile_are_too_broad()
    {
        using var root = new TempDirectory();
        var solution = Folder(root, "app");
        var driveRoot = Path.GetPathRoot(root.Path)!;

        Assert.Contains("drive", ExtraFolders.Validate(driveRoot, solution));
        Assert.Contains("profile", ExtraFolders.Validate(root.Path, solution, userProfile: root.Path + @"\"));
    }

    [Fact]
    public void A_folder_already_inside_the_solution_is_refused_but_a_lookalike_sibling_is_fine()
    {
        using var root = new TempDirectory();
        var solution = Folder(root, "app");
        var inside = Folder(root, @"app\Web");
        var lookalike = Folder(root, "app2");

        Assert.Contains("already", ExtraFolders.Validate(inside, solution));
        Assert.Null(ExtraFolders.Validate(lookalike, solution));
    }

    // ---- store ----

    [Fact]
    public void The_store_round_trips_and_overwrites_without_leaving_a_temp_file()
    {
        using var root = new TempDirectory();
        var store = new ExtraFoldersStore(Path.Combine(root.Path, ".vs", "vsacp", "folders.json"));

        Assert.True(store.Save(new ExtraFoldersData { Added = [@"C:\a"], Removed = [@"C:\r"] }));
        Assert.True(store.Save(new ExtraFoldersData { Added = [@"C:\a", @"C:\b"], Removed = [] }));

        var loaded = store.Load();
        Assert.Equal(new[] { @"C:\a", @"C:\b" }, loaded.Added);
        Assert.Empty(loaded.Removed);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void A_missing_or_corrupt_file_means_no_choices()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "folders.json");
        var store = new ExtraFoldersStore(path);

        Assert.Empty(store.Load().Added);

        File.WriteAllText(path, "{ not json");
        var loaded = store.Load();
        Assert.Empty(loaded.Added);
        Assert.Empty(loaded.Removed);

        File.WriteAllText(path, "{\"added\":[\"\",\"C:\\\\x\"],\"removed\":null}");
        Assert.Equal(new[] { @"C:\x" }, store.Load().Added);
    }

    // ---- controller ----

    private sealed class Rig : IDisposable
    {
        private readonly TempDirectory _root = new();
        public string Solution { get; }
        public string StorePath => Path.Combine(Solution, ".vs", "vsacp", "folders.json");
        public List<string[]> Applied { get; } = [];
        public int ChangedCount { get; private set; }
        public ExtraFoldersController Controller { get; private set; }

        public Rig(params string[] autoNames)
        {
            Solution = Folder(_root, "app");
            Auto = autoNames.Select(n => Folder(_root, n)).ToArray();
            Controller = Create();
        }

        public string[] Auto { get; }
        public string NewFolder(string name) => Folder(_root, name);
        public ExtraFoldersController Create(bool withStore = true)
        {
            var controller = new ExtraFoldersController(Solution, Auto,
                withStore ? new ExtraFoldersStore(StorePath) : null, dirs => Applied.Add(dirs));
            controller.Changed += () => ChangedCount++;
            return Controller = controller;
        }

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public void The_controller_applies_the_auto_folders_straight_away()
    {
        using var rig = new Rig("liba", "libb");

        Assert.Equal(rig.Auto, rig.Applied.Last());
        Assert.Equal(rig.Auto, Paths(rig.Controller.Folders));
    }

    [Fact]
    public void Adding_a_folder_applies_saves_and_notifies()
    {
        using var rig = new Rig("liba");
        var extra = rig.NewFolder("docs");

        Assert.Null(rig.Controller.Add(extra));

        Assert.Equal(new[] { rig.Auto[0], extra }, rig.Applied.Last());
        Assert.Equal(1, rig.ChangedCount);
        Assert.Equal(ExtraFolderSource.Manual, rig.Controller.Folders.Last().Source);

        // a new tab for the same solution starts with the same list
        var second = rig.Create();
        Assert.Equal(new[] { rig.Auto[0], extra }, Paths(second.Folders));
    }

    [Fact]
    public void Adding_an_unusable_folder_returns_the_reason_and_changes_nothing()
    {
        using var rig = new Rig();
        var before = rig.Applied.Count;

        var error = rig.Controller.Add(Path.Combine(rig.Solution, "Web"));

        Assert.NotNull(error);
        Assert.Equal(0, rig.ChangedCount);
        Assert.Equal(before, rig.Applied.Count);
        Assert.False(File.Exists(rig.StorePath));
    }

    [Fact]
    public void Adding_a_folder_that_is_already_shared_is_a_no_op()
    {
        using var rig = new Rig("liba");
        var extra = rig.NewFolder("docs");
        rig.Controller.Add(extra);
        var changes = rig.ChangedCount;

        Assert.Null(rig.Controller.Add(extra + @"\"));
        Assert.Null(rig.Controller.Add(rig.Auto[0]));

        Assert.Equal(changes, rig.ChangedCount);
        Assert.Equal(2, rig.Controller.Folders.Count);
    }

    [Fact]
    public void Removing_a_manual_folder_forgets_it()
    {
        using var rig = new Rig();
        var extra = rig.NewFolder("docs");
        rig.Controller.Add(extra);

        Assert.Null(rig.Controller.Remove(extra));

        Assert.Empty(rig.Controller.Folders);
        Assert.Empty(rig.Create().Folders);
        Assert.Empty(new ExtraFoldersStore(rig.StorePath).Load().Removed);
    }

    [Fact]
    public void Removing_an_auto_folder_keeps_it_off_until_it_is_added_again()
    {
        using var rig = new Rig("liba", "libb");

        Assert.Null(rig.Controller.Remove(rig.Auto[0]));
        Assert.Equal(new[] { rig.Auto[1] }, Paths(rig.Controller.Folders));
        Assert.Equal(new[] { rig.Auto[1] }, Paths(rig.Create().Folders));

        Assert.Null(rig.Controller.Add(rig.Auto[0]));
        Assert.Equal(rig.Auto, Paths(rig.Controller.Folders));
        Assert.Empty(new ExtraFoldersStore(rig.StorePath).Load().Removed);
    }

    [Fact]
    public void Without_a_store_the_list_is_read_only()
    {
        using var rig = new Rig("liba");
        var controller = rig.Create(withStore: false);

        Assert.False(controller.CanEdit);
        Assert.Equal(rig.Auto, Paths(controller.Folders));
        Assert.NotNull(controller.Add(rig.NewFolder("docs")));
        Assert.NotNull(controller.Remove(rig.Auto[0]));
        Assert.Equal(rig.Auto, Paths(controller.Folders));
    }

    [Fact]
    public void A_save_failure_is_reported_and_the_list_is_unchanged()
    {
        using var root = new TempDirectory();
        var solution = Folder(root, "app");
        var extra = Folder(root, "docs");
        var blocker = Path.Combine(root.Path, "blocker");
        File.WriteAllText(blocker, "a file where a folder is needed");
        var controller = new ExtraFoldersController(solution, [], new ExtraFoldersStore(Path.Combine(blocker, "folders.json")), _ => { });

        var error = controller.Add(extra);

        Assert.Contains("saved", error);
        Assert.Empty(controller.Folders);
    }

    // ---- one list shared by all tabs ----

    [Fact]
    public void Every_subscriber_gets_each_change_until_it_unsubscribes()
    {
        using var rig = new Rig();
        var tabB = new List<string[]>();
        var tabC = new List<string[]>();
        using var subB = rig.Controller.Subscribe(dirs => tabB.Add(dirs));
        var subC = rig.Controller.Subscribe(dirs => tabC.Add(dirs));
        var docs = rig.NewFolder("docs");
        var libs = rig.NewFolder("libs");

        Assert.Null(rig.Controller.Add(docs));
        subC.Dispose();
        Assert.Null(rig.Controller.Add(libs));

        Assert.Equal(new[] { docs, libs }, tabB.Last());
        Assert.Equal(new[] { docs }, tabC.Last());
        Assert.Equal(new[] { docs, libs }, rig.Applied.Last());
    }

    [Fact]
    public void A_second_controller_on_the_same_store_does_not_overwrite_the_first_ones_choices()
    {
        using var rig = new Rig();
        var other = new ExtraFoldersController(rig.Solution, rig.Auto, new ExtraFoldersStore(rig.StorePath));
        var docs = rig.NewFolder("docs");
        var libs = rig.NewFolder("libs");

        Assert.Null(rig.Controller.Add(docs));
        Assert.Null(other.Add(libs));

        Assert.Equal(new[] { docs, libs }, Paths(other.Folders));
        Assert.Equal(new[] { docs, libs }, Paths(new ExtraFoldersStore(rig.StorePath).Load().Added.Select(a => new ExtraFolder(a, ExtraFolderSource.Manual)).ToArray()));
        // the first one catches up when it next changes something
        Assert.Null(rig.Controller.Remove(docs));
        Assert.Equal(new[] { libs }, Paths(rig.Controller.Folders));
    }

    [Fact]
    public void New_auto_folders_reach_the_subscribers_but_an_unchanged_list_stays_quiet()
    {
        using var rig = new Rig("liba");
        var liba = rig.Auto[0];
        var libb = rig.NewFolder("libb");

        rig.Controller.SetAutoDirectories([liba]);
        Assert.Equal(0, rig.ChangedCount);

        rig.Controller.SetAutoDirectories([liba, libb]);
        Assert.Equal(1, rig.ChangedCount);
        Assert.Equal(new[] { liba, libb }, rig.Applied.Last());
    }

    // ---- reaching the agent ----

    private static object Caps(bool additionalDirectories) => new
    {
        agentCapabilities = additionalDirectories
            ? new { sessionCapabilities = new { additionalDirectories = new { } } }
            : (object)new { sessionCapabilities = new { } }
    };

    [Fact]
    public async Task A_folder_added_before_the_session_starts_is_sent_on_session_new()
    {
        using var other = new TempDirectory();
        using var h = await ChatHarness.CreateAsync(Caps(true));
        var controller = new ExtraFoldersController(h.Workspace, [], new ExtraFoldersStore(Path.Combine(h.Workspace, ".vs", "vsacp", "folders.json")),
            dirs => h.Options.AdditionalDirectories = dirs);

        Assert.Null(controller.Add(other.Path));
        await h.SendAsync("hi");

        var sent = (JsonArray)h.Received("session/new").Single()["additionalDirectories"]!;
        Assert.Equal(new[] { other.Path }, sent.Select(d => (string?)d).ToArray());
    }

    [Fact]
    public async Task Turning_off_the_only_folder_omits_the_field()
    {
        using var other = new TempDirectory();
        using var h = await ChatHarness.CreateAsync(Caps(true));
        var controller = new ExtraFoldersController(h.Workspace, [other.Path], new ExtraFoldersStore(Path.Combine(h.Workspace, ".vs", "vsacp", "folders.json")),
            dirs => h.Options.AdditionalDirectories = dirs);

        Assert.Null(controller.Remove(other.Path));
        await h.SendAsync("hi");

        Assert.False(h.Received("session/new").Single().ContainsKey("additionalDirectories"));
    }

    [Fact]
    public async Task The_chip_can_tell_whether_the_agent_uses_extra_folders()
    {
        using var yes = await ChatHarness.CreateAsync(Caps(true));
        using var no = await ChatHarness.CreateAsync(Caps(false));

        Assert.Null(yes.Service.SupportsAdditionalDirectories);

        await yes.SendAsync("hi");
        await no.SendAsync("hi");

        Assert.True(yes.Service.SupportsAdditionalDirectories);
        Assert.False(no.Service.SupportsAdditionalDirectories);
    }

    // ---- dropped items ----

    [Fact]
    public void Dropped_items_keep_the_folders_once_and_count_the_rest()
    {
        using var root = new TempDirectory();
        var docs = Folder(root, "docs");
        var libs = Folder(root, "libs");
        var file = Path.Combine(root.Path, "a.txt");
        File.WriteAllText(file, "x");

        var (folders, skipped) = ExtraFolders.FoldersAmong(
            [docs, file, libs, docs + Path.DirectorySeparatorChar, docs.ToUpperInvariant(), Path.Combine(root.Path, "gone"), ""]);

        Assert.Equal(new[] { docs, libs }, folders);
        Assert.Equal(3, skipped);
    }

    [Fact]
    public void Nothing_dropped_means_no_folders()
    {
        var (folders, skipped) = ExtraFolders.FoldersAmong([]);

        Assert.Empty(folders);
        Assert.Equal(0, skipped);
    }

    // ---- what the running session has ----

    [Fact]
    public async Task The_active_list_is_what_was_sent_and_ignores_later_changes()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        using var h = await ChatHarness.CreateAsync(Caps(true));
        var controller = new ExtraFoldersController(h.Workspace, [first.Path], new ExtraFoldersStore(Path.Combine(h.Workspace, ".vs", "vsacp", "folders.json")),
            dirs => h.Options.AdditionalDirectories = dirs);
        var raised = 0;
        h.Service.ActiveAdditionalDirectoriesChanged += () => raised++;

        Assert.Null(h.Service.ActiveAdditionalDirectories);

        await h.SendAsync("hi");
        Assert.Equal(new[] { first.Path }, h.Service.ActiveAdditionalDirectories);
        Assert.Equal(1, raised);

        Assert.Null(controller.Add(second.Path));
        Assert.Equal(new[] { first.Path }, h.Service.ActiveAdditionalDirectories);
    }

    [Fact]
    public async Task The_active_list_is_empty_when_the_agent_does_not_take_folders()
    {
        using var other = new TempDirectory();
        using var h = await ChatHarness.CreateAsync(Caps(false));
        h.Options.AdditionalDirectories = [other.Path];

        await h.SendAsync("hi");

        Assert.NotNull(h.Service.ActiveAdditionalDirectories);
        Assert.Empty(h.Service.ActiveAdditionalDirectories!);
    }
}
