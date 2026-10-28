// Shared browser fixture for the e2e tests: a Vite dev server serving dev/index.html (mock host) and a
// fresh headless Edge page per test. Importing this module registers the before/after hooks, so the
// importing file only has to define its tests. `page` always refers to the current test's page.

import assert from 'node:assert/strict';
import { after, afterEach, before, beforeEach } from 'node:test';
import { createServer } from 'vite';
import { chromium } from 'playwright-core';

const URL = 'http://localhost:5199/dev/index.html';

let server;
let browser;
let current;

before(async () => {
  server = await createServer({ configFile: 'vite.dev.config.ts', server: { open: false }, logLevel: 'error' });
  await server.listen();
  browser = await chromium.launch({ channel: 'msedge', headless: true });
});

after(async () => {
  await browser?.close();
  await server?.close();
});

beforeEach(async () => {
  current = await browser.newPage();
  await current.goto(URL);
  await current.waitForSelector('.editor[contenteditable="plaintext-only"]');
});

afterEach(async () => {
  // every per-tab message the page posted must have carried its tab's id (see dev/mockHost.ts)
  const errors = await current.evaluate(() => window.__host.errors);
  await current.close();
  assert.deepEqual(errors, [], 'messages posted for the wrong tab');
});

export const page = new Proxy(
  {},
  {
    get(_, prop) {
      const value = current[prop];
      return typeof value === 'function' ? value.bind(current) : value;
    },
  },
);

export const sent = () => page.evaluate(() => window.__host.sent);
export const lastOf = async (type) => (await sent()).filter((m) => m.type === type).at(-1);
export const countOf = async (type) => (await sent()).filter((m) => m.type === type).length;
export const deliver = (message) => page.evaluate((m) => window.__host.deliver(m), message);
export const setState = (patch) =>
  page.evaluate((p) => window.__host.deliver({ type: 'state', state: { ...window.__host.defaultState, ...p } }), patch);
/** The panel's own state (the footer's shared folders). */
export const setPanel = (panel) => page.evaluate((p) => window.__host.deliver({ type: 'panel', folders: null, ...p }), panel);
/** The tabs as the host would send them; each is `{ id, name, active?, open? }` (open by default). */
export const setTabs = (tabs) => page.evaluate((t) => window.__host.setTabs(t), tabs);
export const editor = () => page.locator('.chat-view:not([hidden]) .editor');
export const chip = () => page.locator('.editor .cmd-chip');
