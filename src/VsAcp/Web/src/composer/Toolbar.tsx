import { useEffect, useRef, useState } from 'preact/hooks';
import type { Choice } from '../bridge/protocol';
import { useChat } from '../chat';
import { AttachIcon, ChevronIcon, ClaudeLogo, CursorLogo, FileIcon, SendIcon, ShieldIcon, StopIcon } from './icons';
import { SessionFolders } from './SessionFolders';

function Dropdown(props: { items: Choice[]; selectedId: string | null; title: string; onSelect(id: string): void }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  if (props.items.length === 0) return null;
  const current = props.items.find((i) => i.id === props.selectedId) ?? props.items[0]!;

  return (
    <div class="dd" ref={ref} onKeyDown={(e) => e.key === 'Escape' && setOpen(false)}>
      <button type="button" class="tb-btn dd-btn" title={props.title} aria-haspopup="listbox" aria-expanded={open} onClick={() => setOpen(!open)}>
        <span class="dd-label">{current.name}</span>
        <ChevronIcon />
      </button>
      {open && (
        <ul class="dd-list" role="listbox" aria-label={props.title}>
          {props.items.map((item) => (
            <li
              key={item.id}
              role="option"
              aria-selected={item.id === current.id}
              class={item.id === current.id ? 'dd-item selected' : 'dd-item'}
              onClick={() => {
                setOpen(false);
                if (item.id !== current.id) props.onSelect(item.id);
              }}
            >
              {item.name}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function Toolbar() {
  const chat = useChat();
  const post = chat.post;
  const s = chat.composer.value;
  return (
    <div class="toolbar">
      <div class="tb-left">
        {s.agent === 'claude' && (
          <span class="agent-badge" title="Claude Code">
            <ClaudeLogo />
          </span>
        )}
        {s.agent === 'cursor' && (
          <span class="agent-badge" title="Cursor">
            <CursorLogo />
          </span>
        )}
        <Dropdown title="Select mode" items={s.modes} selectedId={s.modeId} onSelect={(id) => post({ type: 'composer.setMode', id })} />
        <Dropdown title="Select model" items={s.models} selectedId={s.modelId} onSelect={(id) => post({ type: 'composer.setModel', id })} />
        <button
          type="button"
          class={s.autoApprove ? 'tb-btn auto on' : 'tb-btn auto'}
          title="Auto-approve: grant all agent permission requests (including commands and file changes) without asking"
          aria-pressed={s.autoApprove}
          onClick={() => post({ type: 'composer.setAutoApprove', value: !s.autoApprove })}
        >
          <ShieldIcon />
          {s.autoApprove && <span class="auto-label">Auto</span>}
        </button>
        <SessionFolders active={s.activeFolders} configured={s.folders} />
        {s.usage && (
          <span class="usage" title={s.usage.tooltip ?? undefined}>
            {s.usage.text}
          </span>
        )}
        {s.activeContext && (
          <button
            type="button"
            class={s.activeContext.enabled ? 'ctx-btn' : 'ctx-btn off'}
            title={s.activeContext.filePath}
            aria-pressed={s.activeContext.enabled}
            onClick={() => post({ type: 'composer.toggleActiveContext' })}
          >
            <FileIcon />
            <span class="ctx-name">{s.activeContext.fileName}</span>
          </button>
        )}
      </div>
      <div class="tb-right">
        {!s.busy && s.canSend ? (
          <button
            type="button"
            class="tb-btn send"
            title="Send (Enter)"
            onClick={() => {
              const ctrl = chat.editor.current;
              post({ type: 'composer.send', text: ctrl?.text ?? '', commandName: ctrl?.commandName ?? null });
              ctrl?.focus();
            }}
          >
            <SendIcon />
          </button>
        ) : (
          <button type="button" class={s.busy ? 'tb-btn stop busy' : 'tb-btn stop'} title="Stop (Esc)" disabled={!s.busy} onClick={() => post({ type: 'composer.stop' })}>
            <StopIcon />
          </button>
        )}
        <button type="button" class="tb-btn" title="Attach image (Ctrl+V to paste)" onClick={() => post({ type: 'composer.attachImage' })}>
          <AttachIcon />
        </button>
      </div>
    </div>
  );
}
