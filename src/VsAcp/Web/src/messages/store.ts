import { signal, type Signal } from '@preact/signals';
import type { BodyMode, ChatMessage, MessageStatus } from '../bridge/protocol';
import { log } from '../log';

function sameList(a: readonly string[] | null, b: readonly string[] | null): boolean {
  if (a === b) return true;
  if (!a || !b || a.length !== b.length) return false;
  return a.every((v, i) => v === b[i]);
}

function sameMessage(a: ChatMessage, b: ChatMessage): boolean {
  return (
    a.type === b.type &&
    a.content === b.content &&
    a.toolName === b.toolName &&
    a.title === b.title &&
    a.status === b.status &&
    a.expanderTitle === b.expanderTitle &&
    a.bodyMode === b.bodyMode &&
    a.body === b.body &&
    a.language === b.language &&
    a.isStreaming === b.isStreaming &&
    a.checkpointId === b.checkpointId &&
    sameList(a.imageDataUris, b.imageDataUris) &&
    sameList(a.fileNames, b.fileNames) &&
    JSON.stringify(a.selections) === JSON.stringify(b.selections)
  );
}

/** One chat's message list. */
export type MessageStore = ReturnType<typeof createMessageStore>;

export function createMessageStore() {
  // One signal per message, so a streaming update re-renders only that message, plus an ordered id list.
  const byId = new Map<string, Signal<ChatMessage>>();

  const order = signal<string[]>([]);

  /** Bumped when the list is replaced wholesale (load / clear): the list scrolls to the bottom again. */
  const resetCount = signal(0);

  // Content updates arrive per streamed chunk; render at most once per frame with the latest text.
  const pendingContent = new Map<string, string>();
  let frame = 0;

  function flushContent(id?: string): void {
    if (id === undefined) {
      frame = 0;
      for (const [pendingId, content] of pendingContent) applyContent(pendingId, content);
      pendingContent.clear();
      return;
    }
    const content = pendingContent.get(id);
    if (content === undefined) return;
    pendingContent.delete(id);
    applyContent(id, content);
  }

  function applyContent(id: string, content: string): void {
    const s = byId.get(id);
    if (s && s.peek().content !== content) s.value = { ...s.peek(), content };
  }

  return {
    order,
    resetCount,

    messageSignal(id: string): Signal<ChatMessage> | undefined {
      return byId.get(id);
    },

    addMessage(message: ChatMessage): void {
      const existing = byId.get(message.id);
      if (existing) {
        // the same id twice (a replayed event): update in place, never duplicate the row
        existing.value = message;
        return;
      }
      byId.set(message.id, signal(message));
      order.value = [...order.value, message.id];
    },

    setContent(id: string, content: string): void {
      if (!byId.has(id)) {
        // the host streams text for a message the page never got: the end-of-turn sync should repair it
        log.warn('messages.content for an unknown message', { id });
        return;
      }
      pendingContent.set(id, content);
      if (!frame) frame = requestAnimationFrame(() => flushContent());
    },

    setStatus(id: string, status: MessageStatus, expanderTitle: string): void {
      const s = byId.get(id);
      if (!s) return;
      const m = s.peek();
      const title = expanderTitle || m.expanderTitle; // an empty title leaves the current one
      if (m.status !== status || m.expanderTitle !== title) s.value = { ...m, status, expanderTitle: title };
    },

    setBody(id: string, body: string, bodyMode: BodyMode, language: string | null): void {
      const s = byId.get(id);
      if (s) s.value = { ...s.peek(), body, bodyMode, language };
    },

    completeMessage(id: string): void {
      flushContent(id);
      const s = byId.get(id);
      if (s && s.peek().isStreaming) s.value = { ...s.peek(), isStreaming: false };
    },

    clearMessages(): void {
      pendingContent.clear();
      byId.clear();
      order.value = [];
      resetCount.value++;
    },

    /**
     * Makes the list equal to `messages`: adds what is missing, updates what differs, drops what is gone and
     * restores the model's order. Messages that did not change keep their signal untouched, so the end-of-turn
     * sync of a long chat renders nothing it does not have to. `scrollToEnd` is true for a fresh load.
     */
    replaceMessages(messages: readonly ChatMessage[], scrollToEnd: boolean): void {
      pendingContent.clear();
      const keep = new Set<string>();
      for (const m of messages) {
        keep.add(m.id);
        const s = byId.get(m.id);
        if (!s) byId.set(m.id, signal(m));
        else if (!sameMessage(s.peek(), m)) s.value = m;
      }
      for (const id of [...byId.keys()]) if (!keep.has(id)) byId.delete(id);

      const ids = messages.map((m) => m.id);
      if (!sameList(order.peek(), ids)) order.value = ids;
      if (scrollToEnd) resetCount.value++;
    },
  };
}
