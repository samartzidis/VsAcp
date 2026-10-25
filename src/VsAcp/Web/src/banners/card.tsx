// Shared look of every banner (a Cursor-style card): header (icon, label, optional "1 of N" stepper), scrolling
// body, footer (quiet secondary action + accent primary action with a key hint) and lettered option rows.

import type { ComponentChildren } from 'preact';
import { useEffect, useRef } from 'preact/hooks';
import { Svg } from '../composer/icons';
export const HelpIcon = () => (
  <Svg d="M11,18H13V16H11V18M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,6A4,4 0 0,0 8,10H10A2,2 0 0,1 12,8A2,2 0 0,1 14,10C14,12 11,11.75 11,15H13C13,12.75 16,12.5 16,10A4,4 0 0,0 12,6Z" size={16} />
);
export const WarningIcon = () => <Svg d="M13,14H11V10H13M13,18H11V16H13M1,21H23L12,2L1,21Z" size={16} />;
const UpIcon = () => <Svg d="M7.41,15.41L12,10.83L16.59,15.41L18,14L12,8L6,14L7.41,15.41Z" size={16} />;
const DownIcon = () => <Svg d="M7.41,8.59L12,13.17L16.59,8.59L18,10L12,16L6,10L7.41,8.59Z" size={16} />;
const ReturnIcon = () => <Svg d="M19,7V11H5.83L9.41,7.41L8,6L2,12L8,18L9.41,16.59L5.83,13H21V7H19Z" size={12} />;

export interface StepNav {
  /** Zero-based current step. */
  index: number;
  count: number;
  onPrev(): void;
  onNext(): void;
}

/**
 * The option row (A = 0, B = 1, ...) an unmodified letter key press selects, or -1 when it is something else
 * or the user is typing into a field.
 */
export function letterIndex(e: KeyboardEvent): number {
  if (e.ctrlKey || e.altKey || e.metaKey || e.isComposing || e.key.length !== 1) return -1;
  const t = e.target as HTMLElement;
  if (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable) return -1;
  const i = e.key.toLowerCase().charCodeAt(0) - 97;
  return i >= 0 && i < 26 ? i : -1;
}

/**
 * Moves focus into the card so its keys work at once, unless the user is in the middle of typing a message
 * (a non-empty input) - their next keystroke must not land in the prompt.
 */
function focusUnlessTyping(card: HTMLElement | null): void {
  if (!card) return;
  const a = document.activeElement as HTMLElement | null;
  const field = a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.isContentEditable);
  const text = a ? (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement ? a.value : a.textContent) : '';
  if (field && (text ?? '').trim().length > 0) return;
  card.focus({ preventScroll: true });
}

export function BannerCard(props: {
  /** Extra CSS modifier, e.g. `permission`. */
  tone: string;
  role: 'dialog' | 'alertdialog' | 'alert';
  ariaLabel: string;
  icon: ComponentChildren;
  label: string;
  step?: StepNav;
  /** Esc (when the key press is not inside an IME composition). */
  onEscape?(): void;
  /** Enter while the card itself (not a button or field) has focus. */
  onEnter?(): void;
  /** Any other key press inside the card; call `preventDefault` when handled. */
  onKey?(e: KeyboardEvent): void;
  /** Focus the card on mount (see `focusUnlessTyping`). Off when a button inside takes the focus itself. */
  focusCard?: boolean;
  children: ComponentChildren;
  footer: ComponentChildren;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (props.focusCard !== false) focusUnlessTyping(ref.current);
  }, []);

  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Escape' && !e.isComposing && props.onEscape) {
      e.preventDefault();
      e.stopPropagation();
      props.onEscape();
      return;
    }
    if (e.key === 'Enter' && !e.isComposing && e.target === ref.current && props.onEnter) {
      e.preventDefault();
      props.onEnter();
      return;
    }
    props.onKey?.(e);
  };

  const step = props.step;
  return (
    <div ref={ref} class={`banner ${props.tone}`} role={props.role} aria-label={props.ariaLabel} tabIndex={-1} onKeyDown={onKeyDown}>
      <div class="banner-header">
        <span class="banner-header-icon">{props.icon}</span>
        <span class="banner-header-label">{props.label}</span>
        {step && step.count > 1 && (
          <span class="banner-step">
            <button type="button" aria-label="Previous question" disabled={step.index === 0} onClick={step.onPrev}>
              <UpIcon />
            </button>
            <span>{`${step.index + 1} of ${step.count}`}</span>
            <button type="button" aria-label="Next question" disabled={step.index >= step.count - 1} onClick={step.onNext}>
              <DownIcon />
            </button>
          </span>
        )}
      </div>
      <div class="banner-body">{props.children}</div>
      <div class="banner-footer">{props.footer}</div>
    </div>
  );
}

export interface Choice {
  id: string;
  label: string;
  description?: string | null;
  /** `reject` paints the label in the danger colour. */
  tone?: 'reject';
}

/** Rows with a lettered keycap (A, B, C, ...): a radio group, or checkboxes when `multi`. */
export function OptionList(props: {
  choices: readonly Choice[];
  selected: readonly string[];
  multi?: boolean;
  label?: string;
  onPick(id: string): void;
}) {
  return (
    <div class="opt-list" role={props.multi ? 'group' : 'radiogroup'} aria-label={props.label}>
      {props.choices.map((c, i) => {
        const on = props.selected.includes(c.id);
        return (
          <button
            key={c.id}
            type="button"
            role={props.multi ? 'checkbox' : 'radio'}
            aria-checked={on}
            class={`opt${on ? ' selected' : ''}${c.tone === 'reject' ? ' reject' : ''}`}
            title={c.description ?? undefined}
            onClick={() => props.onPick(c.id)}
          >
            <span class="opt-key" aria-hidden="true">
              {i < 26 ? String.fromCharCode(65 + i) : ''}
            </span>
            <span class="opt-text">
              <span class="opt-label">{c.label}</span>
              {c.description?.trim() && <span class="opt-desc">{c.description}</span>}
            </span>
          </button>
        );
      })}
    </div>
  );
}

export interface FooterAction {
  label: string;
  onClick(): void;
  disabled?: boolean;
}

/**
 * Quiet text action on the left (with an optional key hint), accent button on the right. `primaryHint`
 * `enter` draws the return glyph, `esc` the word Esc. `danger` colours the secondary action red.
 */
export function BannerFooter(props: {
  secondary?: FooterAction & { hint?: string; danger?: boolean };
  /** Extra neutral buttons between the two (e.g. "Open in browser"). */
  extra?: ComponentChildren;
  /** Omitted when there is nothing to confirm yet. */
  primary?: FooterAction & { hint?: 'enter' | 'esc'; autoFocus?: boolean };
}) {
  const primaryRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (props.primary?.autoFocus) primaryRef.current?.focus();
  }, []);

  const s = props.secondary;
  const p = props.primary;
  return (
    <>
      {s && (
        <button type="button" class={`banner-link${s.danger ? ' danger' : ''}`} disabled={s.disabled} onClick={s.onClick}>
          {s.label}
          {s.hint && <kbd>{s.hint}</kbd>}
        </button>
      )}
      {props.extra}
      {p && (
        <button ref={primaryRef} type="button" class="banner-btn primary" disabled={p.disabled} onClick={p.onClick}>
          {p.label}
          {p.hint === 'enter' && <ReturnIcon />}
          {p.hint === 'esc' && <kbd>Esc</kbd>}
        </button>
      )}
    </>
  );
}
