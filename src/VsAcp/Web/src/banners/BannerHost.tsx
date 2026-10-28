import { useChat } from '../chat';
import { ElicitationBanner } from './ElicitationBanner';
import { RevertBanner } from './RevertBanner';
import { PermissionBanner, ErrorBanner } from './SimpleBanners';

/** The single prompt between the message list and the composer. A newer banner replaces an older one. */
export function BannerHost() {
  const b = useChat().banner.value;
  if (!b) return null;
  // keyed by id so a replacement banner never inherits the previous one's form state
  switch (b.kind) {
    case 'permission':
      return <PermissionBanner key={b.id} banner={b} />;
    case 'revert':
      return <RevertBanner key={b.id} banner={b} />;
    case 'elicitation':
      return <ElicitationBanner key={b.id} banner={b} />;
    case 'error':
      return <ErrorBanner key={b.id} banner={b} />;
  }
}
