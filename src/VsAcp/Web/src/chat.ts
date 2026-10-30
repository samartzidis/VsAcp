import { createContext } from 'preact';
import { useContext } from 'preact/hooks';
import { computed, signal, type ReadonlySignal, type Signal } from '@preact/signals';
import { post as postToHost, postWithFiles as postFilesToHost } from './bridge/host';
import { emptyComposerState, type Banner, type ComposerState, type TabWebMessage } from './bridge/protocol';
import type { EditorController } from './composer/editorDom';
import { createMessageStore, type MessageStore } from './messages/store';

/**
 * One chat tab's state in the page. Every chat view component reads it through `useChat()`, so the page can hold
 * several chats; what a chat posts carries its tab id, and the host routes it to that tab's session.
 */
export interface Chat {
  readonly id: string;
  /** Latest composer snapshot from the host (everything except the text being typed). */
  readonly composer: Signal<ComposerState>;
  /**
   * Whether a revert is running, on its own so the message buttons it disables re-render only when it changes, not
   * with every composer snapshot.
   */
  readonly reverting: ReadonlySignal<boolean>;
  /** The banner currently asking the user something, if any. A newer banner replaces an older one. */
  readonly banner: Signal<Banner | null>;
  /**
   * The live editor, set by <Composer>. Host messages that change the text (`composer.set`, `composer.insert`,
   * `composer.focus`) act on it directly instead of going through state, because the page owns the text while the
   * user types.
   */
  readonly editor: { current: EditorController | null };
  readonly messages: MessageStore;
  /** The chat's view, set while it is mounted: drop targets are looked up inside it. */
  readonly root: { current: HTMLElement | null };
  post(message: TabWebMessage): void;
  /** Like `post`, but also hands the host the dropped File objects so it can read their paths. */
  postWithFiles(message: TabWebMessage, files: File[]): void;
}

export function createChat(id: string): Chat {
  const composer = signal<ComposerState>(emptyComposerState);
  return {
    id,
    composer,
    reverting: computed(() => composer.value.reverting),
    banner: signal<Banner | null>(null),
    editor: { current: null },
    messages: createMessageStore(),
    root: { current: null },
    post: (message) => postToHost({ ...message, tab: id }),
    postWithFiles: (message, files) => postFilesToHost({ ...message, tab: id }, files),
  };
}

export const ChatContext = createContext<Chat | null>(null);

/** The chat the calling component belongs to. */
export function useChat(): Chat {
  const chat = useContext(ChatContext);
  if (!chat) throw new Error('useChat outside a chat view');
  return chat;
}
