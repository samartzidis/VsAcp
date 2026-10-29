import { useEffect, useRef } from 'preact/hooks';
import { BannerHost } from './banners/BannerHost';
import { ChatContext, useChat, type Chat } from './chat';
import { MessageList } from './messages/MessageList';
import { ContextMenu, Lightbox } from './messages/Overlays';
import { Composer } from './composer/Composer';
import { BusyBar, PendingChangesBar } from './status/StatusBars';
import { activeChat, activeTabId, openChats } from './panel';
import { PanelFooter } from './shell/PanelFooter';
import { TabStrip } from './shell/TabStrip';
import { log } from './log';

/**
 * One chat, top to bottom: message list, banner, busy line, pending-changes bar, composer. Mounted while its tab is
 * open and only hidden while another tab is shown, so its scroll position, unsent text and open banner survive.
 */
function ChatView(props: { active: boolean }) {
  const chat = useChat();
  const root = useRef<HTMLDivElement>(null);

  useEffect(() => {
    chat.root.current = root.current;
    // Mounted: ask the host for this chat's snapshot and text (they may have changed before the view existed).
    log.info('ready', { tab: chat.id, hidden: document.hidden });
    chat.post({ type: 'ready' });
    return () => {
      chat.root.current = null;
    };
  }, [chat]);

  return (
    <div class="chat-view" ref={root} hidden={!props.active}>
      <MessageList />
      <BannerHost />
      <BusyBar />
      <PendingChangesBar />
      <Composer />
    </div>
  );
}

/**
 * The whole tool window: the tab strip, a chat view per open tab (the active one shown), the footer with the
 * settings and the shared folders, and the page-wide overlays.
 */
export function App() {
  const active = activeTabId.value;
  const chats = openChats.value;
  const previous = useRef<string | null>(null);

  // Another tab was brought forward: its input gets the focus, as each tab's own page kept it before. Only when the
  // page has the focus already (a tab opened from VS must not pull the keyboard out of the editor), not on the first
  // tab the page shows, and not away from the tab strip (a double-click on a background tab shows it and opens its
  // rename box, which must keep the focus).
  useEffect(() => {
    const switched = previous.current !== null && active !== previous.current;
    previous.current = active;
    const inStrip = document.activeElement?.closest('.tab-strip') != null;
    if (switched && document.hasFocus() && !inStrip) activeChat()?.editor.current?.focus();
  }, [active]);

  return (
    <>
      <TabStrip />
      <div class="chat-area">
        {chats.map((chat: Chat) => (
          <ChatContext.Provider key={chat.id} value={chat}>
            <ChatView active={chat.id === active} />
          </ChatContext.Provider>
        ))}
      </div>
      <PanelFooter />
      <Lightbox />
      <ContextMenu />
    </>
  );
}
