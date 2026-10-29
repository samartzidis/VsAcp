import type { BannerResult } from '../bridge/protocol';
import { useChat, type Chat } from '../chat';
import { log } from '../log';

/**
 * Call before a banner is removed or replaced: if the focus is inside it, hand it to the composer so the user
 * can keep typing (a removed element otherwise drops the focus to the page).
 */
export function handFocusBackToComposer(chat: Chat): void {
  if ((document.activeElement as HTMLElement | null)?.closest('.banner')) chat.editor.current?.focus();
}

/** Answers the banner with this id and takes it down (the host also sends `banner.hide`). */
export function resolveBanner(chat: Chat, id: string, result: BannerResult): void {
  // the answer's keys only: elicitation values are the user's input
  log.info('banner.resolve', { tab: chat.id, id, answer: Object.keys(result) });
  chat.post({ type: 'banner.resolve', id, result });
  if (chat.banner.peek()?.id === id) {
    handFocusBackToComposer(chat);
    chat.banner.value = null;
  }
}

/** `resolveBanner` for the calling banner's chat. */
export function useResolveBanner(): (id: string, result: BannerResult) => void {
  const chat = useChat();
  return (id, result) => resolveBanner(chat, id, result);
}
