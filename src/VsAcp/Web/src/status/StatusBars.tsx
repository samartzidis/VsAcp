import { useState } from 'preact/hooks';
import type { PendingFile, PendingSource } from '../bridge/protocol';
import { ChevronIcon, CloseIcon, KeepIcon } from '../composer/icons';
import { useChat } from '../chat';
import { FileBadge } from './FileBadge';

/** "Thinking..." line shown while a turn is running. */
export function BusyBar() {
  if (!useChat().composer.value.busy) return null;
  return (
    <div class="busy-bar">
      <span class="busy-spinner" aria-hidden="true" />
      Thinking...
    </div>
  );
}

const KIND_LABEL = { added: 'new', deleted: 'deleted' } as const;

/**
 * Files the agent changed, as a collapsible list (like Cursor's): header with the count and
 * Undo All / Keep All, then one row per file (edited, created or deleted) with its +added / -removed
 * lines. A click on a row opens the file (a deleted file has nothing to open); Undo / Keep for a
 * single file appear on hover. Changes found on disk that the agent did not report (shell commands,
 * builds, the user's own edits) are a second group, "Other changes", collapsed by default with its
 * own Undo All / Keep All, so build output never crowds out the agent's edits.
 */
export function PendingChangesBar() {
  const files = useChat().composer.value.pendingFiles;
  if (files.length === 0) return null;
  const agent = files.filter((f) => f.source !== 'scan');
  const other = files.filter((f) => f.source === 'scan');

  return (
    <div class="pending-files">
      {agent.length > 0 && <PendingGroup key="agent" source="agent" files={agent} />}
      {other.length > 0 && <PendingGroup key="scan" source="scan" files={other} />}
    </div>
  );
}

function PendingGroup({ source, files }: { source: PendingSource; files: PendingFile[] }) {
  const chat = useChat();
  const isOther = source === 'scan';
  const [expanded, setExpanded] = useState(!isOther);
  const deleted = isOther ? files.filter((f) => f.kind === 'deleted').length : 0;
  // Accessible names tell the two groups' buttons apart; the visible text is the same.
  const scope = isOther ? ' other changes' : '';

  return (
    <div class={isOther ? 'pending-group other' : 'pending-group'}>
      <div class="pending-header">
        <button
          type="button"
          class="pending-toggle"
          aria-expanded={expanded}
          title={isOther ? 'Changes on disk the agent did not report: shell commands, builds, your own edits' : undefined}
          onClick={() => setExpanded(!expanded)}
        >
          <span class={expanded ? 'pending-chevron' : 'pending-chevron collapsed'}>
            <ChevronIcon />
          </span>
          {isOther ? (
            <span class="pending-other-count">{`Other changes (${files.length})`}</span>
          ) : (
            <span class="pending-count">{files.length === 1 ? '1 File' : `${files.length} Files`}</span>
          )}
          {deleted > 0 && <span class="pending-other-deleted">{`· ${deleted} deleted`}</span>}
        </button>
        <div class="pending-actions">
          <button
            type="button"
            class="pending-link"
            aria-label={`Undo All${scope}`}
            onClick={() => chat.post({ type: 'changes.rejectAll', source })}
          >
            Undo All
          </button>
          <button
            type="button"
            class="pending-link"
            aria-label={`Keep All${scope}`}
            onClick={() => chat.post({ type: 'changes.acceptAll', source })}
          >
            Keep All
          </button>
        </div>
      </div>
      {expanded && (
        <ul class="pending-list">
          {files.map((f) => (
            <PendingRow key={f.filePath} file={f} />
          ))}
        </ul>
      )}
    </div>
  );
}

function PendingRow({ file: f }: { file: PendingFile }) {
  const chat = useChat();
  const label = f.kind === 'modified' ? null : KIND_LABEL[f.kind];
  return (
    <li class={`pending-row ${f.kind}`}>
      <button
        type="button"
        class="pending-open"
        title={label ? `${f.displayPath} (${label})` : f.displayPath}
        onClick={() => {
          if (f.kind !== 'deleted') chat.post({ type: 'changes.open', path: f.filePath });
        }}
      >
        <FileBadge fileName={f.fileName} />
        <span class="pending-name">{f.fileName}</span>
        {label && <span class={`pending-kind ${f.kind}`}>{label}</span>}
        {f.added > 0 && <span class="pending-added">{`+${f.added}`}</span>}
        {f.removed > 0 && <span class="pending-removed">{`-${f.removed}`}</span>}
      </button>
      <span class="pending-row-actions">
        <button
          type="button"
          class="pending-icon-btn"
          title={f.kind === 'added' ? `Undo: delete ${f.fileName}` : f.kind === 'deleted' ? `Undo: restore ${f.fileName}` : `Undo changes to ${f.fileName}`}
          aria-label={`Undo ${f.fileName}`}
          onClick={() => chat.post({ type: 'changes.undo', path: f.filePath })}
        >
          <CloseIcon />
        </button>
        <button
          type="button"
          class="pending-icon-btn"
          title={`Keep changes to ${f.fileName}`}
          aria-label={`Keep ${f.fileName}`}
          onClick={() => chat.post({ type: 'changes.keep', path: f.filePath })}
        >
          <KeepIcon />
        </button>
      </span>
    </li>
  );
}
