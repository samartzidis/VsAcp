import { useEffect, useRef, useState } from 'preact/hooks';
import { post } from '../bridge/host';
import type { Banner, ElicitationField } from '../bridge/protocol';
import { resolveBanner } from './resolve';
import { fieldLabel, groupSteps, initialState, readField, type FieldValue, type FormState } from './elicitation';
import { BannerCard, BannerFooter, HelpIcon, OptionList, letterIndex, type Choice } from './card';

type ElicitationBanner = Extract<Banner, { kind: 'elicitation' }>;

/**
 * The agent needs structured input. A form is shown one field per step (stepper `1 of N` when there are several;
 * Continue validates the field and moves on, the last step is Submit; Esc / Decline declines). A URL request
 * shows the link with Open in browser / Done / Cancel.
 */
export function ElicitationBanner({ banner }: { banner: ElicitationBanner }) {
  const [state, setState] = useState<FormState>(() => initialState(banner.fields));
  const [step, setStep] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const urlMode = banner.url !== null;
  const fields = banner.fields;
  // a choice question's "<name>_custom" free-text field is shown on the question's own step
  const steps = groupSteps(fields);
  const count = Math.max(steps.length, 1);
  const field: ElicitationField | undefined = steps[step]?.field;
  const other: ElicitationField | undefined = steps[step]?.other;
  const last = step >= count - 1;
  const title = banner.title ?? 'The agent needs your input';

  const set = (name: string, value: FieldValue) => {
    setState((s) => ({ ...s, [name]: value }));
    setError(null);
  };
  const decline = () => resolveBanner(banner.id, { action: 'decline' });

  /** Validates every field; the first problem selects its step and shows its message. */
  const submit = () => {
    const values: Record<string, unknown> = {};
    for (let i = 0; i < fields.length; i++) {
      const f = fields[i]!;
      const r = readField(f, state[f.name]);
      if (r.error !== undefined) {
        setStep(Math.max(0, steps.findIndex((s) => s.field === f || s.other === f)));
        setError(`${f.title || f.name}: ${r.error}`);
        return;
      }
      if (r.value !== undefined) values[f.name] = r.value;
    }
    resolveBanner(banner.id, { action: 'accept', values });
  };

  /** Validates the current step's field(s), then goes to the next step (or submits on the last one). */
  const next = () => {
    if (!field) return submit();
    for (const f of other ? [field, other] : [field]) {
      const r = readField(f, state[f.name]);
      if (r.error !== undefined) {
        setError(`${f.title || f.name}: ${r.error}`);
        return;
      }
    }
    if (last) return submit();
    setStep(step + 1);
    setError(null);
  };
  const prev = () => {
    if (step === 0) return;
    setStep(step - 1);
    setError(null);
  };

  if (urlMode) {
    return (
      <BannerCard
        tone="elicitation"
        role="dialog"
        ariaLabel={title}
        icon={<HelpIcon />}
        label="Question"
        onEscape={decline}
        onEnter={() => resolveBanner(banner.id, { action: 'accept' })}
        footer={
          <BannerFooter
            secondary={{ label: 'Cancel', hint: 'Esc', onClick: decline }}
            extra={
              <button type="button" class="banner-btn ghost" onClick={() => post({ type: 'banner.openUrl', id: banner.id })}>
                Open in browser
              </button>
            }
            primary={{ label: 'Done', hint: 'enter', onClick: () => resolveBanner(banner.id, { action: 'accept' }) }}
          />
        }
      >
        <div class="banner-title">{title}</div>
        <div class="banner-text">{banner.message}</div>
        {banner.description?.trim() && <div class="banner-muted">{banner.description}</div>}
        <div class="banner-muted">{banner.url}</div>
      </BannerCard>
    );
  }

  const choices = field ? choicesOf(field) : [];
  const picked = field ? pickedOf(field, state[field.name]) : [];
  const pick = (id: string) => {
    if (!field) return;
    if (field.kind === 'boolean') set(field.name, id === 'yes');
    else if (field.kind === 'multiSelect') {
      const chosen = Array.isArray(state[field.name]) ? (state[field.name] as string[]) : [];
      set(field.name, chosen.includes(id) ? chosen.filter((v) => v !== id) : [...chosen, id]);
    } else set(field.name, picked.includes(id) && !field.required ? '' : id);
  };

  return (
    <BannerCard
      tone="elicitation"
      role="dialog"
      ariaLabel={title}
      icon={<HelpIcon />}
      label={count > 1 ? 'Questions' : 'Question'}
      step={{ index: step, count, onPrev: prev, onNext: next }}
      onEscape={decline}
      onEnter={next}
      onKey={(e) => {
        const t = e.target as HTMLElement;
        // Enter in a single-line field continues (Enter in a text area still adds a line)
        if (e.key === 'Enter' && !e.isComposing && t.tagName === 'INPUT' && (t as HTMLInputElement).type === 'text') {
          e.preventDefault();
          next();
          return;
        }
        const i = letterIndex(e);
        if (i >= 0 && i < choices.length) {
          e.preventDefault();
          pick(choices[i]!.id);
        }
      }}
      footer={
        <BannerFooter
          secondary={{ label: 'Decline', hint: 'Esc', onClick: decline }}
          primary={{ label: last ? 'Submit' : 'Continue', hint: 'enter', onClick: next }}
        />
      }
    >
      {step === 0 && (
        <>
          <div class="banner-title">{title}</div>
          <div class="banner-text">{banner.message}</div>
          {banner.description?.trim() && <div class="banner-muted">{banner.description}</div>}
        </>
      )}
      {field && (
        <div class="elicit-field" key={field.name}>
          <label class="elicit-label" for={`f-${field.name}`}>
            {(count > 1 ? `${step + 1}. ` : '') + fieldLabel(field)}
          </label>
          {field.description?.trim() && <div class="banner-muted">{field.description}</div>}
          {isChoice(field) ? (
            <OptionList
              choices={choices}
              selected={picked}
              multi={field.kind === 'multiSelect'}
              label={fieldLabel(field)}
              onPick={pick}
            />
          ) : (
            <TextEditor field={field} focus={step > 0} value={state[field.name]} onChange={(v) => set(field.name, v)} />
          )}
          {other && (
            <div class="elicit-other">
              <label class="elicit-label" for={`f-${other.name}`}>
                {fieldLabel(other)}
              </label>
              <TextEditor field={other} single focus={false} placeholder={other.description ?? undefined} value={state[other.name]} onChange={(v) => set(other.name, v)} />
            </div>
          )}
        </div>
      )}
      {error && (
        <div class="banner-error" role="alert">
          {error}
        </div>
      )}
    </BannerCard>
  );
}

const isChoice = (f: ElicitationField) => f.kind === 'boolean' || f.kind === 'select' || f.kind === 'multiSelect';

/** Option rows for fields that are a choice; none for free text / numbers. */
function choicesOf(f: ElicitationField): Choice[] {
  if (f.kind === 'boolean') {
    return [
      { id: 'yes', label: 'Yes' },
      { id: 'no', label: 'No' },
    ];
  }
  if (f.kind === 'select' || f.kind === 'multiSelect') {
    return (f.options ?? []).map((o) => ({ id: o.value, label: o.title, description: o.description }));
  }
  return [];
}

function pickedOf(f: ElicitationField, v: FieldValue | undefined): string[] {
  if (f.kind === 'boolean') return [v === true ? 'yes' : 'no'];
  if (f.kind === 'multiSelect') return Array.isArray(v) ? v : [];
  return typeof v === 'string' && v.length > 0 ? [v] : [];
}

function TextEditor(props: {
  field: ElicitationField;
  focus: boolean;
  /** Always one line (the "Other" box under a question). */
  single?: boolean;
  placeholder?: string;
  value: FieldValue | undefined;
  onChange(v: FieldValue): void;
}) {
  const { field: f, value, onChange } = props;
  const id = `f-${f.name}`;
  const text = typeof value === 'string' ? value : '';
  // Focus only after the user moved to this step: the first step must not take the focus (and the keystrokes)
  // from a message being typed in the composer.
  const ref = useRef<HTMLInputElement & HTMLTextAreaElement>(null);
  useEffect(() => {
    if (props.focus) ref.current?.focus();
  }, []);
  // long free text gets a multi-line box; everything else a single line (Enter in it continues)
  if (!props.single && f.kind === 'text' && (f.maxLength == null || f.maxLength > 200)) {
    return <textarea ref={ref} id={id} class="elicit-input" rows={3} value={text} title={f.format ?? undefined} placeholder={props.placeholder} onInput={(e) => onChange(e.currentTarget.value)} />;
  }
  return <input ref={ref} id={id} type="text" class="elicit-input" value={text} title={f.format ?? undefined} placeholder={props.placeholder} onInput={(e) => onChange(e.currentTarget.value)} />;
}
