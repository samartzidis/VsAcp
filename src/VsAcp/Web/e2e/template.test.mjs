// The shipping page: Assets/chat-template.html with the built bundle injected exactly as
// ChatWebView.LoadHtmlTemplate does, in Chromium with a minimal stand-in for window.chrome.webview.
// Catches what the dev harness cannot: template layout, script order, and the real vendor globals.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { after, before, beforeEach, describe, test } from 'node:test';
import { chromium } from 'playwright-core';

const read = (relative) => readFileSync(new URL(relative, import.meta.url), 'utf8');

function shippingHtml() {
  // replace with functions: the scripts contain `$` sequences that a replacement string would interpret
  return read('../../Assets/chat-template.html')
    .replace('{{SHOWDOWN_JS}}', () => read('../../Assets/showdown.min.js'))
    .replace('{{HIGHLIGHTJS_JS}}', () => read('../../Assets/highlight.min.js'))
    .replace('{{SPA_CSS}}', () => read('../dist/spa.css'))
    .replace('{{SPA_JS}}', () => read('../dist/spa.js'));
}

let browser;
let page;
const errors = [];

before(async () => {
  browser = await chromium.launch({ channel: 'msedge', headless: true });
});

after(async () => {
  await browser?.close();
});

beforeEach(async () => {
  page = await browser.newPage({ viewport: { width: 520, height: 700 } });
  errors.length = 0;
  page.on('pageerror', (e) => errors.push(e.message));
  await page.addInitScript(() => {
    const listeners = [];
    window.__sent = [];
    // like the host: a per-tab message the test sends without a tab is for the one open tab
    window.__deliver = (m) => {
      const data = m.type === 'theme' || m.type === 'tabs' || m.tab ? m : { ...m, tab: 't1' };
      listeners.forEach((l) => l({ data }));
    };
    window.chrome = {
      webview: {
        postMessage: (json) => {
          const m = JSON.parse(json);
          window.__sent.push(m);
          // the page has loaded: open one tab, as the host does
          if (m.type === 'page.ready')
            queueMicrotask(() => window.__deliver({ type: 'tabs', tabs: [{ id: 't1', name: 'Chat 1', active: true, open: true }] }));
        },
        postMessageWithAdditionalObjects: () => {},
        addEventListener: (_t, l) => listeners.push(l),
      },
    };
  });
  // a real navigation (not setContent) so the init script above runs before the page's own scripts
  await page.route('http://shipping.test/', (route) => route.fulfill({ contentType: 'text/html', body: shippingHtml() }));
  await page.goto('http://shipping.test/');
  await page.waitForSelector('.editor');
});

const deliver = (m) => page.evaluate((x) => window.__deliver(x), m);
const message = (over) => ({
  content: '', toolName: null, title: '', status: 'Success', expanderTitle: '', bodyMode: 'Markdown', body: null,
  language: null, isStreaming: false, imageDataUris: null, fileNames: null, selections: null, checkpointId: null, ...over,
});

describe('the template', () => {
  test('every replacement token appears exactly once (the host replaces all occurrences, comments included)', () => {
    const template = read('../../Assets/chat-template.html');
    const tokens = [...new Set(template.match(/\{\{[A-Z_]+\}\}/g) ?? [])];
    assert.deepEqual(tokens.sort(), ['{{HIGHLIGHTJS_JS}}', '{{SHOWDOWN_JS}}', '{{SPA_CSS}}', '{{SPA_JS}}']);
    for (const token of tokens) assert.equal(template.split(token).length - 1, 1, `${token} must appear once`);
  });

  test('after replacement no token is left', () => {
    assert.deepEqual(shippingHtml().match(/\{\{[A-Z_]+\}\}/g), null);
  });
});

describe('the shipping page', () => {
  test('boots without errors and announces itself to the host', async () => {
    assert.deepEqual(errors, []);
    assert.ok((await page.evaluate(() => window.__sent)).some((m) => m.type === 'ready'));
  });

  test('uses the real Showdown and highlight.js globals', async () => {
    assert.equal(await page.evaluate(() => typeof window.showdown?.Converter), 'function');
    assert.equal(await page.evaluate(() => typeof window.hljs?.highlightElement), 'function');
    await deliver({ type: 'messages.load', messages: [message({ id: 'a', type: 'Assistant', content: '```csharp\nvar x = 1;\n```' })] });
    await page.waitForSelector('#msg-a pre code.hljs');
  });

  test('layout: the tab strip on top, the message list fills the page, the composer and the footer at the bottom', async () => {
    await deliver({ type: 'state', state: { enabled: true, placeholder: 'Ask', canSend: true, busy: false, images: [], files: [], selections: [],
      activeContext: null, commands: [], modes: [], modeId: null, models: [], modelId: null, autoApprove: false, usage: null, pendingFiles: [] } });
    const many = Array.from({ length: 40 }, (_, i) => message({ id: `m${i}`, type: 'User', content: `line ${i}\n`.repeat(5) }));
    await deliver({ type: 'messages.load', messages: many });
    await page.waitForFunction(() => {
      const el = document.querySelector('.message-list');
      return el.scrollHeight > el.clientHeight && el.scrollHeight - el.scrollTop - el.clientHeight < 4;
    });

    const box = await page.evaluate(() => {
      const r = (s) => document.querySelector(s).getBoundingClientRect();
      return { strip: r('.tab-strip'), list: r('.message-list'), composer: r('.composer'), footer: r('.panel-footer'), viewport: window.innerHeight };
    });
    assert.ok(Math.abs(box.strip.top) < 2, 'the tab strip is at the top edge');
    assert.ok(Math.abs(box.list.top - box.strip.bottom) < 2, 'the list starts under the tab strip');
    assert.ok(Math.abs(box.footer.bottom - box.viewport) < 2, 'the footer is pinned to the bottom edge');
    assert.ok(Math.abs(box.composer.bottom - box.footer.top) < 2, 'the composer sits right above the footer');
    assert.ok(Math.abs(box.list.bottom - box.composer.top) < 2, 'the list ends where the composer starts');
    assert.ok(box.list.height > 200, 'the list gets the remaining height');
    assert.equal(await page.evaluate(() => document.documentElement.scrollHeight <= window.innerHeight), true, 'the page itself never scrolls');
  });

  test('a banner takes space from the list, not from the composer', async () => {
    await deliver({ type: 'messages.load', messages: [message({ id: 'u', type: 'User', content: 'hi' })] });
    const before = await page.evaluate(() => document.querySelector('.message-list').getBoundingClientRect().height);
    await deliver({ type: 'banner.show', banner: { kind: 'permission', id: 'p', title: 'Run it?', options: [{ optionId: 'a', name: 'Allow', kind: 'allow_once' }] } });
    await page.waitForSelector('.banner');
    const after = await page.evaluate(() => ({
      list: document.querySelector('.message-list').getBoundingClientRect().height,
      composerBottom: document.querySelector('.composer').getBoundingClientRect().bottom,
      footerTop: document.querySelector('.panel-footer').getBoundingClientRect().top,
    }));
    assert.ok(after.list < before, 'the list shrinks');
    assert.ok(Math.abs(after.composerBottom - after.footerTop) < 2, 'the composer stays at the bottom, on the footer');
  });

  test('host theme variables reach the page and restyle it', async () => {
    await deliver({ type: 'theme', colors: { '--bg-primary': '#102030', '--text-primary': '#fafafa' } });
    assert.equal(await page.evaluate(() => getComputedStyle(document.body).backgroundColor), 'rgb(16, 32, 48)');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--text-primary').trim()), '#fafafa');
  });

  test('no console errors after a full round of messages', async () => {
    await deliver({ type: 'messages.load', messages: [
      message({ id: 'u', type: 'User', content: 'q', checkpointId: 'cp' }),
      message({ id: 't', type: 'Thinking', content: 'hm' }),
      message({ id: 's', type: 'ToolStep', toolName: 'Read', bodyMode: 'PlainText', body: 'x', language: 'csharp' }),
      message({ id: 'a', type: 'Assistant', content: '# hi\n\n`src/a/b.cs`' }),
    ] });
    await page.waitForSelector('#msg-a a.file-link');
    assert.deepEqual(errors, []);
  });
});
