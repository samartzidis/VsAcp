// DOM-level behaviour of the composer's editable surface: plain text with at most one slash-command
// chip pinned at the very start. Kept free of Preact so the logic can be driven directly by tests.

import { findSlashToken, type SlashToken } from './editorModel';

export const CHIP_CLASS = 'cmd-chip';

export interface EditorCallbacks {
  /** Text or chip changed through the user or the host. */
  onChange(text: string, commandName: string | null): void;
  /** The caret moved or the content changed; the owner re-evaluates the slash popup. */
  onCaret(): void;
}

/** Plain text of a node/fragment: text nodes and <br> / block boundaries become characters; the chip is skipped. */
export function readText(node: Node): string {
  let out = '';
  const walk = (n: Node): void => {
    if (n.nodeType === Node.TEXT_NODE) {
      out += (n.textContent ?? '').replace(/\u00A0/g, ' ');
      return;
    }
    if (n.nodeType !== Node.ELEMENT_NODE && n.nodeType !== Node.DOCUMENT_FRAGMENT_NODE) return;
    const el = n as Element;
    if (el.classList?.contains(CHIP_CLASS)) return;
    if (el.tagName === 'BR') {
      out += '\n';
      return;
    }
    const block = el.tagName === 'DIV' || el.tagName === 'P';
    if (block && out.length > 0 && !out.endsWith('\n')) out += '\n';
    el.childNodes.forEach(walk);
  };
  walk(node);
  return out;
}

function createChip(name: string): HTMLSpanElement {
  const chip = document.createElement('span');
  chip.className = CHIP_CLASS;
  chip.contentEditable = 'false';
  chip.dataset.command = name;
  chip.textContent = '/' + name;
  return chip;
}

export class EditorController {
  private suppress = false;

  constructor(
    readonly root: HTMLElement,
    private readonly cb: EditorCallbacks,
  ) {
    root.addEventListener('input', this.onInput);
    root.addEventListener('beforeinput', this.onBeforeInput);
    document.addEventListener('selectionchange', this.onSelectionChange);
  }

  dispose(): void {
    this.root.removeEventListener('input', this.onInput);
    this.root.removeEventListener('beforeinput', this.onBeforeInput);
    document.removeEventListener('selectionchange', this.onSelectionChange);
  }

  get chip(): HTMLElement | null {
    const first = this.root.firstElementChild;
    return first?.classList.contains(CHIP_CLASS) ? (first as HTMLElement) : null;
  }

  get commandName(): string | null {
    return this.chip?.dataset.command ?? null;
  }

  get text(): string {
    return readText(this.root);
  }

  get isEmpty(): boolean {
    return this.chip === null && this.text.length === 0;
  }

  /** Replaces the whole content (restore after revert, clear after send). Caret goes to the end. */
  setContent(text: string, commandName: string | null): void {
    this.suppress = true;
    this.root.replaceChildren();
    if (commandName) this.root.appendChild(createChip(commandName));
    if (text) this.root.appendChild(document.createTextNode(text));
    this.suppress = false;
    this.placeCaretAtEnd();
    this.notify();
  }

  /** Inserts plain text at the caret (keeps the undo stack). */
  insertText(text: string): void {
    this.root.focus();
    this.ensureCaretAfterChip();
    if (!document.execCommand('insertText', false, text)) {
      this.root.appendChild(document.createTextNode(text));
      this.placeCaretAtEnd();
    }
    this.notify();
  }

  /** Puts `name` in the chip slot (replacing any existing chip) without touching the typed text. */
  setCommand(name: string | null): void {
    this.suppress = true;
    this.chip?.remove();
    if (name) this.root.insertBefore(createChip(name), this.root.firstChild);
    this.suppress = false;
    this.notify();
  }

  /** Text between the end of the chip and the caret, or null when there is no collapsed caret in the editor. */
  textBeforeCaret(): string | null {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return null;
    const r = sel.getRangeAt(0);
    if (!this.root.contains(r.startContainer)) return null;
    const pre = document.createRange();
    pre.selectNodeContents(this.root);
    pre.setEnd(r.startContainer, r.startOffset);
    return readText(pre.cloneContents());
  }

  slashToken(): SlashToken | null {
    const before = this.textBeforeCaret();
    return before === null ? null : findSlashToken(before);
  }

  /** Removes the `/filter` token before the caret and makes `name` the chip. The caret stays where the token was. */
  acceptSlashCommand(name: string): void {
    const token = this.slashToken();
    if (token) {
      const sel = window.getSelection()!;
      for (let i = 0; i < token.length; i++) sel.modify('extend', 'backward', 'character');
      sel.getRangeAt(0).deleteContents();
      sel.collapseToEnd();
    }
    this.setCommand(name);
    this.ensureCaretAfterChip();
    this.root.focus();
  }

  /** True when the caret is directly behind the chip (Backspace there removes the whole chip). */
  caretRightAfterChip(): boolean {
    const chip = this.chip;
    const sel = window.getSelection();
    if (!chip || !sel || sel.rangeCount === 0 || !sel.isCollapsed) return false;
    const r = sel.getRangeAt(0);
    if (r.startContainer === this.root) {
      return r.startOffset === Array.prototype.indexOf.call(this.root.childNodes, chip) + 1;
    }
    return r.startContainer === chip.nextSibling && r.startOffset === 0;
  }

  removeChip(): void {
    this.chip?.remove();
    this.notify();
  }

  focus(): void {
    this.root.focus();
    this.ensureCaretAfterChip();
  }

  /** Typed text never goes in front of the chip: a caret before it is moved behind it. */
  ensureCaretAfterChip(): void {
    const chip = this.chip;
    const sel = window.getSelection();
    if (!chip || !sel || sel.rangeCount === 0 || !sel.isCollapsed) return;
    const r = sel.getRangeAt(0);
    if (r.startContainer === this.root && r.startOffset === 0) {
      sel.collapse(this.root, 1);
    }
  }

  private placeCaretAtEnd(): void {
    const sel = window.getSelection();
    if (!sel) return;
    const r = document.createRange();
    r.selectNodeContents(this.root);
    r.collapse(false);
    sel.removeAllRanges();
    sel.addRange(r);
  }

  private notify(): void {
    if (this.suppress) return;
    this.cb.onChange(this.text, this.commandName);
    this.cb.onCaret();
  }

  private readonly onInput = (): void => {
    // Browsers may leave an empty <br> or stray element behind after deleting everything.
    if (!this.chip && this.root.textContent === '' && this.root.childElementCount > 0) this.root.replaceChildren();
    this.notify();
  };

  private readonly onBeforeInput = (): void => {
    this.ensureCaretAfterChip();
  };

  private readonly onSelectionChange = (): void => {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !this.root.contains(sel.anchorNode)) return;
    this.ensureCaretAfterChip();
    this.cb.onCaret();
  };
}
