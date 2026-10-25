import { post } from '../bridge/host';
import type { BannerResult } from '../bridge/protocol';
import { banner, editor } from '../state';
import { log } from '../log';

/**
 * Call before a banner is removed or replaced: if the focus is inside it, hand it to the composer so the user
 * can keep typing (a removed element otherwise drops the focus to the page).
 */
export function handFocusBackToComposer(): void {
  if ((document.activeElement as HTMLElement | null)?.closest('.banner')) editor.current?.focus();
}

/** Answers the banner with this id and takes it down (the host also sends `banner.hide`). */
export function resolveBanner(id: string, result: BannerResult): void {
  // the answer's keys only: elicitation values are the user's input
  log.info('banner.resolve', { id, answer: Object.keys(result) });
  post({ type: 'banner.resolve', id, result });
  if (banner.peek()?.id === id) {
    handFocusBackToComposer();
    banner.value = null;
  }
}
