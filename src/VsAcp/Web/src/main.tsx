import './theme.css';
import './styles.css';
import './messages/messages.css';
import { render } from 'preact';
import { App } from './App';
import { installDropHandling } from './composer/dropTarget';
import { installLinkHandling } from './messages/Overlays';
import { addMessage, clearMessages, completeMessage, replaceMessages, setBody, setContent, setStatus } from './messages/store';
import { onHostMessage, post } from './bridge/host';
import { banner, composer, editor } from './state';
import { applyTheme } from './theme';
import { handFocusBackToComposer } from './banners/resolve';
import { log } from './log';
import type { ChatMessage } from './bridge/protocol';

/** What a replace-all carried, for the log: how many messages and how long the last one is (not its text). */
function snapshotSummary(messages: readonly ChatMessage[]): Record<string, unknown> {
  const last = messages[messages.length - 1];
  return { count: messages.length, lastId: last?.id, lastLength: last?.content.length, hidden: document.hidden };
}

onHostMessage((m) => {
  switch (m.type) {
    case 'state':
      composer.value = m.state;
      break;
    case 'composer.set':
      editor.current?.setContent(m.text, m.commandName);
      break;
    case 'composer.insert':
      editor.current?.insertText(m.text);
      break;
    case 'composer.focus':
      editor.current?.focus();
      break;
    case 'banner.show':
      log.info('banner.show', { id: m.banner.id, kind: m.banner.kind, replaces: banner.peek()?.id });
      handFocusBackToComposer();
      banner.value = m.banner;
      break;
    case 'banner.hide':
      log.info('banner.hide', { id: banner.peek()?.id });
      handFocusBackToComposer();
      banner.value = null;
      break;
    case 'theme':
      applyTheme(m);
      break;
    case 'messages.add':
      addMessage(m.message);
      break;
    case 'messages.content':
      setContent(m.id, m.content);
      break;
    case 'messages.status':
      setStatus(m.id, m.status, m.expanderTitle);
      break;
    case 'messages.body':
      setBody(m.id, m.body, m.bodyMode, m.language);
      break;
    case 'messages.complete':
      completeMessage(m.id);
      break;
    case 'messages.clear':
      log.info('messages.clear');
      clearMessages();
      break;
    case 'messages.load':
      log.info('messages.load', snapshotSummary(m.messages));
      replaceMessages(m.messages, true);
      break;
    case 'messages.sync':
      log.info('messages.sync', { syncId: m.syncId, ...snapshotSummary(m.messages) });
      replaceMessages(m.messages, false);
      post({ type: 'messages.synced', syncId: m.syncId });
      break;
  }
});

render(<App />, document.getElementById('spa-root')!);
installDropHandling();
installLinkHandling();

// The host collapses the page of a tab that is not shown; record it, since a hidden page runs no animation frames.
document.addEventListener('visibilitychange', () => log.debug('visibility', { hidden: document.hidden }));

// Mounted: ask the host for the current snapshot and text (it may have changed before the page loaded).
log.info('ready', { hidden: document.hidden });
post({ type: 'ready' });
