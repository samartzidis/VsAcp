import './theme.css';
import './styles.css';
import './messages/messages.css';
import { render } from 'preact';
import { App } from './App';
import { installDropHandling } from './composer/dropTarget';
import { installLinkHandling } from './messages/Overlays';
import { onHostMessage, post } from './bridge/host';
import { chatOf, panelFolders, setTabs } from './panel';
import { applyTheme } from './theme';
import { handFocusBackToComposer } from './banners/resolve';
import { log } from './log';
import type { ChatMessage, TabHostMessage } from './bridge/protocol';
import type { Chat } from './chat';

/** What a replace-all carried, for the log: how many messages and how long the last one is (not its text). */
function snapshotSummary(messages: readonly ChatMessage[]): Record<string, unknown> {
  const last = messages[messages.length - 1];
  return { count: messages.length, lastId: last?.id, lastLength: last?.content.length, hidden: document.hidden };
}

function handleTabMessage(chat: Chat, m: TabHostMessage): void {
  const store = chat.messages;
  switch (m.type) {
    case 'state':
      chat.composer.value = m.state;
      break;
    case 'composer.set':
      chat.editor.current?.setContent(m.text, m.commandName);
      break;
    case 'composer.insert':
      chat.editor.current?.insertText(m.text);
      break;
    case 'composer.focus':
      chat.editor.current?.focus();
      break;
    case 'banner.show':
      log.info('banner.show', { tab: chat.id, id: m.banner.id, kind: m.banner.kind, replaces: chat.banner.peek()?.id });
      handFocusBackToComposer(chat);
      chat.banner.value = m.banner;
      break;
    case 'banner.hide':
      log.info('banner.hide', { tab: chat.id, id: chat.banner.peek()?.id });
      handFocusBackToComposer(chat);
      chat.banner.value = null;
      break;
    case 'messages.add':
      store.addMessage(m.message);
      break;
    case 'messages.content':
      store.setContent(m.id, m.content);
      break;
    case 'messages.status':
      store.setStatus(m.id, m.status, m.expanderTitle);
      break;
    case 'messages.body':
      store.setBody(m.id, m.body, m.bodyMode, m.language);
      break;
    case 'messages.complete':
      store.completeMessage(m.id);
      break;
    case 'messages.clear':
      log.info('messages.clear', { tab: chat.id });
      store.clearMessages();
      break;
    case 'messages.load':
      log.info('messages.load', { tab: chat.id, ...snapshotSummary(m.messages) });
      store.replaceMessages(m.messages, true);
      break;
    case 'messages.sync':
      log.info('messages.sync', { tab: chat.id, syncId: m.syncId, ...snapshotSummary(m.messages) });
      store.replaceMessages(m.messages, false);
      chat.post({ type: 'messages.synced', syncId: m.syncId });
      break;
  }
}

onHostMessage((m) => {
  switch (m.type) {
    case 'theme':
      applyTheme(m);
      return;
    case 'tabs':
      log.info('tabs', { count: m.tabs.length, open: m.tabs.filter((t) => t.open).length });
      setTabs(m.tabs);
      return;
    case 'panel':
      panelFolders.value = m.folders;
      return;
  }

  const chat = chatOf(m.tab);
  if (!chat) {
    // a message for a tab the page no longer (or not yet) has: the tab's own `ready` gets it a fresh snapshot
    log.warn('message for an unknown tab', { type: m.type, tab: m.tab });
    return;
  }
  handleTabMessage(chat, m);
});

render(<App />, document.getElementById('spa-root')!);
installDropHandling();
installLinkHandling();

// VS hides the page while the tool window is not shown; record it, since a hidden page runs no animation frames.
document.addEventListener('visibilitychange', () => log.debug('visibility', { hidden: document.hidden }));

// When the page gains and loses the keyboard focus (and to what), so a focus that is taken away right after a click
// (ADR-057) shows in the VsAcp log next to the host's own focus lines. On gaining it the host is told: a click into the
// page goes to Chromium's window, so VS does not know its chat window is the one in use (see VsAcpPackage.ActivateChatWindow).
const focusTarget = () => (document.activeElement as HTMLElement | null)?.className || document.activeElement?.tagName || null;
window.addEventListener('focus', () => {
  log.debug('page.focus', { target: focusTarget() });
  post({ type: 'page.focused' });
});
window.addEventListener('blur', () => log.debug('page.blur', { target: focusTarget() }));

// Loaded: the host answers with the theme and the tabs; each open tab's view then asks for its own state.
log.info('page.ready', { hidden: document.hidden });
post({ type: 'page.ready' });
