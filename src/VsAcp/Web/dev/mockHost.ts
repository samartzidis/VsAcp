// Stand-in for the WebView2 host so the page can run in a plain browser (npm run dev) and under the
// Playwright tests. Installs window.chrome.webview and records everything the page posts.

import type { ComposerState, HostMessage, WebMessage } from '../src/bridge/protocol';
import { sampleMessages } from './sample';

type Listener = (e: { data: unknown }) => void;

const listeners: Listener[] = [];

export const defaultState: ComposerState = {
  enabled: true,
  placeholder: 'Ask a question or describe a task...',
  canSend: true,
  busy: false,
  images: [],
  files: [],
  attachedFolders: [],
  selections: [],
  activeContext: { fileName: 'Program.cs', filePath: 'C:\\src\\Program.cs', enabled: true },
  commands: [
    { name: 'review', description: 'Review the current changes' },
    { name: 'test', description: 'Write tests for the selected code' },
    { name: 'explain', description: 'Explain how this code works' },
  ],
  modes: [
    { id: 'agent', name: 'Agent' },
    { id: 'plan', name: 'Plan' },
  ],
  modeId: 'agent',
  models: [
    { id: 'm|a', name: 'claude-sonnet' },
    { id: 'm|b', name: 'gpt-5' },
  ],
  modelId: 'm|a',
  autoApprove: false,
  usage: { text: '12.3k / 200k (6%)  $0.42', tooltip: 'Context window usage' },
  pendingFiles: [],
  folders: null,
  activeFolders: null,
  agent: null,
};

export interface MockHost {
  sent: WebMessage[];
  /** The snapshot delivered on `ready`; tests derive variants from it. */
  defaultState: ComposerState;
  deliver(message: HostMessage): void;
  /** Called for every message the page posts, after it is recorded. */
  onSend: ((message: WebMessage) => void) | null;
}

const host: MockHost = {
  sent: [],
  defaultState,
  deliver(message) {
    for (const l of listeners) l({ data: message });
  },
  onSend: null,
};

window.chrome = {
  webview: {
    postMessage(json: string) {
      const message = JSON.parse(json) as WebMessage;
      host.sent.push(message);
      host.onSend?.(message);
      if (message.type === 'ready') {
        queueMicrotask(() => {
          host.deliver({ type: 'state', state: defaultState });
          // the real host sends the session snapshot on ready; the harness does so only on request (?sample)
          if (new URLSearchParams(location.search).has('sample')) host.deliver({ type: 'messages.load', messages: sampleMessages });
        });
      }
    },
    postMessageWithAdditionalObjects(json: string, objects: unknown[]) {
      const message = JSON.parse(json) as WebMessage;
      // the real host receives CoreWebView2File objects; record the names so tests can see what was handed over
      (message as unknown as { files: string[] }).files = (objects as File[]).map((f) => f.name);
      host.sent.push(message);
      host.onSend?.(message);
    },
    addEventListener(_type, listener) {
      listeners.push(listener);
    },
  },
};

(window as unknown as { __host: MockHost }).__host = host;
