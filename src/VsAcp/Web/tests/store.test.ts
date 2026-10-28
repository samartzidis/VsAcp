import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import type { ChatMessage } from '../src/bridge/protocol';
import { msg } from '../dev/sample';
import { createMessageStore, type MessageStore } from '../src/messages/store';


// a fresh chat's list per test
let store: MessageStore;

const content = (id: string) => store.messageSignal(id)!.value.content;
const user = (id: string, text = ''): ChatMessage => msg({ id, type: 'User', content: text });

beforeEach(() => {
  vi.useFakeTimers();
  store = createMessageStore();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('adding and clearing', () => {
  test('messages keep their arrival store.order', () => {
    store.addMessage(user('a'));
    store.addMessage(msg({ id: 'b', type: 'Assistant' }));
    store.addMessage(user('c'));
    expect(store.order.value).toEqual(['a', 'b', 'c']);
  });

  test('the same id twice updates in place instead of adding a row', () => {
    store.addMessage(user('a', 'one'));
    store.addMessage(user('a', 'two'));
    expect(store.order.value).toEqual(['a']);
    expect(content('a')).toBe('two');
  });

  test('clearing empties the list and asks the view to start following the end again', () => {
    store.addMessage(user('a'));
    const before = store.resetCount.value;
    store.clearMessages();
    expect(store.order.value).toEqual([]);
    expect(store.messageSignal('a')).toBeUndefined();
    expect(store.resetCount.value).toBe(before + 1);
  });
});

describe('streaming content', () => {
  test('chunks within a frame collapse into one render of the latest text', () => {
    store.addMessage(msg({ id: 'a', type: 'Assistant', isStreaming: true }));
    const seen: string[] = [];
    const stop = store.messageSignal('a')!.subscribe((m) => seen.push(m.content));

    store.setContent('a', 'He');
    store.setContent('a', 'Hel');
    store.setContent('a', 'Hello');
    expect(content('a')).toBe('');

    vi.advanceTimersByTime(50);
    expect(content('a')).toBe('Hello');
    expect(seen).toEqual(['', 'Hello']);
    stop();
  });

  test('completing flushes the pending text at once and ends streaming', () => {
    store.addMessage(msg({ id: 'a', type: 'Assistant', isStreaming: true }));
    store.setContent('a', 'final text');
    store.completeMessage('a');
    expect(content('a')).toBe('final text');
    expect(store.messageSignal('a')!.value.isStreaming).toBe(false);
  });

  test('updates for an unknown message are ignored', () => {
    expect(() => {
      store.setContent('nope', 'x');
      store.setStatus('nope', 'Success', 'x');
      store.setBody('nope', 'x', 'Markdown', null);
      store.completeMessage('nope');
      vi.advanceTimersByTime(50);
    }).not.toThrow();
  });
});

describe('status and body', () => {
  test('an empty title leaves the current one; a new title replaces it', () => {
    store.addMessage(msg({ id: 's', type: 'ToolStep', status: 'Pending', expanderTitle: 'Read a.cs' }));
    store.setStatus('s', 'Success', '');
    expect(store.messageSignal('s')!.value).toMatchObject({ status: 'Success', expanderTitle: 'Read a.cs' });
    store.setStatus('s', 'Error', 'Read b.cs');
    expect(store.messageSignal('s')!.value).toMatchObject({ status: 'Error', expanderTitle: 'Read b.cs' });
  });

  test('the body arrives separately from the message', () => {
    store.addMessage(msg({ id: 's', type: 'ToolStep' }));
    store.setBody('s', 'output', 'PlainText', 'csharp');
    expect(store.messageSignal('s')!.value).toMatchObject({ body: 'output', bodyMode: 'PlainText', language: 'csharp' });
  });
});

describe('store.replaceMessages (load and end-of-turn sync)', () => {
  test('adds what is missing, updates what differs, drops what is gone, and restores the store.order', () => {
    store.addMessage(user('a', 'old'));
    store.addMessage(user('gone'));
    store.addMessage(user('c'));

    store.replaceMessages([user('c'), user('a', 'new'), user('d')], false);

    expect(store.order.value).toEqual(['c', 'a', 'd']);
    expect(content('a')).toBe('new');
    expect(store.messageSignal('gone')).toBeUndefined();
  });

  test('messages that did not change are not touched (a long chat is not re-rendered at the end of every turn)', () => {
    store.addMessage(user('a', 'same'));
    store.addMessage(msg({ id: 'b', type: 'ToolStep', selections: [{ label: 'x', tooltip: 'y' }] }));
    const first = store.messageSignal('a')!.value;
    const second = store.messageSignal('b')!.value;

    store.replaceMessages([user('a', 'same'), msg({ id: 'b', type: 'ToolStep', selections: [{ label: 'x', tooltip: 'y' }] })], false);

    expect(store.messageSignal('a')!.value).toBe(first);
    expect(store.messageSignal('b')!.value).toBe(second);
  });

  test('a fresh load goes to the end; a sync does not move the view', () => {
    const before = store.resetCount.value;
    store.replaceMessages([user('a')], false);
    expect(store.resetCount.value).toBe(before);
    store.replaceMessages([user('a'), user('b')], true);
    expect(store.resetCount.value).toBe(before + 1);
  });

  test('the snapshot wins over streaming text that was still waiting for its frame', () => {
    store.addMessage(msg({ id: 'a', type: 'Assistant', isStreaming: true }));
    store.setContent('a', 'stale partial');
    store.replaceMessages([msg({ id: 'a', type: 'Assistant', content: 'authoritative', isStreaming: true })], false);
    vi.advanceTimersByTime(50);
    expect(content('a')).toBe('authoritative');
  });
});

describe('several chats', () => {
  test('each chat has its own list', () => {
    const other = createMessageStore();
    store.addMessage(user('a', 'mine'));
    other.addMessage(user('a', 'theirs'));
    other.addMessage(user('b'));
    expect(store.order.value).toEqual(['a']);
    expect(content('a')).toBe('mine');
    expect(other.messageSignal('a')!.value.content).toBe('theirs');
    other.clearMessages();
    expect(store.order.value).toEqual(['a']);
  });
});
