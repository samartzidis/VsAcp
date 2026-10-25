namespace VsAcp.Core.Services;

/// <summary>
/// The extra folders of one solution: the auto-detected ones plus the user's changes. One instance is shared by
/// every chat tab of the solution, so the list is the same in all of them; each tab subscribes to put the merged
/// list into its own agent options. A change only reaches the agent when a session is created or loaded (ACP has
/// no "update a running session"), so the new list is applied to the options and used from then on. The user's
/// choices are re-read from the store before each change, so another instance that saved in between is not
/// overwritten. Not thread-safe: call it from the thread that owns the tabs (the UI thread in VS).
/// </summary>
public sealed class ExtraFoldersController
{
    private readonly string _solutionDirectory;
    private string[] _auto;
    private readonly ExtraFoldersStore? _store;
    private readonly Func<string, bool>? _directoryExists;
    private readonly List<Action<string[]>> _subscribers = new();
    private ExtraFoldersData _data;

    /// <param name="solutionDirectory">The working directory of the tabs.</param>
    /// <param name="autoDirectories">Project folders found in the solution (already outside the solution folder).</param>
    /// <param name="store">Where the user's choices persist; null when there is no local project (editing is then off).</param>
    /// <param name="apply">An initial subscriber (see <see cref="Subscribe"/>); null for none.</param>
    public ExtraFoldersController(
        string solutionDirectory,
        IEnumerable<string?> autoDirectories,
        ExtraFoldersStore? store,
        Action<string[]>? apply = null,
        Func<string, bool>? directoryExists = null)
    {
        _solutionDirectory = solutionDirectory;
        _auto = Clean(autoDirectories);
        _store = store;
        _directoryExists = directoryExists;
        _data = store?.Load() ?? new ExtraFoldersData();
        Folders = Compute();
        if (apply is not null) Subscribe(apply);
    }

    /// <summary>The folders currently shared, auto-detected first.</summary>
    public IReadOnlyList<ExtraFolder> Folders { get; private set; }

    /// <summary>False when there is nowhere to save the user's choices (no local project folder).</summary>
    public bool CanEdit => _store is not null;

    public event Action? Changed;

    /// <summary>
    /// Calls <paramref name="apply"/> with the full list to send to the agent now and after every change, until
    /// the returned handle is disposed (do that when the tab goes away).
    /// </summary>
    public IDisposable Subscribe(Action<string[]> apply)
    {
        _subscribers.Add(apply);
        apply(Paths());
        return new Subscription(this, apply);
    }

    /// <summary>
    /// Replaces the auto-detected folders (the solution's projects can change while it is open). Nothing happens
    /// when the merged list stays the same; otherwise subscribers and <see cref="Changed"/> are notified.
    /// </summary>
    public void SetAutoDirectories(IEnumerable<string?> autoDirectories)
    {
        _auto = Clean(autoDirectories);
        Refresh();
    }

    /// <summary>Adds a folder. Returns null on success (or when it is already shared), else the reason it was refused.</summary>
    public string? Add(string? path)
    {
        if (!CanEdit) return "There is no local project folder to save the choice in.";

        var error = ExtraFolders.Validate(path, _solutionDirectory, _directoryExists, isNetworkPath: null);
        if (error is not null) return error;

        _data = _store!.Load();
        Refresh(); // another instance may have saved since: bring everyone up to date first

        var key = ExtraFolders.Key(path)!;
        var removed = _data.Removed.ToList();
        var added = _data.Added.ToList();

        var wasRemoved = removed.RemoveAll(r => KeyEquals(r, key)) > 0;
        var alreadyShared = Folders.Any(f => KeyEquals(f.Path, key));
        if (alreadyShared && !wasRemoved) return null;

        if (!added.Any(a => KeyEquals(a, key)) && !_auto.Any(a => KeyEquals(a, key)))
            added.Add(key);

        return Commit(new ExtraFoldersData { Added = added.ToArray(), Removed = removed.ToArray() });
    }

    /// <summary>Stops sharing a folder: a user-added one is forgotten, an auto-detected one is remembered as turned off.</summary>
    public string? Remove(string? path)
    {
        if (!CanEdit) return "There is no local project folder to save the choice in.";

        var key = ExtraFolders.Key(path);
        if (key is null) return null;

        _data = _store!.Load();
        Refresh();

        var added = _data.Added.Where(a => !KeyEquals(a, key)).ToList();
        var removed = _data.Removed.ToList();
        if (_auto.Any(a => KeyEquals(a, key)) && !removed.Any(r => KeyEquals(r, key)))
            removed.Add(key);

        return Commit(new ExtraFoldersData { Added = added.ToArray(), Removed = removed.ToArray() });
    }

    private string? Commit(ExtraFoldersData next)
    {
        if (!_store!.Save(next)) return "The choice could not be saved.";
        _data = next;
        Folders = Compute();
        Serilog.Log.Information("[Folders] {Count} extra folder(s) now shared: {Dirs}", Folders.Count, string.Join(", ", Paths()));
        Notify();
        return null;
    }

    private void Refresh()
    {
        var next = Compute();
        if (next.Select(f => f.Path).SequenceEqual(Folders.Select(f => f.Path), StringComparer.OrdinalIgnoreCase)
            && next.Select(f => f.Source).SequenceEqual(Folders.Select(f => f.Source)))
            return;
        Folders = next;
        Notify();
    }

    private void Notify()
    {
        var paths = Paths();
        foreach (var apply in _subscribers.ToArray()) apply(paths);
        Changed?.Invoke();
    }

    private IReadOnlyList<ExtraFolder> Compute() =>
        ExtraFolders.Merge(_solutionDirectory, _auto, _data.Added, _data.Removed, _directoryExists);

    private string[] Paths() => Folders.Select(f => f.Path).ToArray();

    private static string[] Clean(IEnumerable<string?> directories) =>
        directories.Where(d => d is not null).Select(d => d!).ToArray();

    private static bool KeyEquals(string? a, string b) =>
        string.Equals(ExtraFolders.Key(a), b, StringComparison.OrdinalIgnoreCase);

    private sealed class Subscription : IDisposable
    {
        private ExtraFoldersController? _owner;
        private readonly Action<string[]> _apply;

        public Subscription(ExtraFoldersController owner, Action<string[]> apply)
        {
            _owner = owner;
            _apply = apply;
        }

        public void Dispose()
        {
            _owner?._subscribers.Remove(_apply);
            _owner = null;
        }
    }
}
