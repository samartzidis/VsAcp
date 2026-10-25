import { BannerHost } from './banners/BannerHost';
import { MessageList } from './messages/MessageList';
import { ContextMenu, Lightbox } from './messages/Overlays';
import { Composer } from './composer/Composer';
import { BusyBar, PendingChangesBar } from './status/StatusBars';

/** The whole chat UI, top to bottom: message list, banner, busy line, pending-changes bar, composer. */
export function App() {
  return (
    <>
      <MessageList />
      <BannerHost />
      <BusyBar />
      <PendingChangesBar />
      <Composer />
      <Lightbox />
      <ContextMenu />
    </>
  );
}
