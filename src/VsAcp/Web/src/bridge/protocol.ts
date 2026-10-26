// Contract between the WebView2 page (this project) and the VS host (ChatWebView / ComposerBridge in
// C#). Every message is a JSON object with a string `type`. Keep this file and
// Bridge/ComposerProtocol.cs in sync; the C# side serialises with camelCase names.

export interface Choice {
  id: string;
  name: string;
}

export interface SlashCommand {
  name: string;
  description: string;
}

export interface ImageChip {
  id: string;
  fileName: string;
}

export interface FileChip {
  id: string;
  fileName: string;
  filePath: string;
}

/** A folder dropped on the composer: a one-off reference for this message only, distinct from the shared extra-folders chip (`folders` below). */
export interface FolderChip {
  id: string;
  folderName: string;
  folderPath: string;
}

export interface SelectionChip {
  id: string;
  label: string;
  filePath: string;
}

export interface ActiveContext {
  fileName: string;
  filePath: string;
  enabled: boolean;
}

/** A file with a pending change (the "N Files" list above the composer). */
export interface PendingFile {
  fileName: string;
  filePath: string;
  /** Path relative to the workspace folder (forward slashes), for the tooltip. */
  displayPath: string;
  /** What the agent did to it: edited it, created it, or removed it. */
  kind: 'modified' | 'added' | 'deleted';
  added: number;
  removed: number;
  /**
   * "agent": the agent reported the change (main list). "scan": found by comparing the workspace with the
   * turn's checkpoint (a shell command, a build, the user's own edits), listed under "Other changes".
   */
  source: PendingSource;
}

export type PendingSource = 'agent' | 'scan';

/**
 * A folder shared with the agent besides the solution folder: found in the solution ("auto") or added by the
 * user ("manual"). In `activeFolders` the origin is not tracked and the source is always "active".
 */
export interface SharedFolder {
  path: string;
  displayName: string;
  source: 'auto' | 'manual' | 'active';
}

/** The extra folders (ACP additionalDirectories); null in the state when there is no local project to attach them to. */
export interface FoldersState {
  items: SharedFolder[];
  /** Whether the agent uses extra folders; null until it has answered `initialize`. */
  supported: boolean | null;
  /** False when there is nowhere to save changes. */
  canEdit: boolean;
}

export interface Usage {
  text: string;
  tooltip: string | null;
}

/**
 * Everything the composer shows except the text being typed. The host sends a complete snapshot
 * whenever any of it changes (idempotent, no deltas). The text itself is owned by the page while the
 * user types and only set by the host through `composer.set` (restore after revert, clear after send).
 */
export interface ComposerState {
  enabled: boolean;
  placeholder: string;
  canSend: boolean;
  busy: boolean;
  images: ImageChip[];
  files: FileChip[];
  attachedFolders: FolderChip[];
  selections: SelectionChip[];
  activeContext: ActiveContext | null;
  commands: SlashCommand[];
  modes: Choice[];
  modeId: string | null;
  models: Choice[];
  modelId: string | null;
  autoApprove: boolean;
  usage: Usage | null;
  pendingFiles: PendingFile[];
  folders: FoldersState | null;
  /** The extra folders this tab's agent session was started with; null until the session exists. May differ from `folders`. */
  activeFolders: SharedFolder[] | null;
}

export type AttachmentKind = 'image' | 'file' | 'folder' | 'selection';

// ---- chat messages (the list above the composer) ----

export type MessageType = 'User' | 'Assistant' | 'ToolStep' | 'Thinking' | 'Notice' | 'Compaction';
export type MessageStatus = 'Pending' | 'Success' | 'Error' | 'Info' | 'Warning';
export type BodyMode = 'Markdown' | 'Html' | 'PlainText';

export interface SelectionRef {
  label: string;
  tooltip: string;
}

/** One chat message as the host's session model holds it (ChatMessageData in C#). */
export interface ChatMessage {
  id: string;
  type: MessageType;
  content: string;
  toolName: string | null;
  title: string;
  status: MessageStatus;
  expanderTitle: string;
  bodyMode: BodyMode;
  body: string | null;
  language: string | null;
  isStreaming: boolean;
  imageDataUris: string[] | null;
  fileNames: string[] | null;
  folderNames: string[] | null;
  selections: SelectionRef[] | null;
  /** Workspace snapshot taken before this user message; enables "Revert to here". */
  checkpointId: string | null;
}

// ---- banners: one prompt at a time between the message list and the composer ----

export interface PermissionOption {
  optionId: string;
  name: string;
  /** ACP option kind; `allow_*` options are the affirmative (accent) buttons. */
  kind: string;
}

export interface RevertFile {
  action: 'delete' | 'overwrite' | 'recreate';
  path: string;
}

/** One group of files a revert would touch. `files` is capped by the host; `omitted` is how many are not listed. */
export interface RevertGroup {
  deleted: number;
  overwritten: number;
  recreated: number;
  files: RevertFile[];
  omitted: number;
}

/** What a revert would change: the agent's files (always reverted) and everything else (only when asked). */
export interface RevertPreview {
  agent: RevertGroup;
  /** Changes the agent did not report: your own edits, other chats, unreported shell changes, build output. Null when none. */
  other: RevertGroup | null;
  /** False for chat history saved before the agent's files were recorded: `agent` then holds every change, all reverted. */
  agentFilesKnown: boolean;
}

export type FieldKind = 'text' | 'boolean' | 'integer' | 'number' | 'select' | 'multiSelect';

export interface FieldOption {
  value: string;
  title: string;
  description?: string | null;
}

/** One elicitation form field, already translated from the agent's JSON schema by the host. */
export interface ElicitationField {
  name: string;
  kind: FieldKind;
  title?: string | null;
  description?: string | null;
  required: boolean;
  format?: string | null;
  pattern?: string | null;
  minLength?: number | null;
  maxLength?: number | null;
  minimum?: number | null;
  maximum?: number | null;
  minItems?: number | null;
  maxItems?: number | null;
  options?: FieldOption[] | null;
  defaultText?: string | null;
  defaultBoolean?: boolean | null;
  defaultNumber?: number | null;
  defaultValues?: string[] | null;
}

export type Banner =
  | { kind: 'permission'; id: string; title: string; options: PermissionOption[] }
  | { kind: 'revert'; id: string; preview: RevertPreview | null }
  | {
      kind: 'elicitation';
      id: string;
      title: string | null;
      message: string;
      description: string | null;
      /** Set for URL-mode elicitations (fields is then empty). The page never opens it itself. */
      url: string | null;
      fields: ElicitationField[];
    }
  | { kind: 'error'; id: string; message: string };

/** What the page answers, by banner kind: permission `optionId`, revert `confirmed`/`includeOther`, elicitation `action`/`values`, error `{}`. */
export type BannerResult =
  | { optionId: string }
  | { confirmed: boolean; includeOther?: boolean }
  | { action: 'accept' | 'decline' | 'cancel'; values?: Record<string, unknown> }
  | Record<string, never>;

/**
 * The VS theme as the page needs it: a few anchor colours (see src/theme.css) plus the scheme the editor
 * background implies and whether a Windows high-contrast theme is active. Re-sent on every theme change.
 */
export interface ThemeMessage {
  type: 'theme';
  colors: Record<string, string>;
  scheme: 'dark' | 'light';
  highContrast: boolean;
}

/** Host -> page. */
export type HostMessage =
  | { type: 'state'; state: ComposerState }
  | { type: 'composer.set'; text: string; commandName: string | null }
  | { type: 'composer.insert'; text: string }
  | { type: 'composer.focus' }
  | { type: 'banner.show'; banner: Banner }
  | { type: 'banner.hide' }
  | ThemeMessage
  | { type: 'messages.add'; message: ChatMessage }
  | { type: 'messages.content'; id: string; content: string }
  | { type: 'messages.status'; id: string; status: MessageStatus; expanderTitle: string }
  | { type: 'messages.body'; id: string; body: string; bodyMode: BodyMode; language: string | null }
  | { type: 'messages.complete'; id: string }
  | { type: 'messages.clear' }
  /** Replaces the whole list with the session snapshot (the page asks for it by posting `ready`). */
  | { type: 'messages.load'; messages: ChatMessage[] }
  /** Like load, but the page acknowledges with `messages.synced` so the host knows the view converged. */
  | { type: 'messages.sync'; syncId: string; messages: ChatMessage[] };

/** Page -> host. `openFile`, `revert` and `fork` (chat messages) are handled by ChatWebView directly. */
export type WebMessage =
  | { type: 'ready' }
  | { type: 'composer.changed'; text: string; commandName: string | null }
  | { type: 'composer.send'; text: string; commandName: string | null }
  | { type: 'composer.stop' }
  | { type: 'composer.paste'; text: string; hasImage: boolean }
  | { type: 'composer.remove'; kind: AttachmentKind; id: string }
  | { type: 'composer.attachImage' }
  /** Files dropped on the page; their paths travel as WebView2 additional objects (none = ask the host's Solution Explorer fallback). */
  | { type: 'composer.drop' }
  | { type: 'composer.setMode'; id: string }
  | { type: 'composer.setModel'; id: string }
  | { type: 'composer.setAutoApprove'; value: boolean }
  | { type: 'composer.toggleActiveContext' }
  /** Opens the host's folder picker and shares the chosen folder with the agent. */
  | { type: 'folders.add' }
  | { type: 'folders.remove'; path: string }
  /** Items dropped on the folders chip; the host reads their paths (WebView2 additional objects) and adds the folders among them. */
  | { type: 'folders.drop' }
  | { type: 'changes.acceptAll'; source: PendingSource }
  | { type: 'changes.rejectAll'; source: PendingSource }
  | { type: 'changes.open'; path: string }
  | { type: 'changes.keep'; path: string }
  | { type: 'changes.undo'; path: string }
  | { type: 'banner.resolve'; id: string; result: BannerResult }
  /** Asks the host to open the URL of the banner with this id (the page never receives a URL to open). */
  | { type: 'banner.openUrl'; id: string }
  | { type: 'messages.synced'; syncId: string }
  /** A file path link was clicked in rendered content (path may carry a :line suffix). */
  | { type: 'openFile'; path: string }
  /** "Revert to here" on a user message. */
  | { type: 'revert'; checkpointId: string }
  /** "Fork from here" on a user message or a reply: a new chat continues from there. */
  | { type: 'fork'; messageId: string };

export const emptyComposerState: ComposerState = {
  enabled: false,
  placeholder: 'Connecting to agent...',
  canSend: false,
  busy: false,
  images: [],
  files: [],
  attachedFolders: [],
  selections: [],
  activeContext: null,
  commands: [],
  modes: [],
  modeId: null,
  models: [],
  modelId: null,
  autoApprove: false,
  usage: null,
  pendingFiles: [],
  folders: null,
  activeFolders: null,
};
