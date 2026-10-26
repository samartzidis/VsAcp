using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;

namespace VsAcp.ViewModels;

public partial class SessionInfo : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The persisted session ID (GUID) from the store. Null for sessions not yet saved.
    /// </summary>
    public string? PersistedId { get; set; }

    /// <summary>
    /// Agent executable saved at session creation time. Null for sessions created before
    /// this field was introduced (treated as "use current global settings").
    /// </summary>
    public string? AgentCommand { get; set; }

    /// <summary>
    /// Agent arguments saved at session creation time (space-separated).
    /// </summary>
    public string? AgentArgs { get; set; }

    [ObservableProperty]
    private string _name = "New Session";

    [ObservableProperty]
    private DateTime _lastActivity = DateTime.Now;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>The tab title is being edited in place.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>
    /// Cumulative USD cost for this session. Null until the first message is sent.
    /// </summary>
    [ObservableProperty]
    private decimal? _sessionCost;

    /// <summary>
    /// Formatted cost string shown in the session list (e.g. "$0.0042").
    /// Empty string when cost is not yet available.
    /// </summary>
    public string SessionCostDisplay => SessionCost.HasValue
        ? $"${SessionCost.Value:F2}"
        : string.Empty;

    partial void OnSessionCostChanged(decimal? value)
        => OnPropertyChanged(nameof(SessionCostDisplay));
}

public partial class SessionListViewModel : ObservableObject
{
    private ISessionStore? _sessionStore;

    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    public event Action<SessionInfo>? SessionOpenRequested;
    public event Action<SessionInfo>? SessionRemoved;

    /// <summary>
    /// Initializes the view model with a session store for persistence.
    /// </summary>
    public void Initialize(ISessionStore sessionStore)
    {
        _sessionStore = sessionStore;
    }

    public void Clear() => Sessions.Clear();

    /// <summary>
    /// Loads previously saved sessions from the store into the Sessions collection.
    /// </summary>
    public async Task LoadSessionsAsync()
    {
        if (_sessionStore is null) return;

        var entries = await _sessionStore.GetSessionIndexAsync();
        Sessions.Clear();

        foreach (var entry in entries.OrderByDescending(e => e.LastActivityUtc))
        {
            var info = new SessionInfo
            {
                PersistedId = entry.Id,
                AgentCommand = entry.AgentCommand,
                AgentArgs = entry.AgentArgs,
                Name = entry.Title,
                LastActivity = entry.LastActivityUtc.ToLocalTime(),
                IsActive = false
            };

            Sessions.Add(info);
        }
    }

    [RelayCommand]
    public async Task NewSessionAsync()
    {
        var session = new SessionInfo
        {
            Name = $"Chat {Sessions.Count + 1}",
            IsActive = true
        };

        if (_sessionStore is not null)
        {
            try
            {
                var entry = await _sessionStore.CreateSessionAsync(session.Name);
                session.PersistedId = entry.Id;
            }
            catch { /* best effort — session works in-memory even if persistence fails */ }
        }

        Sessions.Add(session);
        SelectedSession = session;
        SessionOpenRequested?.Invoke(session);
    }

    /// <summary>Adds a chat another one was just forked into (already in the store) and opens its tab.</summary>
    public SessionInfo OpenForkedSession(SessionEntry entry)
    {
        var session = new SessionInfo
        {
            PersistedId = entry.Id,
            AgentCommand = entry.AgentCommand,
            AgentArgs = entry.AgentArgs,
            Name = entry.Title,
            IsActive = true
        };

        Sessions.Add(session);
        SelectedSession = session;
        SessionOpenRequested?.Invoke(session);
        return session;
    }

    [RelayCommand]
    private void OpenSession(SessionInfo? session)
    {
        if (session is null) return;
        SelectedSession = session;
        session.LastActivity = DateTime.Now;
        SessionOpenRequested?.Invoke(session);
    }

    /// <summary>
    /// Renames a session that has no open tab (an open tab renames through its own view-model, which owns the
    /// persistence queue) and locks the title so the agent does not replace it later.
    /// </summary>
    public async Task RenameAsync(SessionInfo session, string title)
    {
        session.Name = title;
        if (session.PersistedId is null || _sessionStore is null) return;

        try
        {
            var entry = (await _sessionStore.GetSessionIndexAsync()).FirstOrDefault(e => e.Id == session.PersistedId);
            if (entry is null) return;

            entry.Title = title;
            entry.TitleLocked = true;
            await _sessionStore.UpdateSessionAsync(entry);
        }
        catch { /* best effort */ }
    }

    [RelayCommand]
    private async Task RemoveSessionAsync(SessionInfo? session)
    {
        if (session is null) return;
        session.IsActive = false;
        Sessions.Remove(session);
        SessionRemoved?.Invoke(session);

        if (session.PersistedId is not null && _sessionStore is not null)
        {
            try
            {
                await _sessionStore.DeleteSessionAsync(session.PersistedId);
            }
            catch { /* best effort */ }
        }

        if (SelectedSession == session)
            SelectedSession = Sessions.FirstOrDefault();
    }
}
