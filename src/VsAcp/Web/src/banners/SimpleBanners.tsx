import { useState } from 'preact/hooks';
import type { Banner } from '../bridge/protocol';
import { useResolveBanner } from './resolve';
import { BannerCard, BannerFooter, OptionList, WarningIcon, letterIndex, type Choice } from './card';
import { ShieldIcon } from '../composer/icons';

type Of<K extends Banner['kind']> = Extract<Banner, { kind: K }>;

const isReject = (kind: string) => kind.toLowerCase().startsWith('reject');

/**
 * The agent asks permission (run a command, change a file, ...).
 *
 * - One or two options (typically Yes / No): plain footer buttons, one click answers; the quiet left action is
 *   the reject option (Esc).
 * - Three or more (Allow once / Allow always / Reject ...): lettered rows. A click answers at once; a letter
 *   only selects the row and Enter confirms it, so a stray key press while the user types never grants anything.
 *   The reject option stays the quiet left action (Esc) and is not repeated as a row.
 *
 * Enter alone never answers a permission prompt.
 */
export function PermissionBanner({ banner }: { banner: Of<'permission'> }) {
  const resolveBanner = useResolveBanner();
  const [selected, setSelected] = useState<string | null>(null);
  const options = banner.options;
  const answer = (optionId: string) => resolveBanner(banner.id, { optionId });

  // Esc / the left-hand action: prefer "reject once", else any reject option
  const dismiss = options.find((o) => o.kind.toLowerCase() === 'reject_once') ?? options.find((o) => isReject(o.kind));
  const simple = options.length <= 2;

  if (simple) {
    // with two options and no reject among them, the first one is the quiet action (but not bound to Esc)
    const left = dismiss ?? (options.length === 2 ? options[0] : undefined);
    const right = options.find((o) => o !== left);
    return (
      <BannerCard
        tone="permission"
        role="alertdialog"
        ariaLabel={banner.title}
        icon={<ShieldIcon />}
        label="Permission"
        onEscape={dismiss ? () => answer(dismiss.optionId) : undefined}
        footer={
          <BannerFooter
            secondary={left ? { label: left.name, hint: left === dismiss ? 'Esc' : undefined, onClick: () => answer(left.optionId) } : undefined}
            primary={right ? { label: right.name, onClick: () => answer(right.optionId) } : undefined}
          />
        }
      >
        <div class="banner-title">{banner.title}</div>
      </BannerCard>
    );
  }

  const rows = options.filter((o) => o !== dismiss);
  const choices: Choice[] = rows.map((o) => ({ id: o.optionId, label: o.name, tone: isReject(o.kind) ? 'reject' : undefined }));
  const confirm = () => selected && answer(selected);

  return (
    <BannerCard
      tone="permission"
      role="alertdialog"
      ariaLabel={banner.title}
      icon={<ShieldIcon />}
      label="Permission"
      onEscape={dismiss ? () => answer(dismiss.optionId) : undefined}
      onEnter={confirm}
      onKey={(e) => {
        const i = letterIndex(e);
        if (i >= 0 && i < rows.length) {
          e.preventDefault();
          setSelected(rows[i]!.optionId);
        }
      }}
      footer={
        <BannerFooter
          secondary={dismiss ? { label: dismiss.name, hint: 'Esc', onClick: () => answer(dismiss.optionId) } : undefined}
          // nothing to confirm until a row was picked with its letter
          primary={selected ? { label: 'Continue', hint: 'enter', onClick: confirm } : undefined}
        />
      }
    >
      <div class="banner-title">{banner.title}</div>
      <OptionList choices={choices} selected={selected ? [selected] : []} onPick={answer} />
    </BannerCard>
  );
}

/** The chat view could not be updated; reloading rebuilds it from the session (no data is touched). */
export function ErrorBanner({ banner }: { banner: Of<'error'> }) {
  const resolveBanner = useResolveBanner();
  const reload = () => resolveBanner(banner.id, {});
  return (
    <BannerCard
      tone="error"
      role="alert"
      ariaLabel="Chat view problem"
      icon={<WarningIcon />}
      label="Chat view"
      onEnter={reload}
      footer={<BannerFooter primary={{ label: 'Reload view', hint: 'enter', onClick: reload }} />}
    >
      <div class="banner-text">{banner.message}</div>
    </BannerCard>
  );
}
