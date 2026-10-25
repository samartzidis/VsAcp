import { signal } from '@preact/signals';
import { useRef, useState } from 'preact/hooks';
import { post } from '../bridge/host';
import type { ChatMessage, MessageStatus } from '../bridge/protocol';
import { copyToClipboard } from './clipboard';
import { CheckIcon, CopyIcon, RevertIcon, ToolIcon } from './icons';
import { parseToolTitle, toolIconKind } from './render';
import { RichContent } from './RichContent';
import { messageSignal } from './store';

/** Source of the image shown full-size over the list, or null. */
export const lightboxSrc = signal<string | null>(null);

const STATUS_SYMBOLS: Record<MessageStatus, string> = { Pending: '○', Success: '✓', Error: '✗', Info: '○', Warning: '!' };
const STATUS_COLORS: Record<MessageStatus, string> = {
  Pending: 'var(--info)',
  Success: 'var(--success)',
  Error: 'var(--error)',
  Info: 'var(--info)',
  Warning: 'var(--warning)',
};

function Status({ status, class: cls }: { status: MessageStatus; class: string }) {
  return (
    <span class={`${cls} msg-status`} style={{ color: STATUS_COLORS[status] ?? STATUS_COLORS.Pending }}>
      {STATUS_SYMBOLS[status] ?? STATUS_SYMBOLS.Pending}
    </span>
  );
}

function CopyButton({ getText }: { getText: () => string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      class={copied ? 'msg-copy-btn copied' : 'msg-copy-btn'}
      title="Copy to clipboard"
      aria-label="Copy to clipboard"
      onClick={(e) => {
        e.stopPropagation();
        const text = getText();
        if (!text) return;
        copyToClipboard(text);
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      }}
    >
      {copied ? <CheckIcon /> : <CopyIcon />}
    </button>
  );
}

function Chips({ items, glyph = '📄 ' }: { items: { text: string; title?: string }[]; glyph?: string }) {
  return (
    <div class="user-strip">
      {items.map((c, i) => (
        <span key={i} class="user-chip" title={c.title}>
          {glyph + c.text}
        </span>
      ))}
    </div>
  );
}

function UserMessage({ m }: { m: ChatMessage }) {
  const content = useRef<HTMLDivElement>(null);
  return (
    <div id={'msg-' + m.id} class="msg msg-user" data-type="User">
      {m.imageDataUris && m.imageDataUris.length > 0 && (
        <div class="user-strip">
          {m.imageDataUris.map((uri, i) => (
            <img key={i} class="user-img" src={uri} alt="attached image" onClick={() => (lightboxSrc.value = uri)} />
          ))}
        </div>
      )}
      {m.fileNames && m.fileNames.length > 0 && <Chips items={m.fileNames.map((text) => ({ text }))} />}
      {m.folderNames && m.folderNames.length > 0 && (
        <Chips items={m.folderNames.map((text) => ({ text }))} glyph="📁 " />
      )}
      {m.selections && m.selections.length > 0 && (
        <Chips items={m.selections.map((s) => ({ text: s.label, title: s.tooltip }))} />
      )}
      <RichContent kind="text" text={m.content} class="md-content msg-content user-text" elRef={content} />
      <CopyButton getText={() => content.current?.innerText ?? ''} />
      {m.checkpointId && (
        <button
          type="button"
          class="msg-revert-btn"
          title="Revert to here"
          aria-label="Revert to here"
          onClick={(e) => {
            e.stopPropagation();
            post({ type: 'revert', checkpointId: m.checkpointId! });
          }}
        >
          <RevertIcon />
        </button>
      )}
    </div>
  );
}

function AssistantMessage({ m }: { m: ChatMessage }) {
  const content = useRef<HTMLDivElement>(null);
  return (
    <div id={'msg-' + m.id} class="msg msg-assistant" data-type="Assistant">
      <div class="msg-assistant-inner">
        <RichContent
          kind="markdown"
          text={m.content}
          class={m.isStreaming ? 'md-content msg-content streaming-cursor' : 'md-content msg-content'}
          elRef={content}
        />
      </div>
      <CopyButton getText={() => content.current?.innerText ?? ''} />
    </div>
  );
}

/**
 * The expandable pill of a tool call. A compaction reuses it: its title is a sentence, not "tool detail",
 * so it is shown whole, and the summary is the expandable body.
 */
function ToolStepMessage({ m, compaction }: { m: ChatMessage; compaction?: boolean }) {
  // the pill shows the most specific title the host has given the step so far
  const { name, detail } = compaction
    ? { name: m.expanderTitle || m.title || 'Context compaction', detail: '' }
    : parseToolTitle(m.expanderTitle || m.toolName || m.title || 'Tool');
  const [expanded, setExpanded] = useState(false);
  const body = useRef<HTMLDivElement>(null);
  const hasBody = !!m.body?.trim();
  const showBody = expanded && hasBody;

  return (
    <div id={'msg-' + m.id} class="msg msg-toolstep" data-type={compaction ? 'Compaction' : 'ToolStep'}>
      <div
        class="toolstep-header"
        role="button"
        aria-expanded={showBody}
        // a step without output has nothing to expand
        onClick={() => hasBody && setExpanded(!expanded)}
      >
        <span class="toolstep-icon">
          <ToolIcon kind={toolIconKind(name)} />
        </span>
        <Status status={m.status} class="toolstep-status" />
        <span class="toolstep-name">{name}</span>
        {detail && <span class="toolstep-title">{detail}</span>}
        <span class={showBody ? 'toolstep-arrow expanded' : 'toolstep-arrow'}>{'▶'}</span>
        <CopyButton
          getText={() => [name, detail, showBody ? (body.current?.innerText ?? '') : ''].filter(Boolean).join('\n')}
        />
      </div>
      {hasBody && (
        <RichContent
          kind={m.bodyMode === 'Html' ? 'html' : m.bodyMode === 'PlainText' ? 'code' : 'markdown'}
          text={m.body!}
          language={m.language}
          class="md-content toolstep-body msg-body"
          style={{ display: showBody ? '' : 'none' }}
          elRef={body}
        />
      )}
      {/* only a finished compaction changed the agent's context; a failed or cancelled one left it as it was */}
      {compaction && m.status === 'Success' && (
        <div class="compaction-hint">Messages above are no longer in the agent's context</div>
      )}
    </div>
  );
}

/** A live agent notice: severity symbol, title and optional detail. Both are agent text, so shown as plain text. */
function NoticeMessage({ m }: { m: ChatMessage }) {
  const severity = m.status === 'Error' ? 'error' : m.status === 'Warning' ? 'warning' : 'info';
  const text = useRef<HTMLDivElement>(null);
  return (
    <div id={'msg-' + m.id} class={`msg msg-notice msg-notice-${severity}`} data-type="Notice" data-severity={severity}>
      <Status status={m.status} class="notice-status" />
      <div class="notice-text" ref={text}>
        <div class="notice-title">{m.title}</div>
        {m.body?.trim() && <div class="notice-detail">{m.body}</div>}
      </div>
      <CopyButton getText={() => text.current?.innerText ?? ''} />
    </div>
  );
}

function ThinkingMessage({ m }: { m: ChatMessage }) {
  const [expanded, setExpanded] = useState(false);
  return (
    <div id={'msg-' + m.id} class="msg msg-thinking" data-type="Thinking">
      <div class="thinking-header" role="button" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>
        <span class={expanded ? 'thinking-arrow expanded' : 'thinking-arrow'}>{'▶'}</span>
        <Status status={m.status} class="thinking-status" />
        <span class="thinking-title msg-expander-title">{m.expanderTitle || 'Thinking...'}</span>
      </div>
      <div class={expanded ? 'thinking-content visible' : 'thinking-content'}>{m.content}</div>
      <CopyButton getText={() => m.content} />
    </div>
  );
}

export function MessageView({ id }: { id: string }) {
  const m = messageSignal(id)?.value;
  if (!m) return null;
  switch (m.type) {
    case 'User':
      return <UserMessage m={m} />;
    case 'Assistant':
      return <AssistantMessage m={m} />;
    case 'ToolStep':
      return <ToolStepMessage m={m} />;
    case 'Thinking':
      return <ThinkingMessage m={m} />;
    case 'Notice':
      return <NoticeMessage m={m} />;
    case 'Compaction':
      return <ToolStepMessage m={m} compaction />;
    default:
      return null;
  }
}
