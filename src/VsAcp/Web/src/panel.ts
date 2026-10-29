import { computed, signal } from '@preact/signals';
import type { FoldersState, TabInfo } from './bridge/protocol';
import { createChat, type Chat } from './chat';
import { log } from './log';

/** The solution's shared folders (the footer chip); null when there is no local project to attach them to. */
export const panelFolders = signal<FoldersState | null>(null);

/** The tabs as the host last sent them, in order. */
export const tabs = signal<TabInfo[]>([]);

// The chats of the open tabs. Kept outside the signal so a chat (its messages, its banner, its unsent text) lives
// as long as its tab is open, whatever else the tab list does.
const chats = new Map<string, Chat>();

/** The open tabs' chats, in tab order. */
export const openChats = computed(() =>
  tabs.value.filter((t) => t.open).map((t) => chats.get(t.id)).filter((c): c is Chat => c !== undefined),
);

export const activeTabId = computed(() => tabs.value.find((t) => t.active && t.open)?.id ?? null);

export function chatOf(id: string): Chat | undefined {
  return chats.get(id);
}

/** The chat of the tab being shown, if one is open. */
export function activeChat(): Chat | null {
  const id = activeTabId.peek();
  return id === null ? null : (chats.get(id) ?? null);
}

/** Applies the host's tab list: a chat for each open tab that has none, and none for a tab that closed. */
export function setTabs(next: TabInfo[]): void {
  const open = new Set(next.filter((t) => t.open).map((t) => t.id));
  for (const id of [...chats.keys()]) {
    if (!open.has(id)) {
      chats.delete(id);
      log.info('chat.closed', { tab: id });
    }
  }
  for (const id of open) {
    if (!chats.has(id)) {
      chats.set(id, createChat(id));
      log.info('chat.opened', { tab: id });
    }
  }
  tabs.value = next;
}
