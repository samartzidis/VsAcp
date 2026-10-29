import { useLayoutEffect, useRef, useState } from 'preact/hooks';
import { post } from '../bridge/host';
import type { TabInfo } from '../bridge/protocol';
import { tabs } from '../panel';

/**
 * The chat tabs across the top of the panel, with "+" for a new chat, as the tool window's WPF strip had them: a click
 * shows a tab (the host creates its chat the first time), a double-click or the context menu renames it, and the
 * close button closes it, which deletes the chat. The host owns the list: every action is a request, and the strip
 * shows what the next `tabs` message says.
 *
 * Presses on the strip never take the focus (preventDefault on mousedown), so the input of the chat being shown
 * keeps it; the rename box is the only thing here that does.
 */
export function TabStrip() {
  const list = tabs.value;
  const [renaming, setRenaming] = useState<string | null>(null);
  const [menu, setMenu] = useState<{ id: string; x: number; y: number } | null>(null);

  // the context menu closes on any press elsewhere, and on Esc
  useLayoutEffect(() => {
    if (!menu) return;
    const close = () => setMenu(null);
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && close();
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', onKey);
    };
  }, [menu]);

  return (
    <div class="tab-strip">
      <div class="tab-list" role="tablist" aria-label="Chats">
        {list.map((tab) => (
          <Tab
            key={tab.id}
            tab={tab}
            renaming={renaming === tab.id}
            onRename={() => setRenaming(tab.id)}
            onRenameEnd={() => setRenaming(null)}
            onMenu={(x, y) => setMenu({ id: tab.id, x, y })}
          />
        ))}
      </div>
      <button
        type="button"
        class="tab-new"
        title="New chat"
        aria-label="New chat"
        onMouseDown={(e) => e.preventDefault()}
        onClick={() => post({ type: 'tabs.new' })}
      >
        +
      </button>
      {menu && (
        <div
          class="ctx-menu tab-menu"
          role="menu"
          style={{ display: 'block', left: `${menu.x}px`, top: `${menu.y}px` }}
          onMouseDown={(e) => e.stopPropagation()}
        >
          <div
            class="ctx-menu-item"
            role="menuitem"
            onClick={() => {
              setRenaming(menu.id);
              setMenu(null);
            }}
          >
            Rename
          </div>
        </div>
      )}
    </div>
  );
}

function Tab(props: { tab: TabInfo; renaming: boolean; onRename(): void; onRenameEnd(): void; onMenu(x: number, y: number): void }) {
  const { tab } = props;
  return (
    <div
      class={tab.active ? 'tab active' : 'tab'}
      role="tab"
      aria-selected={tab.active}
      title={tab.name}
      onMouseDown={(e) => {
        if (props.renaming || e.button !== 0) return;
        e.preventDefault();
        if (!tab.active) post({ type: 'tabs.activate', id: tab.id });
      }}
      onDblClick={() => props.onRename()}
      onContextMenu={(e) => {
        e.preventDefault();
        // not the page's Copy menu, which listens on the document
        e.stopPropagation();
        props.onMenu(Math.min(e.clientX, window.innerWidth - 120), e.clientY);
      }}
    >
      {props.renaming ? (
        <RenameBox tab={tab} onEnd={props.onRenameEnd} />
      ) : (
        <span class="tab-name">{tab.name}</span>
      )}
      <button
        type="button"
        class="tab-close"
        title="Close chat"
        aria-label={`Close ${tab.name}`}
        // closing a background tab must not bring it forward first
        onMouseDown={(e) => {
          e.preventDefault();
          e.stopPropagation();
        }}
        onClick={(e) => {
          e.stopPropagation();
          post({ type: 'tabs.close', id: tab.id });
        }}
      >
        {'✕'}
      </button>
    </div>
  );
}

/** Edits a tab's name in place: Enter or leaving the box keeps the new name, Esc keeps the old one. */
function RenameBox(props: { tab: TabInfo; onEnd(): void }) {
  const input = useRef<HTMLInputElement>(null);
  const done = useRef(false);

  // the starting text is set once, not as a prop: a re-render while the user types (a tab list update) must not reset it
  useLayoutEffect(() => {
    if (!input.current) return;
    input.current.value = props.tab.name;
    input.current.focus();
    input.current.select();
  }, []);

  const finish = (keep: boolean) => {
    // Enter and the blur it causes both end up here; only the first acts
    if (done.current) return;
    done.current = true;
    const name = input.current?.value.trim() ?? '';
    if (keep && name.length > 0 && name !== props.tab.name) post({ type: 'tabs.rename', id: props.tab.id, name });
    props.onEnd();
  };

  return (
    <input
      ref={input}
      class="tab-rename"
      aria-label="Chat name"
      onMouseDown={(e) => e.stopPropagation()}
      onDblClick={(e) => e.stopPropagation()}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          finish(true);
        } else if (e.key === 'Escape') {
          e.preventDefault();
          finish(false);
        }
      }}
      onBlur={() => finish(true)}
    />
  );
}
