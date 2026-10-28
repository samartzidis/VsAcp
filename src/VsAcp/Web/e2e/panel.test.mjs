// The panel around the chats: the tab strip, the footer, and several chats in one page. The mock host keeps a tab
// list and answers the page's tab actions as the real host does (dev/mockHost.ts).

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { countOf, deliver, editor, lastOf, page, setPanel, setTabs } from './harness.mjs';

const tabNames = () => page.locator('.tab .tab-name').allInnerTexts();
const activeName = () => page.locator('.tab.active .tab-name').innerText();
const tab = (name) => page.locator('.tab', { has: page.locator('.tab-name', { hasText: name }) });
const shownChats = () => page.locator('.chat-view:not([hidden])').count();
const sentFor = (tabId, type) => page.evaluate(([t, ty]) => window.__host.sentWithTab.filter((s) => s.tab === t && s.message.type === ty).length, [tabId, type]);

const two = [
  { id: 't1', name: 'Chat 1', active: true },
  { id: 't2', name: 'Fix the parser', active: false },
];

describe('tab strip', () => {
  test('shows the tabs in order with the active one marked', async () => {
    await setTabs(two);
    assert.deepEqual(await tabNames(), ['Chat 1', 'Fix the parser']);
    assert.equal(await activeName(), 'Chat 1');
    assert.equal(await page.locator('.tab.active').getAttribute('aria-selected'), 'true');
  });

  test('a click asks for the tab and shows its chat once the host makes it active', async () => {
    await setTabs([two[0], { ...two[1], open: false }]);
    assert.equal(await page.locator('.chat-view').count(), 1, 'a tab that is not open has no chat yet');

    await tab('Fix the parser').click();
    assert.deepEqual(await lastOf('tabs.activate'), { type: 'tabs.activate', id: 't2' });
    await page.waitForFunction(() => document.querySelectorAll('.chat-view').length === 2);
    assert.equal(await activeName(), 'Fix the parser');
    assert.equal(await shownChats(), 1);
    assert.equal(await sentFor('t2', 'ready'), 1, 'the new chat view asks for its state');
  });

  test('clicking the active tab asks for nothing', async () => {
    await setTabs(two);
    await tab('Chat 1').click();
    assert.equal(await countOf('tabs.activate'), 0);
  });

  test('+ asks for a new chat', async () => {
    await page.getByRole('button', { name: 'New chat' }).click();
    assert.equal(await countOf('tabs.new'), 1);
    await page.waitForFunction(() => document.querySelectorAll('.tab').length === 2);
    assert.equal(await activeName(), 'Chat 2');
  });

  test('closing a background tab does not bring it forward first', async () => {
    await setTabs(two);
    await tab('Fix the parser').hover();
    await tab('Fix the parser').locator('.tab-close').click();
    assert.deepEqual(await lastOf('tabs.close'), { type: 'tabs.close', id: 't2' });
    assert.equal(await countOf('tabs.activate'), 0);
    await page.waitForFunction(() => document.querySelectorAll('.tab').length === 1);
  });

  test('a double-click renames in place: Enter keeps the name, Esc keeps the old one', async () => {
    await setTabs(two);
    await tab('Fix the parser').dblclick();
    const box = page.locator('.tab-rename');
    assert.equal(await box.inputValue(), 'Fix the parser');
    await box.fill('Parser work');
    await box.press('Enter');
    assert.deepEqual(await lastOf('tabs.rename'), { type: 'tabs.rename', id: 't2', name: 'Parser work' });
    await page.waitForFunction(() => [...document.querySelectorAll('.tab-name')].some((n) => n.textContent === 'Parser work'));

    await tab('Chat 1').dblclick();
    await page.locator('.tab-rename').fill('Nope');
    await page.locator('.tab-rename').press('Escape');
    assert.equal(await countOf('tabs.rename'), 1);
    assert.equal(await page.locator('.tab-rename').count(), 0);
  });

  test('an empty or unchanged name is not sent', async () => {
    await setTabs(two);
    await tab('Chat 1').dblclick();
    await page.locator('.tab-rename').fill('   ');
    await page.locator('.tab-rename').press('Enter');
    await tab('Chat 1').dblclick();
    await page.locator('.tab-rename').press('Enter');
    assert.equal(await countOf('tabs.rename'), 0);
  });

  test('the context menu offers Rename', async () => {
    await setTabs(two);
    await tab('Fix the parser').click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Rename' }).click();
    await page.locator('.tab-rename').fill('Renamed');
    await page.locator('.tab-rename').press('Enter');
    assert.deepEqual(await lastOf('tabs.rename'), { type: 'tabs.rename', id: 't2', name: 'Renamed' });
  });

  test('a press on a tab leaves the focus in the input', async () => {
    await setTabs(two);
    await editor().click();
    await tab('Chat 1').click();
    assert.equal(await page.evaluate(() => document.activeElement?.classList.contains('editor') ?? false), true);
  });
});

describe('several chats in one page', () => {
  test('each chat keeps its own text, state and messages', async () => {
    await setTabs(two);
    await editor().click();
    await page.keyboard.type('draft for one');

    await tab('Fix the parser').click();
    await page.waitForFunction(() => document.querySelectorAll('.chat-view').length === 2);
    await deliver({ type: 'state', tab: 't2', state: { ...(await page.evaluate(() => window.__host.defaultState)), busy: true } });
    await deliver({ type: 'messages.add', tab: 't2', message: {
      id: 'x', type: 'User', content: 'only in two', toolName: null, title: '', status: 'Success', expanderTitle: '',
      bodyMode: 'Markdown', body: null, language: null, isStreaming: false, imageDataUris: null, fileNames: null,
      folderNames: null, selections: null, checkpointId: null } });
    assert.equal(await page.locator('.chat-view:not([hidden]) .busy-bar').count(), 1);
    assert.equal(await page.locator('.chat-view:not([hidden])').innerText().then((s) => s.includes('only in two')), true);

    await tab('Chat 1').click();
    await page.waitForFunction(() => document.querySelector('.tab.active .tab-name')?.textContent === 'Chat 1');
    assert.equal(await editor().innerText(), 'draft for one', 'the unsent text survived the switch');
    assert.equal(await page.locator('.chat-view:not([hidden]) .busy-bar').count(), 0);
    assert.equal(await page.locator('.chat-view:not([hidden])').innerText().then((s) => s.includes('only in two')), false);
  });

  test('a chat keeps its scroll position while another tab is shown', async () => {
    await setTabs(two);
    const many = Array.from({ length: 60 }, (_, i) => ({
      id: `m${i}`, type: 'User', content: `line ${i}
`.repeat(4), toolName: null, title: '', status: 'Success', expanderTitle: '',
      bodyMode: 'Markdown', body: null, language: null, isStreaming: false, imageDataUris: null, fileNames: null,
      folderNames: null, selections: null, checkpointId: null }));
    await deliver({ type: 'messages.load', tab: 't1', messages: many });
    const list = () => page.locator('.chat-view:not([hidden]) .message-list');
    await page.waitForFunction(() => {
      const el = document.querySelector('.chat-view:not([hidden]) .message-list');
      return el && el.scrollHeight > el.clientHeight * 2;
    });
    await list().evaluate((el) => (el.scrollTop = 300));
    // the user scrolled up: the list no longer follows the end
    await page.waitForTimeout(50);

    await tab('Fix the parser').click();
    await page.waitForFunction(() => document.querySelector('.tab.active .tab-name')?.textContent === 'Fix the parser');
    await tab('Chat 1').click();
    await page.waitForFunction(() => document.querySelector('.tab.active .tab-name')?.textContent === 'Chat 1');

    assert.equal(await list().evaluate((el) => el.scrollTop), 300);
  });

  test('a chat that follows the end shows the newest messages after streaming in the background', async () => {
    await setTabs(two);
    const message = (i) => ({
      id: `m${i}`, type: 'User', content: `line ${i}
`.repeat(4), toolName: null, title: '', status: 'Success', expanderTitle: '',
      bodyMode: 'Markdown', body: null, language: null, isStreaming: false, imageDataUris: null, fileNames: null,
      folderNames: null, selections: null, checkpointId: null });
    await deliver({ type: 'messages.load', tab: 't1', messages: Array.from({ length: 30 }, (_, i) => message(i)) });
    const atEnd = () => page.evaluate(() => {
      const el = document.querySelector('.chat-view:not([hidden]) .message-list');
      return el.scrollHeight - el.scrollTop - el.clientHeight < 4;
    });
    await page.waitForFunction(() => {
      const el = document.querySelector('.chat-view:not([hidden]) .message-list');
      return el && el.scrollHeight > el.clientHeight && el.scrollHeight - el.scrollTop - el.clientHeight < 4;
    });

    await tab('Fix the parser').click();
    await page.waitForFunction(() => document.querySelector('.tab.active .tab-name')?.textContent === 'Fix the parser');
    for (let i = 30; i < 50; i++) await deliver({ type: 'messages.add', tab: 't1', message: message(i) });

    await tab('Chat 1').click();
    await page.waitForFunction(() => document.querySelector('.tab.active .tab-name')?.textContent === 'Chat 1');
    await page.waitForTimeout(100);
    assert.equal(await atEnd(), true, 'the newest message is in view');
  });

  test('what a chat sends carries its own tab', async () => {
    await setTabs([{ ...two[0], active: false }, { ...two[1], active: true }]);
    await page.waitForFunction(() => document.querySelectorAll('.chat-view').length === 2);
    await editor().click();
    await page.keyboard.type('hello');
    await page.keyboard.press('Enter');
    assert.equal(await sentFor('t2', 'composer.send'), 1);
    assert.equal(await sentFor('t1', 'composer.send'), 0);
  });

  test('switching tabs puts the focus in the shown chat\'s input', async () => {
    await setTabs(two);
    await page.waitForFunction(() => document.querySelectorAll('.chat-view').length === 2);
    await editor().click();
    await tab('Fix the parser').click();
    // the shown chat's own input, not the one the focus was in before
    await page.waitForFunction(() => {
      const shown = document.querySelector('.chat-view:not([hidden])');
      return document.querySelector('.tab.active .tab-name')?.textContent === 'Fix the parser' && document.activeElement === shown?.querySelector('.editor');
    });
  });

  test('a closed tab\'s chat goes away, and messages for it are ignored', async () => {
    await setTabs(two);
    await page.waitForFunction(() => document.querySelectorAll('.chat-view').length === 2);
    await setTabs([two[0]]);
    assert.equal(await page.locator('.chat-view').count(), 1);
    await deliver({ type: 'banner.show', tab: 't2', banner: { kind: 'error', id: 'e', message: 'gone' } });
    assert.equal(await page.locator('.banner').count(), 0);
  });
});

describe('focus', () => {
  test('the page tells the host when it gets the focus (so VS makes the chat window its active one)', async () => {
    // the harness page already has the focus: take it away, then give it back as a click from another window would
    await page.evaluate(() => window.dispatchEvent(new FocusEvent('blur')));
    const before = await countOf('page.focused');
    await page.evaluate(() => window.dispatchEvent(new FocusEvent('focus')));
    assert.equal(await countOf('page.focused'), before + 1);
    assert.deepEqual(await lastOf('page.focused'), { type: 'page.focused' });
  });
});

describe('footer', () => {
  test('the settings and the shared folders are there with no chat open', async () => {
    await setTabs([]);
    assert.equal(await page.locator('.chat-view').count(), 0);
    await setPanel({ folders: { items: [], supported: null, canEdit: true } });
    assert.equal(await page.locator('.panel-footer .settings-btn').count(), 1);
    assert.equal(await page.locator('.panel-footer .folders-btn').count(), 1);
    await page.locator('.settings-btn').click();
    assert.deepEqual(await lastOf('settings.open'), { type: 'settings.open' });
  });

  test('a drop with no chat open attaches nothing, but the folders chip still takes folders', async () => {
    await setTabs([]);
    await setPanel({ folders: { items: [], supported: null, canEdit: true } });
    const drop = (selector) =>
      page.evaluate((sel) => {
        const dt = new DataTransfer();
        dt.items.add(new File([''], 'docs'));
        document.querySelector(sel).dispatchEvent(new DragEvent('drop', { dataTransfer: dt, bubbles: true, cancelable: true }));
      }, selector);
    await drop('.chat-area');
    assert.equal(await countOf('composer.drop'), 0);
    await drop('.folders-btn');
    assert.deepEqual((await lastOf('folders.drop')).files, ['docs']);
  });

  test('the footer stays at the bottom under the chat', async () => {
    const box = await page.evaluate(() => {
      const r = (s) => document.querySelector(s).getBoundingClientRect();
      return { composer: r('.chat-view:not([hidden]) .composer'), footer: r('.panel-footer'), viewport: window.innerHeight };
    });
    assert.ok(Math.abs(box.footer.bottom - box.viewport) < 2);
    assert.ok(Math.abs(box.composer.bottom - box.footer.top) < 2);
  });
});
