import { useLayoutEffect, useRef, useState } from 'preact/hooks';
import type { FoldersState, SharedFolder } from '../bridge/protocol';
import { FolderOpenIcon } from './icons';

/**
 * Read-only toolbar button for the folders this tab's agent session was actually started with
 * (ACP additionalDirectories as sent on session/new or session/load). Unlike the shared-folders chip under
 * the box, which edits what new chats will get, this shows what the running session has, so it can differ.
 * Hidden until the session exists, and when there is nothing to report.
 */
export function SessionFolders(props: { active: SharedFolder[] | null; configured: FoldersState | null }) {
  const { active, configured } = props;
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

  if (!active) return null;

  const configuredCount = configured?.items.length ?? 0;
  // folders are set up but the agent cannot take them, so the session has none
  const unsupported = configured?.supported === false && configuredCount > 0 && active.length === 0;
  if (active.length === 0 && !unsupported) return null;

  const key = (p: string) => p.toLowerCase();
  const activeKeys = new Set(active.map((f) => key(f.path)));
  const configuredKeys = new Set((configured?.items ?? []).map((f) => key(f.path)));
  const differs =
    !unsupported &&
    configured !== null &&
    (activeKeys.size !== configuredKeys.size || [...activeKeys].some((k) => !configuredKeys.has(k)));

  const title = unsupported
    ? 'Extra folders are not shared with this chat: the agent does not support them'
    : `${active.length} extra folder(s) shared with this chat`;

  return (
    <div class="dd session-folders" ref={ref} onKeyDown={(e) => e.key === 'Escape' && setOpen(false)}>
      <button
        type="button"
        class={unsupported ? 'tb-btn folders-session-btn warn' : 'tb-btn folders-session-btn'}
        title={title}
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
      >
        <FolderOpenIcon />
        <span class="folders-count">{unsupported ? 0 : active.length}</span>
      </button>
      {open && (
        <div class="folders-pop" role="dialog" aria-label="Folders shared with this chat">
          <div class="folders-head">Folders shared with this chat</div>
          {unsupported ? (
            <div class="folders-warn" role="status">
              This agent does not support extra folders, so none are shared with this chat.
            </div>
          ) : (
            <ul class="folders-list">
              {active.map((f) => (
                <li key={f.path} class="folders-row" title={f.path}>
                  <span class="folders-text">
                    <span class="folders-name">{f.displayName}</span>
                    <span class="folders-path">{f.path}</span>
                  </span>
                </li>
              ))}
            </ul>
          )}
          {differs && <div class="folders-hint session-folders-differs">Folder changes apply to new chats.</div>}
        </div>
      )}
    </div>
  );
}
