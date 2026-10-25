import { useState } from 'preact/hooks';
import type { Banner, RevertGroup } from '../bridge/protocol';
import { ChevronIcon } from '../composer/icons';
import { resolveBanner } from './resolve';
import { BannerCard, BannerFooter, WarningIcon } from './card';

const LABEL: Record<RevertGroup['files'][number]['action'], string> = { delete: 'DELETE', overwrite: 'OVERWRITE', recreate: 'RECREATE' };

function total(g: RevertGroup): number {
  return g.deleted + g.overwritten + g.recreated;
}

function summary(g: RevertGroup): string {
  const parts: string[] = [];
  if (g.deleted > 0) parts.push(`${g.deleted} deleted`);
  if (g.overwritten > 0) parts.push(`${g.overwritten} overwritten`);
  if (g.recreated > 0) parts.push(`${g.recreated} recreated`);
  const n = total(g);
  return `${n} file${n === 1 ? '' : 's'}: ${parts.join(', ')}`;
}

function FileList({ group }: { group: RevertGroup }) {
  return (
    <div class="revert-files">
      {group.files.map((f) => (
        <div key={f.action + f.path} class="revert-row">
          <span class={`revert-action ${f.action}`}>{LABEL[f.action]}</span>
          <span class="revert-path">{f.path}</span>
        </div>
      ))}
      {group.omitted > 0 && <div class="revert-more">{`… and ${group.omitted} more`}</div>}
    </div>
  );
}

/**
 * Confirmation before "Revert to here" changes files on disk. The agent's changes are always reverted; the other
 * changes (your own edits, other chats, build output, shell changes the agent did not report) only when "Also
 * revert other changes" is ticked. Cancel is focused, so Enter / Esc are safe.
 */
export function RevertBanner({ banner }: { banner: Extract<Banner, { kind: 'revert' }> }) {
  const preview = banner.preview;
  const [includeOther, setIncludeOther] = useState(false);
  const [otherOpen, setOtherOpen] = useState(false);
  const cancel = () => resolveBanner(banner.id, { confirmed: false });

  const agent = preview?.agent;
  const other = preview?.agentFilesKnown ? preview.other : null;
  const agentChanges = agent !== undefined && total(agent) > 0;

  let title: string;
  let text: string;
  if (!preview) {
    title = 'Revert to here?';
    text =
      'The files that will change could not be determined. Reverting restores your workspace to how it was before this message and discards the chat after it. This cannot be undone.';
  } else if (!preview.agentFilesKnown) {
    title = 'Reverting will change files on disk';
    text = 'Your workspace will be restored to how it was before this message, and the chat after it will be discarded. This cannot be undone.';
  } else if (agentChanges) {
    title = 'Reverting will change files on disk';
    text = "The agent's changes after this message will be undone, and the chat after it will be discarded. This cannot be undone.";
  } else {
    title = 'No file changes from the agent to revert';
    text = 'The chat after this message will be discarded. This cannot be undone.';
  }

  return (
    <BannerCard
      tone="revert"
      role="alertdialog"
      ariaLabel="Revert to here"
      icon={<WarningIcon />}
      label="Revert"
      focusCard={false}
      onEscape={cancel}
      footer={
        <BannerFooter
          secondary={{
            label: 'Revert',
            danger: true,
            onClick: () => resolveBanner(banner.id, other ? { confirmed: true, includeOther } : { confirmed: true }),
          }}
          primary={{ label: 'Cancel', hint: 'esc', autoFocus: true, onClick: cancel }}
        />
      }
    >
      <div class="banner-title">{title}</div>
      <div class="banner-text">{text}</div>
      {agent && agentChanges && (
        <>
          <div class="banner-strong">{summary(agent)}</div>
          <FileList group={agent} />
        </>
      )}
      {other && (
        <div class="revert-other">
          <div class="revert-other-head">
            <button
              type="button"
              class="revert-other-toggle"
              aria-expanded={otherOpen}
              onClick={() => setOtherOpen(!otherOpen)}
            >
              <span class={otherOpen ? 'pending-chevron' : 'pending-chevron collapsed'}>
                <ChevronIcon />
              </span>
              {`Other changes (${total(other)})`}
            </button>
            <label class="revert-other-check">
              <input type="checkbox" checked={includeOther} onChange={(e) => setIncludeOther(e.currentTarget.checked)} />
              Also revert other changes
            </label>
          </div>
          <div class="revert-other-text">
            Your own edits, other chats, build output and shell changes the agent did not report. Kept unless you tick
            the box.
          </div>
          {otherOpen && <FileList group={other} />}
        </div>
      )}
    </BannerCard>
  );
}
