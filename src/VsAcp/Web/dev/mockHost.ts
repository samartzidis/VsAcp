// Stand-in for the WebView2 host so the page can run in a plain browser (npm run dev) and under the
// Playwright tests. Installs window.chrome.webview and records everything the page posts.

import type { ComposerState, HostMessage, TabInfo, WebMessage } from '../src/bridge/protocol';
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

/** The tab the harness opens first. Tests that do not name a tab talk to this one. */
export const defaultTab = 't1';

/** Page messages that belong to no tab (see PanelWebMessage); every other one must carry the tab it is for. */
const panelTypes = new Set([
  'page.ready',
  'page.focused',
  'openFile',
  'tabs.new',
  'tabs.activate',
  'tabs.close',
  'tabs.rename',
  'settings.open',
  'logs.open',
  'folders.add',
  'folders.remove',
  'folders.drop',
]);
/** Host messages that belong to no tab (see PanelHostMessage). */
const panelHostTypes = new Set(['theme', 'tabs', 'panel']);

/** A message as a test writes it: a per-tab message without `tab` goes to the default tab. */
type Deliverable = { type: string; tab?: string; [key: string]: unknown };

export interface MockHost {
  /** What the page posted, without the `tab` of per-tab messages (tests check the content; the tab is checked here). */
  sent: WebMessage[];
  /** The same, with the tab the message was for (null for panel messages). */
  sentWithTab: { tab: string | null; message: WebMessage }[];
  /** Per-tab messages the page posted without the id of an open tab. The e2e harness fails the test when this is not empty. */
  errors: string[];
  /** The snapshot delivered on `ready`; tests derive variants from it. */
  defaultState: ComposerState;
  deliver(message: Deliverable): void;
  /** Replaces the tabs (like the host's tab list) and sends them to the page. Tabs are open unless `open: false`. */
  setTabs(tabs: { id: string; name: string; active?: boolean; open?: boolean }[]): void;
  /** Called for every message the page posted, after it is recorded. */
  onSend: ((message: WebMessage) => void) | null;
}

// The host's tab list, as the mock keeps it: the page's tab actions change it as the host would.
let tabList: TabInfo[] = [{ id: defaultTab, name: 'Chat 1', active: true, open: true }];
let nextTab = 2;

const host: MockHost = {
  sent: [],
  sentWithTab: [],
  errors: [],
  defaultState,
  deliver(message) {
    const full = panelHostTypes.has(message.type) || message.tab !== undefined ? message : { ...message, tab: defaultTab };
    for (const l of listeners) l({ data: full as HostMessage });
  },
  setTabs(tabs) {
    tabList = tabs.map((t) => ({ id: t.id, name: t.name, active: t.active ?? false, open: t.open ?? true }));
    sendTabs();
  },
  onSend: null,
};

function sendTabs(): void {
  host.deliver({ type: 'tabs', tabs: tabList.map((t) => ({ ...t })) });
}

function activate(id: string): void {
  tabList = tabList.map((t) => ({ ...t, active: t.id === id, open: t.open || t.id === id }));
}

/** What the host does with the page's tab actions. */
function onTabAction(message: WebMessage): void {
  switch (message.type) {
    case 'tabs.new': {
      const id = `t${nextTab++}`;
      tabList = [...tabList, { id, name: `Chat ${tabList.length + 1}`, active: false, open: true }];
      activate(id);
      break;
    }
    case 'tabs.activate':
      activate(message.id);
      break;
    case 'tabs.close': {
      const closing = tabList.find((t) => t.id === message.id);
      tabList = tabList.filter((t) => t.id !== message.id);
      if (closing?.active && tabList[0]) activate(tabList[0].id);
      break;
    }
    case 'tabs.rename':
      tabList = tabList.map((t) => (t.id === message.id ? { ...t, name: message.name } : t));
      break;
    default:
      return;
  }
  queueMicrotask(sendTabs);
}

/** Records a page message as the tests see it, and checks it was posted for a tab that is open. */
function record(message: WebMessage & { tab?: string }): WebMessage {
  const isPanel = panelTypes.has(message.type);
  if (isPanel) {
    if (message.tab !== undefined) host.errors.push(`${message.type} is a panel message but carried tab ${message.tab}`);
  } else if (!tabList.some((t) => t.open && t.id === message.tab)) {
    host.errors.push(`${message.type} was posted for tab ${String(message.tab)}, which is not open`);
  }
  const { tab, ...rest } = message;
  const seen = rest as WebMessage;
  host.sent.push(seen);
  host.sentWithTab.push({ tab: isPanel ? null : (tab ?? null), message: seen });
  return seen;
}

window.chrome = {
  webview: {
    postMessage(json: string) {
      const message = JSON.parse(json);
      const seen = record(message);
      host.onSend?.(seen);
      onTabAction(seen);
      if (seen.type === 'page.ready') queueMicrotask(sendTabs);
      if (seen.type === 'ready') {
        const tab = message.tab as string;
        queueMicrotask(() => {
          host.deliver({ type: 'state', state: defaultState, tab });
          // the real host sends the session snapshot on ready; the harness does so only on request (?sample)
          if (new URLSearchParams(location.search).has('sample')) host.deliver({ type: 'messages.load', messages: sampleMessages, tab });
        });
      }
    },
    postMessageWithAdditionalObjects(json: string, objects: unknown[]) {
      const message = JSON.parse(json);
      // the real host receives CoreWebView2File objects; record the names so tests can see what was handed over
      message.files = (objects as File[]).map((f) => f.name);
      const seen = record(message);
      host.onSend?.(seen);
    },
    addEventListener(_type, listener) {
      listeners.push(listener);
    },
  },
};

(window as unknown as { __host: MockHost }).__host = host;
