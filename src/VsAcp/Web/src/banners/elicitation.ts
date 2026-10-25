// Form logic for elicitation banners: initial values, validation and the answer sent back to the host.
// A port of the readers in the previous WPF ChatBannerBuilder (same rules and messages); kept free of
// Preact so it can be unit-tested.

import type { ElicitationField } from '../bridge/protocol';

/** What the UI holds per field: text/number input, checkbox, single choice ('' = none) or several choices. */
export type FieldValue = string | boolean | string[];
export type FormState = Record<string, FieldValue>;

export interface ReadResult {
  /** The JSON value to send; `undefined` means the optional field was left empty and is omitted. */
  value?: unknown;
  error?: string;
}

/** One step of the form: a field and, for a choice question, its free-text "Other" companion. */
export interface FieldStep {
  field: ElicitationField;
  other?: ElicitationField;
}

const CUSTOM_SUFFIX = '_custom';

/**
 * Groups the form's fields into steps. Agents (claude-agent-acp) send the free-text "Other" of a choice question
 * as a separate property named `<question>_custom`; it is folded into the step of its question instead of being a
 * step of its own. Both stay separate fields (and separate answer keys). Without a matching choice question it is
 * an ordinary step.
 */
export function groupSteps(fields: readonly ElicitationField[]): FieldStep[] {
  const isChoice = (f: ElicitationField | undefined) => f !== undefined && (f.kind === 'select' || f.kind === 'multiSelect');
  const baseOf = (f: ElicitationField) =>
    f.kind === 'text' && f.name.length > CUSTOM_SUFFIX.length && f.name.endsWith(CUSTOM_SUFFIX)
      ? fields.find((b) => b.name === f.name.slice(0, -CUSTOM_SUFFIX.length) && isChoice(b))
      : undefined;

  const steps: FieldStep[] = [];
  for (const f of fields) {
    if (baseOf(f)) continue;
    const other = fields.find((o) => o !== f && baseOf(o) === f);
    steps.push(other ? { field: f, other } : { field: f });
  }
  return steps;
}

export function fieldLabel(f: ElicitationField): string {
  return (f.title || f.name) + (f.required ? ' *' : '');
}

export function initialValue(f: ElicitationField): FieldValue {
  switch (f.kind) {
    case 'boolean':
      return f.defaultBoolean ?? false;
    case 'select': {
      const options = f.options ?? [];
      if (f.defaultText != null && options.some((o) => o.value === f.defaultText)) return f.defaultText;
      return f.required && options.length > 0 ? options[0]!.value : '';
    }
    case 'multiSelect':
      return [...(f.defaultValues ?? [])];
    default:
      return f.defaultText ?? (f.defaultNumber != null ? String(f.defaultNumber) : '');
  }
}

export function initialState(fields: readonly ElicitationField[]): FormState {
  return Object.fromEntries(fields.map((f) => [f.name, initialValue(f)]));
}

const INTEGER = /^[+-]?\d+$/;
const NUMBER = /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/;

function readText(f: ElicitationField, raw: string): ReadResult {
  const text = raw.trim();
  if (text.length === 0) return f.required ? { error: 'this field is required' } : {};

  if (f.kind === 'integer') {
    const n = Number(text);
    if (!INTEGER.test(text) || !Number.isSafeInteger(n)) return { error: 'enter a whole number' };
    return bounds(f, n) ?? { value: n };
  }
  if (f.kind === 'number') {
    const n = Number(text);
    if (!NUMBER.test(text) || !Number.isFinite(n)) return { error: 'enter a number' };
    return bounds(f, n) ?? { value: n };
  }

  if (f.minLength != null && text.length < f.minLength) return { error: `at least ${f.minLength} characters` };
  if (f.maxLength != null && text.length > f.maxLength) return { error: `at most ${f.maxLength} characters` };
  if (f.pattern != null) {
    try {
      if (!new RegExp(f.pattern).test(text)) return { error: 'does not match the expected format' };
    } catch {
      // an invalid pattern from the agent is accepted as-is
    }
  }
  return { value: text };
}

function bounds(f: ElicitationField, n: number): ReadResult | null {
  if (f.minimum != null && n < f.minimum) return { error: `must be at least ${f.minimum}` };
  if (f.maximum != null && n > f.maximum) return { error: `must be at most ${f.maximum}` };
  return null;
}

export function readField(f: ElicitationField, raw: FieldValue | undefined): ReadResult {
  switch (f.kind) {
    case 'boolean':
      return { value: raw === true };

    case 'select':
      if (typeof raw === 'string' && raw.length > 0) return { value: raw };
      return f.required ? { error: 'please choose an option' } : {};

    case 'multiSelect': {
      const chosen = new Set(Array.isArray(raw) ? raw : []);
      // keep the order the options are listed in
      const selected = (f.options ?? []).filter((o) => chosen.has(o.value)).map((o) => o.value);
      if (selected.length === 0 && !f.required && f.minItems == null) return {};
      if (f.minItems != null && selected.length < f.minItems) return { error: `choose at least ${f.minItems}` };
      if (f.maxItems != null && selected.length > f.maxItems) return { error: `choose at most ${f.maxItems}` };
      if (selected.length === 0 && f.required) return { error: 'please choose at least one option' };
      return { value: selected };
    }

    default:
      return readText(f, typeof raw === 'string' ? raw : '');
  }
}

/** Validates the whole form: the first problem wins ("Title: message"), otherwise the answer values. */
export function readForm(
  fields: readonly ElicitationField[],
  state: FormState,
): { values: Record<string, unknown> } | { error: string } {
  const values: Record<string, unknown> = {};
  for (const f of fields) {
    const r = readField(f, state[f.name]);
    if (r.error !== undefined) return { error: `${f.title || f.name}: ${r.error}` };
    if (r.value !== undefined) values[f.name] = r.value;
  }
  return { values };
}
