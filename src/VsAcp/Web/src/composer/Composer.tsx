import { useEffect, useReducer, useRef } from 'preact/hooks';
import { post } from '../bridge/host';
import type { SlashCommand } from '../bridge/protocol';
import { composer, editor } from '../state';
import { Attachments } from './Attachments';
import { EditorController } from './editorDom';
import { filterCommands, moveIndex } from './editorModel';
import { FoldersMenu } from './FoldersMenu';
import { SlashPopup } from './SlashPopup';
import { Toolbar } from './Toolbar';

export function Composer() {
  const s = composer.value;
  const rootRef = useRef<HTMLDivElement>(null);
  const ctrlRef = useRef<EditorController | null>(null);
  const lastSent = useRef<{ text: string; command: string | null } | null>(null);
  const [, rerender] = useReducer((n: number) => n + 1, 0);

  // Popup state lives in refs and is recomputed from the live DOM/snapshot, so a key pressed before
  // Preact has re-rendered (fast typing, key repeat) never acts on a stale popup.
  const indexRef = useRef(0);
  const dismissedRef = useRef(false);
  const filterRef = useRef<string | null>(null);
  const emptyRef = useRef(true);

  const liveMatches = (): SlashCommand[] => {
    const f = ctrlRef.current?.slashToken()?.filter;
    if (f === undefined || dismissedRef.current || !composer.peek().enabled) return [];
    return filterCommands(composer.peek().commands, f);
  };

  useEffect(() => {
    const ctrl = new EditorController(rootRef.current!, {
      onChange: (text, command) => {
        emptyRef.current = ctrl.isEmpty;
        dismissedRef.current = false;
        const prev = lastSent.current;
        if (!prev || prev.text !== text || prev.command !== command) {
          lastSent.current = { text, command };
          post({ type: 'composer.changed', text, commandName: command });
        }
        rerender(0);
      },
      onCaret: () => {
        const filter = ctrl.slashToken()?.filter ?? null;
        if (filter !== filterRef.current) {
          filterRef.current = filter;
          indexRef.current = 0;
          rerender(0);
        }
      },
    });
    ctrlRef.current = ctrl;
    editor.current = ctrl;
    return () => {
      ctrl.dispose();
      editor.current = null;
    };
  }, []);

  // The editor is not rendered by Preact, so its editability is applied imperatively.
  useEffect(() => {
    if (rootRef.current) rootRef.current.contentEditable = s.enabled ? 'plaintext-only' : 'false';
  }, [s.enabled]);

  const acceptCommand = (name: string) => {
    ctrlRef.current?.acceptSlashCommand(name);
    filterRef.current = null;
    rerender(0);
  };

  const onKeyDown = (e: KeyboardEvent) => {
    const ctrl = ctrlRef.current;
    if (!ctrl || e.isComposing || e.keyCode === 229) return;

    const matches = liveMatches();
    if (matches.length > 0) {
      const selected = Math.min(indexRef.current, matches.length - 1);
      switch (e.key) {
        case 'ArrowDown':
        case 'ArrowUp':
          e.preventDefault();
          indexRef.current = moveIndex(selected, e.key === 'ArrowDown' ? 1 : -1, matches.length);
          rerender(0);
          return;
        case 'Enter':
        case 'Tab': {
          e.preventDefault();
          const cmd = matches[selected];
          if (cmd) acceptCommand(cmd.name);
          return;
        }
        case 'Escape':
          e.preventDefault();
          dismissedRef.current = true;
          rerender(0);
          return;
      }
    }

    if (e.key === 'Enter' && !e.shiftKey) {
      // Always swallow plain Enter; the host decides whether a send is possible right now.
      e.preventDefault();
      post({ type: 'composer.send', text: ctrl.text, commandName: ctrl.commandName });
      return;
    }
    if (e.key === 'Escape' && composer.peek().busy) {
      e.preventDefault();
      post({ type: 'composer.stop' });
      return;
    }
    if (e.key === 'Backspace' && ctrl.caretRightAfterChip()) {
      e.preventDefault();
      ctrl.removeChip();
    }
  };

  const onPaste = (e: ClipboardEvent) => {
    // Never let the browser paste rich content; the host may turn the paste into an attachment or chip.
    e.preventDefault();
    const text = e.clipboardData?.getData('text/plain') ?? '';
    const hasImage = Array.from(e.clipboardData?.items ?? []).some((i) => i.type.startsWith('image/'));
    if (!hasImage && !text.includes('\n')) {
      // A single line can never be a code reference: no need to ask the host.
      if (text) ctrlRef.current?.insertText(text);
      return;
    }
    post({ type: 'composer.paste', text, hasImage });
  };

  const matches = liveMatches();
  const selected = Math.min(indexRef.current, Math.max(0, matches.length - 1));

  return (
    <div class="composer">
      <div class="composer-box">
        {matches.length > 0 && <SlashPopup commands={matches} selected={selected} onPick={acceptCommand} />}
        <Attachments />
        <div class="editor-wrap">
          {emptyRef.current && <div class="placeholder">{s.placeholder}</div>}
          <div
            ref={rootRef}
            class="editor"
            role="textbox"
            aria-multiline="true"
            aria-label="Message"
            spellcheck={false}
            tabIndex={0}
            onKeyDown={onKeyDown}
            onPaste={onPaste}
          />
        </div>
        <Toolbar />
      </div>
      {/* Outside the box on purpose: the shared folders belong to the whole solution, not to this message. */}
      {s.folders && (
        <div class="composer-footer">
          <FoldersMenu folders={s.folders} />
        </div>
      )}
    </div>
  );
}
