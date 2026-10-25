import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import type { ChatMessage } from '../src/bridge/protocol';
import { msg } from '../dev/sample';
import {
  addMessage,
  clearMessages,
  completeMessage,
  messageSignal,
  order,
  replaceMessages,
  resetCount,
  setBody,
  setContent,
  setStatus,
} from '../src/messages/store';

const content = (id: string) => messageSignal(id)!.value.content;
const user = (id: string, text = ''): ChatMessage => msg({ id, type: 'User', content: text });

beforeEach(() => {
  vi.useFakeTimers();
  clearMessages();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('adding and clearing', () => {
  test('messages keep their arrival order', () => {
    addMessage(user('a'));
    addMessage(msg({ id: 'b', type: 'Assistant' }));
    addMessage(user('c'));
    expect(order.value).toEqual(['a', 'b', 'c']);
  });

  test('the same id twice updates in place instead of adding a row', () => {
    addMessage(user('a', 'one'));
    addMessage(user('a', 'two'));
    expect(order.value).toEqual(['a']);
    expect(content('a')).toBe('two');
  });

  test('clearing empties the list and asks the view to start following the end again', () => {
    addMessage(user('a'));
    const before = resetCount.value;
    clearMessages();
    expect(order.value).toEqual([]);
    expect(messageSignal('a')).toBeUndefined();
    expect(resetCount.value).toBe(before + 1);
  });
});

describe('streaming content', () => {
  test('chunks within a frame collapse into one render of the latest text', () => {
    addMessage(msg({ id: 'a', type: 'Assistant', isStreaming: true }));
    const seen: string[] = [];
    const stop = messageSignal('a')!.subscribe((m) => seen.push(m.content));

    setContent('a', 'He');
    setContent('a', 'Hel');
    setContent('a', 'Hello');
    expect(content('a')).toBe('');

    vi.advanceTimersByTime(50);
    expect(content('a')).toBe('Hello');
    expect(seen).toEqual(['', 'Hello']);
    stop();
  });

  test('completing flushes the pending text at once and ends streaming', () => {
    addMessage(msg({ id: 'a', type: 'Assistant', isStreaming: true }));
    setContent('a', 'final text');
    completeMessage('a');
    expect(content('a')).toBe('final text');
    expect(messageSignal('a')!.value.isStreaming).toBe(false);
  });

  test('updates for an unknown message are ignored', () => {
    expect(() => {
      setContent('nope', 'x');
      setStatus('nope', 'Success', 'x');
      setBody('nope', 'x', 'Markdown', null);
      completeMessage('nope');
      vi.advanceTimersByTime(50);
    }).not.toThrow();
  });
});

describe('status and body', () => {
  test('an empty title leaves the current one; a new title replaces it', () => {
    addMessage(msg({ id: 's', type: 'ToolStep', status: 'Pending', expanderTitle: 'Read a.cs' }));
    setStatus('s', 'Success', '');
    expect(messageSignal('s')!.value).toMatchObject({ status: 'Success', expanderTitle: 'Read a.cs' });
    setStatus('s', 'Error', 'Read b.cs');
    expect(messageSignal('s')!.value).toMatchObject({ status: 'Error', expanderTitle: 'Read b.cs' });
  });

  test('the body arrives separately from the message', () => {
    addMessage(msg({ id: 's', type: 'ToolStep' }));
    setBody('s', 'output', 'PlainText', 'csharp');
    expect(messageSignal('s')!.value).toMatchObject({ body: 'output', bodyMode: 'PlainText', language: 'csharp' });
  });
});

describe('replaceMessages (load and end-of-turn sync)', () => {
  test('adds what is missing, updates what differs, drops what is gone, and restores the order', () => {
    addMessage(user('a', 'old'));
    addMessage(user('gone'));
    addMessage(user('c'));

    replaceMessages([user('c'), user('a', 'new'), user('d')], false);

    expect(order.value).toEqual(['c', 'a', 'd']);
    expect(content('a')).toBe('new');
    expect(messageSignal('gone')).toBeUndefined();
  });

  test('messages that did not change are not touched (a long chat is not re-rendered at the end of every turn)', () => {
    addMessage(user('a', 'same'));
    addMessage(msg({ id: 'b', type: 'ToolStep', selections: [{ label: 'x', tooltip: 'y' }] }));
    const first = messageSignal('a')!.value;
    const second = messageSignal('b')!.value;

    replaceMessages([user('a', 'same'), msg({ id: 'b', type: 'ToolStep', selections: [{ label: 'x', tooltip: 'y' }] })], false);

    expect(messageSignal('a')!.value).toBe(first);
    expect(messageSignal('b')!.value).toBe(second);
  });

  test('a fresh load goes to the end; a sync does not move the view', () => {
    const before = resetCount.value;
    replaceMessages([user('a')], false);
    expect(resetCount.value).toBe(before);
    replaceMessages([user('a'), user('b')], true);
    expect(resetCount.value).toBe(before + 1);
  });

  test('the snapshot wins over streaming text that was still waiting for its frame', () => {
    addMessage(msg({ id: 'a', type: 'Assistant', isStreaming: true }));
    setContent('a', 'stale partial');
    replaceMessages([msg({ id: 'a', type: 'Assistant', content: 'authoritative', isStreaming: true })], false);
    vi.advanceTimersByTime(50);
    expect(content('a')).toBe('authoritative');
  });
});
