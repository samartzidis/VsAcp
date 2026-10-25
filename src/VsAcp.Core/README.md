# VsAcp.Core

The integration layer between the ACP agent process and the Visual Studio extension. It manages the agent process lifecycle, implements the ACP protocol client callbacks, coordinates user interactions (file diffs, permissions, elicitation, terminal output), owns the chat session/turn/persistence model, snapshots and reverts the workspace, and exposes a set of clean interfaces for the extension to consume.

## Position in the stack

```
VsAcp
  │  implements IOutputListener, IPermissionBroker/IElicitationBroker/IDiffBroker UI, ITerminalOutputSink
  │  drives ChatSessionController (owns ChatSession) through a view-model
  ▼
VsAcp.Core          ← this library
  │  ChatSession / ChatSessionController / ChatTurnRunner — session state, turns, persistence, revert
  │  AcpSessionService implements IChatService + IAcpSessionLifecycle
  │  AcpClientHandler implements IAcpClient (ACP callbacks), AgentProcessHost, ShadowGitCheckpointService
  ▼
AcpProtocol
  │  JSON-RPC 2.0 over stdin/stdout
  ▼
Agent process
```

## Namespaces

### `VsAcp.Core.Abstractions`

Interfaces and data types the VS extension depends on:

| Type | Purpose |
|---|---|
| `IChatService` | Send prompts to the agent and stream text back; session title, cancel, usage/cost, config options, modes, available commands, and priming a "prior conversation context" prefix after a revert |
| `IAcpSessionLifecycle` | ACP-specific session continuation: serialize/restore the agent session id, eager `LoadSessionAsync`, `SessionRestoreFailed`, active extra-directories, `DeleteAgentSessionAsync` |
| `IDiffBroker` | Fire-and-forget notification (`DiffCompleted`) that a file was created/modified/deleted, for the extension's inline diff overlay — raised *after* the write, not an approval gate |
| `IPermissionBroker` | Receive permission requests from the agent; prompt the user and return the decision; per-tab auto-approve |
| `IElicitationBroker` | Receive `elicitation/create` requests (form or URL) from the agent; prompt the user and return accept/decline/cancel; agent-side `Dismiss` |
| `ISessionStore` | Persist and retrieve sessions and messages per workspace |
| `ICheckpointService` | Snapshot/restore/preview the workspace via the shadow-git checkpoint repo (backs "Revert to here") |
| `IOutputListener` | Receive real-time tool-call output items (`OutputItem`) for display in the extension UI |
| `ITerminalOutputSink` | Optional — stream terminal output to a VS output pane |
| `FileAttachment`, `ImageAttachment`, `CodeSelection` | Attachment/paste data passed with user messages |
| `ChatItemType` | Kind of a transcript entry: User, Assistant, ToolStep, Thinking, Notice, Compaction |

### `VsAcp.Core.Acp`

Protocol integration and process management:

| Type | Purpose |
|---|---|
| `AcpSessionService` | Implements `IChatService` + `IAcpSessionLifecycle`. Top-level facade: starts the agent, opens the ACP connection, sends prompts, exposes config/mode/command/usage APIs |
| `AcpClientHandler` | Implements `IAcpClient`. Routes incoming ACP callbacks to the handler classes in `Acp.Handlers` |
| `AgentProcessHost` | Launches the agent executable (`.ps1`, `.cmd`, or direct). Attaches a Win32 Job Object so child processes are killed when the host exits |
| `DiffBroker` | Plain `IDiffBroker`: just raises `DiffCompleted` |
| `PermissionBroker` | `TaskCompletionSource`-based `IPermissionBroker`; owns the per-tab auto-approve flag (starts off, toggled via `SetAutoApprove`, which also grants any prompt already waiting) |
| `ElicitationBroker` | Same `TaskCompletionSource` pattern as `PermissionBroker`, for `IElicitationBroker` |
| `ProcessJobObject` | P/Invoke wrapper around Win32 Job Objects (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) |
| `AgentConfigurationException` | Thrown when the agent process cannot be started (missing/invalid `AgentCommand`) |
| `AcpErrorText` | User-facing text for a failed agent request; prefers the agent's own `data.details` over the generic exception message |

### `VsAcp.Core.Acp.Handlers`

| Type | Purpose |
|---|---|
| `FileSystemHandler` | Handles `ReadTextFileRequest`/`WriteTextFileRequest`. Writes the file immediately (write-then-review), then fires `IDiffBroker.FireDiffCompleted` for existing files so the extension can show an inline diff |
| `PermissionHandler` | Translates ACP permission requests to `IPermissionBroker` calls; auto-approve picks allow-once over allow-always and never a reject option |
| `ElicitationHandler` | Translates `elicitation/create`'s JSON-Schema form (or URL) into an `AcpElicitationRequest` for the UI via `IElicitationBroker`, and maps the answer back to `accept`/`decline`/`cancel` |
| `SessionUpdateAdapter` | Processes `SessionNotification` messages from the agent: streams text chunks, converts tool calls to `OutputItem` events, tracks mode/config/command/usage updates, classifies notices and compaction |
| `TerminalHandler` | Manages agent-created terminal processes; buffers output, forwards to `ITerminalOutputSink`, handles kill/release/wait-for-exit |
| `ToolCallAccumulator` | Per-tool-call display title and merged body content across streaming `tool_call`/`tool_call_update` notifications |
| `ToolCallDiffNotifier` | Fires inline-diff reports: tool-call-reported edits/deletes (checkpoint as "before", disk as "after"), plus a workspace scan against the turn's checkpoint for changes no tool call announced (shell commands, build output, user edits) |
| `ToolCallChangedFiles` | Tracks which files each tool call of a turn changed, so the completed step can carry them (`OutputItem.ChangedFiles`) — what "Revert to here" restores by default |
| `ClaudeCodeMeta` | Defensively reads claude-agent-acp's private `_meta.claudeCode...bashEditDiff` data (files a shell command changed), not part of the ACP spec |

### `VsAcp.Core.Configuration`

| Type | Purpose |
|---|---|
| `VsAcpOptions` | Agent executable path/args/env, working directory, `HasProjectDirectory`, `AdditionalDirectories` |
| `VsAcpPaths` | Resolves the project-local `.vs\vsacp\` paths for sessions, checkpoints, and extra-folder choices; `IsNetworkPath` |

### `VsAcp.Core.Models`

| Type | Purpose |
|---|---|
| `ChatSession` | In-memory source of truth for one tab's messages: owns id/ordinal assignment, step→message mapping, and the change events a projector/persister subscribe to |
| `ChatMessage` | A single message owned by `ChatSession`; mutable fields are `internal set` so only `ChatSession` can mutate them |
| `TurnSubmission` | Everything the user sent in one turn (message, images, file/dropped-file paths, code selections) |
| `PersistedMessage` | On-disk shape of a `ChatMessage` (JSONL record), including `ChangedFiles`/`TracksChangedFiles` for revert |
| `SessionEntry` | Session index entry: title (+ lock), ordinal, timestamps, ACP session id/agent command |
| `WorkspaceInfo` | Workspace folder path + creation time, written once per workspace |

### `VsAcp.Core.Services`

| Type | Purpose |
|---|---|
| `ChatSessionController` | The non-UI half of one chat tab: routes agent output into `ChatSession` via a `SerialDispatcher`, runs turns through `ChatTurnRunner`, persists/restores/flushes, and drives revert via `RevertCoordinator`. Shared by the view-model and the headless tests |
| `ChatTurnRunner` | Executes one turn: creates a workspace checkpoint, adds the user message, streams the agent reply, turns cancellation/errors into transient messages |
| `SerialDispatcher` | Applies posted actions on the host thread/lock in enqueue order, one drain at a time, so `ChatSession` mutations never race or reorder |
| `ChatSessionPersister` | Subscribes to `ChatSession` events and queues the resulting writes through a `SessionPersistenceWriter` (user messages on add, steps on completion; transients never persisted) |
| `SessionPersistenceWriter` | Single background queue that drains persistence work items in enqueue order and surfaces failures via `PersistFailed` instead of swallowing them |
| `JsonSessionStore` | Implements `ISessionStore`: `sessions-index.json` + one `messages-{sessionId}.jsonl` per session under the project's `.vs\vsacp\sessions\` folder; disabled (no-op) when there is no real project directory or it is a network path |
| `ShadowGitCheckpointService` | Implements `ICheckpointService` via a private bare git repo (LibGit2Sharp, no git.exe) at `.vs\vsacp\checkpoints\shadow.git`; excludes build/VCS folders and oversize/locked files |
| `RevertCoordinator` | Orchestrates "Revert to here": restores the agent's reported files (or everything), truncates persisted history by ordinal, deletes the discarded checkpoints, resets the agent session, and primes the prior-conversation context prefix |
| `LineDiff` | Line-level diff (`LineHunk`s) between two versions of a file's text, for the per-hunk Keep/Undo editor adornment |
| `DiffKindRules` | Pure rules for merging/classifying pending file changes (the Keep/Undo list) as they arrive and get edited |
| `Composer` | Pure rules for turning composer state into a `TurnSubmission`, and restoring a reverted message back into the composer (`RestoredDraft`) |
| `FileLinkResolver` | Resolves a path as an agent wrote it (with `:line` suffix, Git-Bash form, relative to the solution, ...) to a real file |
| `UsageFormatter` | Formats the agent's `usage_update` into the compact toolbar readout and tooltip |
| `SelectOptions` | Reads the choices of an ACP select config option (e.g. the model picker) |
| `ProjectFolders` | Which project folders outside the solution folder to share with the agent (`additionalDirectories`) |
| `ExtraFolders`, `ExtraFoldersStore`, `ExtraFoldersController` | User-editable extra-folder list shared with the agent: merge rules/validation, `folders.json` persistence, and the per-solution controller shared by every tab |

### `VsAcp.Core.Diagnostics`

`PageLog` / `PageLogLimiter` — turns the chat WebView's console output and uncaught exceptions (via the DevTools protocol) into sanitized, rate-limited log lines for the extension log.

### `VsAcp.Core.DependencyInjection`

`ServiceCollectionExtensions.AddVsAcpServices` — registers every service below as a singleton (see **Registration**).

## Sending a prompt — request flow

1. The extension calls `ChatSessionController.SendAsync(TurnSubmission, ct)`.
2. `ChatTurnRunner` snapshots the workspace (`ICheckpointService.CreateCheckpointAsync`), adds the user message to `ChatSession`, and calls `IChatService.SendMessageAsync`.
3. `AcpSessionService` builds a `PromptRequest` (text + image/file content blocks), opens a `Channel<string>`, calls `PromptAsync`, and returns the channel as `IAsyncEnumerable<string>`.
4. `SessionNotification` messages from the agent route through `AcpClientHandler` → `SessionUpdateAdapter`, which writes text chunks to the channel and raises `IOutputListener` events (`OnStepStarted`/`OnStepUpdated`/`OnStepCompleted`) for tool calls.
5. `ChatSessionController` applies those events to `ChatSession` through a `SerialDispatcher` (so output always lands in order), and `ChatSessionPersister` queues the resulting writes to `ISessionStore`.

## Writing a file — inline diff flow (write-then-review)

There is no pre-write approval gate: the agent's file write always succeeds immediately, and the user reviews it afterward via the inline diff overlay.

1. Agent sends `WriteTextFileRequest`.
2. `FileSystemHandler` reads the file's current content (for an existing file), writes the new content to disk at once, then — for an existing file only — fires `IDiffBroker.FireDiffCompleted` with the before/after text.
3. Separately, `ToolCallDiffNotifier` fires diffs for edits/deletes a tool call explicitly reported (comparing against the turn's shadow-git checkpoint, not the live buffer) and for changes a workspace scan finds that no tool call announced (shell commands, build output, the user's own edits), tagging each `DiffRequest` with `DiffSource.Agent` or `DiffSource.Scan`.
4. The extension renders these as an inline Keep/Undo adornment in the editor; nothing here waits on the user or throws on rejection.

## Revert to here

1. Every turn is preceded by a checkpoint (`ICheckpointService.CreateCheckpointAsync`), whose id is stored on the user message.
2. `ChatSessionController.RevertAsync(checkpointId, includeOther)` flushes pending persistence, then `RevertCoordinator`: restores the files the agent reported changing in the discarded turns (or every file, if `includeOther` or the agent's files are unknown), truncates persisted history by ordinal, deletes the discarded checkpoints, and then gives the agent the kept conversation:
   - when the agent advertises the unstable `sessionCapabilities.fork` and the last kept message is a reply with an agent message id (`RevertCoordinator.ForkPoint`), `IChatService.TryForkSessionAsync` forks the agent session at that reply (`session/fork`, fork point in `_meta.jetbrains.air.fork`, as claude-agent-acp reads it), closes the old one and persists the fork's id;
   - otherwise (Cursor and other agents without fork, or a fork that fails or times out) it clears the agent-side session and primes a "prior conversation context" prefix via `IChatService.InitializeWithContextAsync` for the next prompt.
3. `ChatSessionController.PreviewRevertAsync(checkpointId)` computes the same split (agent files vs. everything else) without touching anything, for a confirmation UI.

## Registration

```csharp
services.AddVsAcpServices(opts =>
{
    opts.WorkingDirectory = "/path/to/project";
    opts.AgentCommand = "agent.cmd";
});

// Provide extension-owned UI implementations
services.AddSingleton<IOutputListener>(myOutputListener);
services.AddSingleton<ITerminalOutputSink>(myTerminalSink); // optional
```

`AddVsAcpServices` also registers `ISessionStore`, `ICheckpointService`, `IPermissionBroker`, `IElicitationBroker`, `IDiffBroker`, `AgentProcessHost`, the ACP handler classes, and `AcpSessionService` itself, all as singletons, because they hold process, channel, and checkpoint state across the lifetime of a tab.

## Dependencies

| Package | Purpose |
|---|---|
| `AcpProtocol` | JSON-RPC transport and ACP message types |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | DI interfaces |
| `Microsoft.Extensions.Logging.Abstractions` | Logging |
| `Microsoft.Extensions.Options` | Options pattern |
| `Serilog` | Structured logging |
| `System.Text.Json` | JSON serialization for session persistence |
| `System.Threading.Channels` | Streaming agent replies as `IAsyncEnumerable<string>` |
| `System.Memory` | Span/memory primitives used by `LineDiff` |
| `LibGit2Sharp` | Shadow-git workspace checkpoints (no git.exe dependency) |

Targets **.NET Framework 4.7.2** for Visual Studio extension compatibility.
