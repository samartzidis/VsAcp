import { useLayoutEffect, useRef, useState } from 'preact/hooks';
import { post } from '../bridge/host';
import type { FoldersState } from '../bridge/protocol';
import { CloseIcon, FolderIcon } from './icons';

/**
 * Panel footer chip for the folders shared with the agent besides the solution folder (ACP additionalDirectories):
 * a count that opens a popover listing them (found in the solution, or added by the user), with remove and
 * "Add folder...". The host owns the list and the folder picker; the page only shows it and posts requests.
 */
export function FoldersMenu(props: { folders: FoldersState }) {
  const { folders } = props;
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // layout effect: the outside-click handler must be active as soon as the popover is shown
  useLayoutEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  const count = folders.items.length;
  const unsupported = folders.supported === false;
  const warn = unsupported && count > 0;
  const title = warn
    ? `${count} extra folder(s) - this agent does not support them`
    : count === 0
      ? 'Share extra folders with the agent (applies to new chats)'
      : `${count} extra folder(s) shared with the agent (changes apply to new chats)`;

  return (
    <div class="dd folders" ref={ref} onKeyDown={(e) => e.key === 'Escape' && setOpen(false)}>
      <button
        type="button"
        class={warn ? 'ctx-btn folders-btn warn' : 'ctx-btn folders-btn'}
        title={title}
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
      >
        <FolderIcon />
        <span class="folders-count">{count}</span>
      </button>
      {open && (
        <div class="folders-pop" role="dialog" aria-label="Folders shared with the agent">
          <div class="folders-head">Folders shared with the agent</div>
          {count === 0 ? (
            <div class="folders-empty">No extra folders. The solution folder is always shared.</div>
          ) : (
            <ul class="folders-list">
              {folders.items.map((f) => (
                <li key={f.path} class="folders-row" title={f.path}>
                  <span class="folders-text">
                    <span class="folders-name">{f.displayName}</span>
                    <span class="folders-path">{f.path}</span>
                  </span>
                  <span class="folders-tag">{f.source === 'auto' ? 'project' : 'added'}</span>
                  {folders.canEdit && (
                    <button type="button" class="folders-x" aria-label={`Stop sharing ${f.displayName}`} title="Stop sharing" onClick={() => post({ type: 'folders.remove', path: f.path })}>
                      <CloseIcon />
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {unsupported && (
            <div class="folders-warn" role="status">
              This agent does not support extra folders, so they are not shared with it.
            </div>
          )}
          <div class="folders-hint">Applies to new chats and when a chat is reopened.</div>
          {folders.canEdit && (
            <button type="button" class="folders-add" onClick={() => post({ type: 'folders.add' })}>
              Add folder...
            </button>
          )}
        </div>
      )}
    </div>
  );
}
