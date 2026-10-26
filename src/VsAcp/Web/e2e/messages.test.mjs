// The chat message list in the real page against the mock host.

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { msg } from '../dev/sample.ts';
import { countOf, deliver, lastOf, page } from './harness.mjs';

const load = (...messages) => deliver({ type: 'messages.load', messages });
const add = (message) => deliver({ type: 'messages.add', message });
const msgEl = (id) => page.locator(`#msg-${id}`);
const rendered = (selector) => page.waitForSelector(selector);

describe('rendering', () => {
  test('a user message: text, attachment chips, selections and images', async () => {
    await load(
      msg({
        id: 'u1',
        type: 'User',
        content: 'Please fix this',
        fileNames: ['a.cs'],
        folderNames: ['assets'],
        selections: [{ label: 'b.cs (3-9)', tooltip: 'C:\\b.cs' }],
        imageDataUris: ['data:image/gif;base64,R0lGODlhAQABAAAAACw='],
      }),
    );
    const el = msgEl('u1');
    assert.equal(await el.locator('.user-text').innerText(), 'Please fix this');
    assert.equal(await el.locator('.user-chip').count(), 3);
    assert.equal(await el.locator('.user-chip', { hasText: 'assets' }).innerText(), '📁 assets');
    assert.equal(await el.locator('.user-chip', { hasText: 'b.cs (3-9)' }).getAttribute('title'), 'C:\\b.cs');
    assert.equal(await el.locator('img.user-img').count(), 1);
  });

  test('user text is not interpreted as markdown or HTML', async () => {
    await load(msg({ id: 'u1', type: 'User', content: '**not bold** <b>nor this</b>' }));
    assert.equal(await msgEl('u1').locator('.user-text').innerText(), '**not bold** <b>nor this</b>');
    assert.equal(await msgEl('u1').locator('b').count(), 0);
  });

  test('an assistant message renders markdown with highlighted code', async () => {
    await load(msg({ id: 'a1', type: 'Assistant', content: '## Title\n\nsome **bold**\n\n```csharp\nvar x = 1;\n```' }));
    const el = msgEl('a1');
    assert.equal(await el.locator('h2').innerText(), 'Title');
    assert.equal(await el.locator('strong').innerText(), 'bold');
    assert.ok((await el.locator('pre code.hljs').count()) === 1, 'code block is highlighted');
  });

  test('a fenced block with no declared language is left as plain text, not auto-detected', async () => {
    await load(msg({
      id: 'a2',
      type: 'Assistant',
      content: '```\nAdd crop-before-send to image upload\n\nPull in react-image-crop and let users adjust a crop before sending.\n```',
    }));
    const code = msgEl('a2').locator('pre code');
    assert.equal(await code.count(), 1);
    assert.equal(await code.getAttribute('class'), null);
    assert.equal(await code.locator('span').count(), 0, 'no hljs spans from an auto-detect guess');
  });

  test('a tool step shows its icon, status, name and detail', async () => {
    await load(msg({ id: 's1', type: 'ToolStep', toolName: 'Read File', expanderTitle: 'Read src/a.cs', status: 'Success' }));
    const header = msgEl('s1').locator('.toolstep-header');
    assert.equal(await header.locator('.toolstep-name').innerText(), 'Read');
    assert.equal(await header.locator('.toolstep-title').innerText(), 'src/a.cs');
    assert.equal(await header.locator('.toolstep-status').innerText(), '✓');
    assert.equal(await header.locator('.toolstep-icon svg').count(), 1);
  });

  test('a thinking row shows its title and keeps the text hidden until expanded', async () => {
    await load(msg({ id: 't1', type: 'Thinking', content: 'secret reasoning', expanderTitle: 'Thought for 3s' }));
    const el = msgEl('t1');
    assert.equal(await el.locator('.thinking-title').innerText(), 'Thought for 3s');
    assert.equal(await el.locator('.thinking-content').isVisible(), false);
    await el.locator('.thinking-header').click();
    assert.equal(await el.locator('.thinking-content').isVisible(), true);
    assert.equal(await el.locator('.thinking-content').innerText(), 'secret reasoning');
    await el.locator('.thinking-header').click();
    assert.equal(await el.locator('.thinking-content').isVisible(), false);
  });

  test('a notice shows its severity, title and detail as plain text', async () => {
    await load(
      msg({ id: 'n1', type: 'Notice', status: 'Warning', title: 'Rate limited', body: '<b>retry</b> in 30s' }),
      msg({ id: 'n2', type: 'Notice', status: 'Error', title: 'MCP server failed' }),
      msg({ id: 'n3', type: 'Notice', status: 'Info', title: 'Model switched' }),
    );
    const warn = msgEl('n1');
    assert.equal(await warn.getAttribute('data-severity'), 'warning');
    assert.equal(await warn.locator('.notice-status').innerText(), '!');
    assert.equal(await warn.locator('.notice-title').innerText(), 'Rate limited');
    assert.equal(await warn.locator('.notice-detail').innerText(), '<b>retry</b> in 30s');
    assert.equal(await warn.locator('b').count(), 0, 'detail is text, not HTML');
    assert.equal(await msgEl('n2').getAttribute('data-severity'), 'error');
    assert.equal(await msgEl('n2').locator('.notice-status').innerText(), '✗');
    assert.equal(await msgEl('n2').locator('.notice-detail').count(), 0, 'no detail, no detail row');
    assert.equal(await msgEl('n3').getAttribute('data-severity'), 'info');
  });

  test('a compaction shows a whole-sentence title and an expandable summary, and follows live updates', async () => {
    await add(msg({ id: 'c1', type: 'Compaction', status: 'Pending', title: 'Compacting context...', expanderTitle: 'Compacting context...' }));
    const header = msgEl('c1').locator('.toolstep-header');
    assert.equal(await header.locator('.toolstep-name').innerText(), 'Compacting context...');
    assert.equal(await header.locator('.toolstep-title').count(), 0, 'the title is not split into name and detail');
    assert.equal(await msgEl('c1').getAttribute('data-type'), 'Compaction');

    await deliver({ type: 'messages.body', id: 'c1', body: 'We **fixed** the build.', bodyMode: 'Markdown', language: null });
    await deliver({ type: 'messages.status', id: 'c1', status: 'Success', expanderTitle: 'Context compacted' });
    await page.waitForFunction(() => document.querySelector('#msg-c1 .toolstep-status')?.textContent === '✓');
    assert.equal(await header.locator('.toolstep-name').innerText(), 'Context compacted');

    assert.equal(await msgEl('c1').locator('.toolstep-body').isVisible(), false);
    await header.click();
    assert.equal(await msgEl('c1').locator('.toolstep-body strong').innerText(), 'fixed');
  });

  test('the "no longer in the agent\'s context" hint shows only once a compaction has succeeded', async () => {
    const hint = (id) => msgEl(id).locator('.compaction-hint');
    await add(msg({ id: 'c1', type: 'Compaction', status: 'Pending', title: 'Compacting context...', expanderTitle: 'Compacting context...' }));
    assert.equal(await hint('c1').count(), 0, 'not while in progress');
    await deliver({ type: 'messages.status', id: 'c1', status: 'Success', expanderTitle: 'Context compacted' });
    await hint('c1').waitFor();
    assert.equal(await hint('c1').innerText(), "Messages above are no longer in the agent's context");

    await load(
      msg({ id: 'f1', type: 'Compaction', status: 'Error', title: 'Context compaction failed', expanderTitle: 'Context compaction failed' }),
      msg({ id: 'x1', type: 'Compaction', status: 'Info', title: 'Context compaction cancelled', expanderTitle: 'Context compaction cancelled' }),
      msg({ id: 't1', type: 'ToolStep', toolName: 'Read File', expanderTitle: 'Read src/a.cs', status: 'Success' }),
    );
    for (const id of ['f1', 'x1', 't1']) assert.equal(await hint(id).count(), 0, `none on ${id}`);
  });
});

describe('live updates', () => {
  test('a streaming assistant message grows, shows the cursor, and settles on completion', async () => {
    await add(msg({ id: 'a1', type: 'Assistant', isStreaming: true }));
    const content = msgEl('a1').locator('.msg-content');
    assert.match(await content.getAttribute('class'), /streaming-cursor/);

    await deliver({ type: 'messages.content', id: 'a1', content: 'Hel' });
    await deliver({ type: 'messages.content', id: 'a1', content: 'Hello **world**' });
    await page.waitForFunction(() => document.querySelector('#msg-a1 strong')?.textContent === 'world');

    await deliver({ type: 'messages.complete', id: 'a1' });
    await page.waitForFunction(() => !document.querySelector('#msg-a1 .msg-content').classList.contains('streaming-cursor'));
    assert.equal(await content.innerText(), 'Hello world');
  });

  test('code blocks of a streaming reply get their copy buttons only once it completes', async () => {
    await add(msg({ id: 'a1', type: 'Assistant', isStreaming: true }));
    await deliver({ type: 'messages.content', id: 'a1', content: ['```', 'x = 1', '```'].join(String.fromCharCode(10)) });
    await rendered('#msg-a1 pre');
    assert.equal(await msgEl('a1').locator('.code-copy-btn').count(), 0);

    await deliver({ type: 'messages.complete', id: 'a1' });
    await rendered('#msg-a1 .code-copy-btn');
    assert.equal(await msgEl('a1').locator('.code-copy-btn').count(), 1);
  });

  test('status and title updates change the tool pill without losing the open body', async () => {
    await add(msg({ id: 's1', type: 'ToolStep', toolName: 'Terminal', status: 'Pending' }));
    await deliver({ type: 'messages.body', id: 's1', body: 'line one', bodyMode: 'PlainText', language: null });
    await msgEl('s1').locator('.toolstep-header').click();
    assert.equal(await msgEl('s1').locator('.msg-body').isVisible(), true);

    await deliver({ type: 'messages.status', id: 's1', status: 'Error', expanderTitle: 'Terminal dotnet build' });
    await page.waitForFunction(() => document.querySelector('#msg-s1 .toolstep-status')?.textContent === '✗');
    assert.equal(await msgEl('s1').locator('.toolstep-name').innerText(), 'Terminal');
    assert.equal(await msgEl('s1').locator('.toolstep-title').innerText(), 'dotnet build');
    assert.equal(await msgEl('s1').locator('.msg-body').isVisible(), true, 'the user\'s expanded state survives updates');
  });

  test('tool output is collapsed by default, expands on click, and a step without output cannot expand', async () => {
    await load(
      msg({ id: 'empty', type: 'ToolStep', toolName: 'Read' }),
      msg({ id: 'full', type: 'ToolStep', toolName: 'Read', bodyMode: 'PlainText', body: 'file contents', language: null }),
    );
    assert.equal(await msgEl('empty').locator('.msg-body').count(), 0);
    await msgEl('empty').locator('.toolstep-header').click();
    assert.equal(await msgEl('empty').locator('.msg-body').count(), 0);

    assert.equal(await msgEl('full').locator('.msg-body').isVisible(), false);
    await msgEl('full').locator('.toolstep-header').click();
    assert.equal(await msgEl('full').locator('.msg-body').isVisible(), true);
    assert.match(await msgEl('full').locator('.msg-body').innerText(), /file contents/);
    assert.match(await msgEl('full').locator('.toolstep-arrow').getAttribute('class'), /expanded/);
  });

  test('tool bodies render in their mode: markdown, plain text with highlighting, sanitized html', async () => {
    await load(
      msg({ id: 'md', type: 'ToolStep', toolName: 'Read', bodyMode: 'Markdown', body: '**bold** body' }),
      msg({ id: 'code', type: 'ToolStep', toolName: 'Read', bodyMode: 'PlainText', language: 'csharp', body: 'var x = 1;' }),
      msg({ id: 'html', type: 'ToolStep', toolName: 'Read', bodyMode: 'Html', body: '<b>html</b><img src=x onerror="window.__pwned=1">' }),
    );
    for (const id of ['md', 'code', 'html']) await msgEl(id).locator('.toolstep-header').click();
    assert.equal(await msgEl('md').locator('strong').innerText(), 'bold');
    assert.equal(await msgEl('code').locator('code.hljs').count(), 1);
    assert.equal(await msgEl('html').locator('b').innerText(), 'html');
    assert.equal(await page.evaluate(() => window.__pwned), undefined);
  });

  test('clear removes everything; load replaces everything', async () => {
    await load(msg({ id: 'a', type: 'User', content: '1' }), msg({ id: 'b', type: 'User', content: '2' }));
    assert.equal(await page.locator('.msg').count(), 2);
    await deliver({ type: 'messages.clear' });
    assert.equal(await page.locator('.msg').count(), 0);
    await load(msg({ id: 'c', type: 'User', content: '3' }));
    assert.equal(await page.locator('.msg').count(), 1);
    await load(msg({ id: 'd', type: 'User', content: '4' }));
    assert.equal(await page.locator('.msg').count(), 1);
    assert.equal(await page.locator('#msg-c').count(), 0);
  });
});

describe('sync (end-of-turn self-heal)', () => {
  test('converges content, order and membership, then confirms with its id', async () => {
    await load(
      msg({ id: 'a', type: 'User', content: 'one' }),
      msg({ id: 'b', type: 'Assistant', content: 'old' }),
      msg({ id: 'gone', type: 'User', content: 'x' }),
    );
    await deliver({
      type: 'messages.sync',
      syncId: 'sync-1',
      messages: [
        msg({ id: 'new', type: 'User', content: 'first' }),
        msg({ id: 'b', type: 'Assistant', content: 'fresh' }),
        msg({ id: 'a', type: 'User', content: 'one' }),
      ],
    });

    await page.waitForFunction(() => document.querySelector('#msg-b .msg-content')?.textContent === 'fresh');
    const ids = await page.evaluate(() => [...document.querySelectorAll('.msg')].map((e) => e.id));
    assert.deepEqual(ids, ['msg-new', 'msg-b', 'msg-a']);
    assert.deepEqual(await lastOf('messages.synced'), { type: 'messages.synced', syncId: 'sync-1' });
  });

  test('a sync keeps what the user opened and does not yank the scroll position', async () => {
    const tool = msg({ id: 's', type: 'ToolStep', toolName: 'Read', bodyMode: 'PlainText', body: 'output' });
    await load(tool);
    await msgEl('s').locator('.toolstep-header').click();
    await deliver({ type: 'messages.sync', syncId: 'x', messages: [tool] });
    await page.waitForFunction(() => window.__host.sent.some((m) => m.type === 'messages.synced'));
    assert.equal(await msgEl('s').locator('.msg-body').isVisible(), true);
  });
});

describe('buttons and links', () => {
  test('Revert to here is offered only on user messages that have a checkpoint, and reports it', async () => {
    await load(
      msg({ id: 'u1', type: 'User', content: 'with', checkpointId: 'cp-7' }),
      msg({ id: 'u2', type: 'User', content: 'without' }),
      msg({ id: 'a1', type: 'Assistant', content: 'reply' }),
    );
    assert.equal(await page.locator('.msg-revert-btn').count(), 1);
    await msgEl('u1').hover();
    await msgEl('u1').locator('.msg-revert-btn').click();
    assert.deepEqual(await lastOf('revert'), { type: 'revert', checkpointId: 'cp-7' });
  });

  test('Fork from here is offered on user messages and finished replies, and reports the message', async () => {
    await load(
      msg({ id: 'u1', type: 'User', content: 'question', checkpointId: 'cp-7' }),
      msg({ id: 'a1', type: 'Assistant', content: 'reply' }),
      msg({ id: 't1', type: 'ToolStep', title: 'Read file' }),
      msg({ id: 'a2', type: 'Assistant', content: 'still typing', isStreaming: true }),
    );
    assert.equal(await page.locator('.msg-fork-btn').count(), 2);
    assert.equal(await msgEl('u1').locator('.msg-fork-btn').count(), 1);
    assert.equal(await msgEl('a1').locator('.msg-fork-btn').count(), 1);
    await msgEl('a1').hover();
    await msgEl('a1').locator('.msg-fork-btn').click();
    assert.deepEqual(await lastOf('fork'), { type: 'fork', messageId: 'a1' });
    await msgEl('u1').hover();
    await msgEl('u1').locator('.msg-fork-btn').click();
    assert.deepEqual(await lastOf('fork'), { type: 'fork', messageId: 'u1' });
  });

  test('Copy puts the message text on the clipboard and flashes a confirmation', async () => {
    await page.evaluate(() => {
      window.__copied = [];
      const original = document.execCommand.bind(document);
      document.execCommand = (command, ...rest) => {
        if (command === 'copy') window.__copied.push(document.activeElement?.value);
        return original(command, ...rest);
      };
    });
    await load(msg({ id: 'a1', type: 'Assistant', content: 'Copy **me**' }));
    await msgEl('a1').hover();
    await msgEl('a1').locator('.msg-copy-btn').click();
    assert.deepEqual(await page.evaluate(() => window.__copied), ['Copy me']);
    assert.match(await msgEl('a1').locator('.msg-copy-btn').getAttribute('class'), /copied/);
  });

  test('each fenced code block of a reply has a copy button that copies just that block', async () => {
    await page.evaluate(() => {
      window.__copied = [];
      const original = document.execCommand.bind(document);
      document.execCommand = (command, ...rest) => {
        if (command === 'copy') window.__copied.push(document.activeElement?.value);
        return original(command, ...rest);
      };
    });
    await load(
      msg({ id: 'a1', type: 'Assistant', content: ['Two blocks:', '', '```js', 'const a = 1;', '```', '', 'text', '', '```', 'plain <b>text</b>', '```'].join(String.fromCharCode(10)) }),
      msg({ id: 't1', type: 'ToolStep', title: 'Read file', body: ['```', 'inside a tool body', '```'].join(String.fromCharCode(10)) }),
    );
    await rendered('#msg-a1 pre');
    assert.equal(await msgEl('a1').locator('.code-copy-btn').count(), 2);
    assert.equal(await msgEl('t1').locator('.code-copy-btn').count(), 0);
    const blocks = msgEl('a1').locator('.code-block');
    assert.equal(await blocks.nth(0).locator('.code-copy-btn svg').count(), 1);
    await blocks.nth(1).hover();
    await blocks.nth(1).locator('.code-copy-btn').click();
    assert.deepEqual(await page.evaluate(() => window.__copied), ['plain <b>text</b>']);
    assert.match(await blocks.nth(1).locator('.code-copy-btn').getAttribute('class'), /copied/);
    await blocks.nth(0).hover();
    await blocks.nth(0).locator('.code-copy-btn').click();
    assert.deepEqual(await page.evaluate(() => window.__copied), ['plain <b>text</b>', 'const a = 1;']);
  });

  test('a code block the agent writes in HTML cannot bring a copy button of its own', async () => {
    await add(msg({ id: 'x', type: 'Assistant', content: '<button class="code-copy-btn">Copy</button>text' }));
    await rendered('#msg-x .msg-content');
    assert.equal(await msgEl('x').locator('.code-copy-btn').count(), 0);
  });

  test('copying a tool step includes its name, detail and (when open) its output', async () => {
    await page.evaluate(() => {
      window.__copied = [];
      const original = document.execCommand.bind(document);
      document.execCommand = (command, ...rest) => {
        if (command === 'copy') window.__copied.push(document.activeElement?.value);
        return original(command, ...rest);
      };
    });
    await load(msg({ id: 's', type: 'ToolStep', expanderTitle: 'Read a.cs', bodyMode: 'PlainText', body: 'the output' }));
    await msgEl('s').locator('.toolstep-header').hover();
    await msgEl('s').locator('.msg-copy-btn').click();
    assert.deepEqual(await page.evaluate(() => window.__copied.at(-1)), 'Read\na.cs');
    await msgEl('s').locator('.toolstep-header').click();
    await msgEl('s').locator('.msg-copy-btn').click();
    assert.match(await page.evaluate(() => window.__copied.at(-1)), /^Read\na\.cs\nthe output/);
  });

  test('file path links report the path to open, and only they do', async () => {
    await load(msg({ id: 'a1', type: 'Assistant', content: 'See `src/App/Program.cs:12` and [web](https://example.com)' }));
    await msgEl('a1').locator('a.file-link').click();
    assert.deepEqual(await lastOf('openFile'), { type: 'openFile', path: 'src/App/Program.cs:12' });
    const before = await countOf('openFile');
    // a web link is not a file link: the page does not post openFile for it
    await page.evaluate(() => document.querySelector('#msg-a1 a:not(.file-link)').addEventListener('click', (e) => e.preventDefault(), { once: true }));
    await msgEl('a1').locator('a:not(.file-link)').click();
    assert.equal(await countOf('openFile'), before);
  });

  test('clicking an attached image opens it full size; click or Esc closes it', async () => {
    await load(msg({ id: 'u1', type: 'User', content: 'pic', imageDataUris: ['data:image/gif;base64,R0lGODlhAQABAAAAACw='] }));
    await msgEl('u1').locator('img.user-img').click();
    assert.equal(await page.locator('.lightbox').count(), 1);
    await page.locator('.lightbox').click();
    assert.equal(await page.locator('.lightbox').count(), 0);

    await msgEl('u1').locator('img.user-img').click();
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('.lightbox').count(), 0);
  });

  test('right-clicking a selection offers Copy; without a selection nothing is shown', async () => {
    await page.evaluate(() => {
      window.__copied = [];
      const original = document.execCommand.bind(document);
      document.execCommand = (command, ...rest) => {
        if (command === 'copy') window.__copied.push(document.activeElement?.value);
        return original(command, ...rest);
      };
    });
    await load(msg({ id: 'a1', type: 'Assistant', content: 'select this text' }));
    await msgEl('a1').locator('.msg-content').click({ button: 'right' });
    assert.equal(await page.locator('.ctx-menu').count(), 0);

    await msgEl('a1').locator('.msg-content').selectText();
    await msgEl('a1').locator('.msg-content').click({ button: 'right' });
    assert.equal(await page.locator('.ctx-menu').count(), 1);
    await page.locator('.ctx-menu-item', { hasText: 'Copy' }).click();
    assert.deepEqual(await page.evaluate(() => window.__copied), ['select this text']);
    assert.equal(await page.locator('.ctx-menu').count(), 0);
  });
});

describe('scrolling', () => {
  const many = (n, prefix = 'm') =>
    Array.from({ length: n }, (_, i) => msg({ id: `${prefix}${i}`, type: 'User', content: `message ${i}\n`.repeat(4) }));
  const atBottom = () =>
    page.evaluate(() => {
      const el = document.querySelector('.message-list');
      return el.scrollHeight - el.scrollTop - el.clientHeight < 4;
    });
  const scrollable = () => page.evaluate(() => {
    const el = document.querySelector('.message-list');
    return el.scrollHeight > el.clientHeight + 100;
  });

  test('a loaded conversation starts at the end', async () => {
    await load(...many(40));
    await page.waitForFunction(() => {
      const el = document.querySelector('.message-list');
      return el.scrollHeight > el.clientHeight && el.scrollHeight - el.scrollTop - el.clientHeight < 4;
    });
    assert.equal(await scrollable(), true);
  });

  test('new messages and streaming growth follow the end', async () => {
    await load(...many(40));
    await add(msg({ id: 'stream', type: 'Assistant', isStreaming: true }));
    for (let i = 1; i <= 5; i++) {
      await deliver({ type: 'messages.content', id: 'stream', content: 'streamed line\n\n'.repeat(i * 4) });
      await page.waitForTimeout(40);
    }
    await page.waitForFunction(() => {
      const el = document.querySelector('.message-list');
      return el.scrollHeight - el.scrollTop - el.clientHeight < 4;
    });
    assert.equal(await atBottom(), true);
  });

  test('after the user scrolls up, new content does not pull the view down', async () => {
    await load(...many(40));
    await page.waitForFunction(() => document.querySelector('.message-list').scrollHeight > document.querySelector('.message-list').clientHeight);
    await page.evaluate(() => (document.querySelector('.message-list').scrollTop = 0));
    await page.waitForTimeout(80);
    await add(msg({ id: 'late', type: 'Assistant', content: 'a new reply\n\n'.repeat(10) }));
    await page.waitForSelector('#msg-late');
    await page.waitForTimeout(100);
    assert.equal(await page.evaluate(() => document.querySelector('.message-list').scrollTop), 0);
  });

  test('scrolling back to the end resumes following', async () => {
    await load(...many(40));
    await page.evaluate(() => (document.querySelector('.message-list').scrollTop = 0));
    await page.waitForTimeout(80);
    await page.evaluate(() => {
      const el = document.querySelector('.message-list');
      el.scrollTop = el.scrollHeight;
    });
    await page.waitForTimeout(80);
    await add(msg({ id: 'late', type: 'Assistant', content: 'another reply\n\n'.repeat(10) }));
    await page.waitForSelector('#msg-late');
    await page.waitForFunction(() => {
      const el = document.querySelector('.message-list');
      return el.scrollHeight - el.scrollTop - el.clientHeight < 4;
    });
  });
});

describe('untrusted content', () => {
  test('script and event handlers in agent markdown never run, and the page keeps working', async () => {
    await add(
      msg({
        id: 'evil',
        type: 'Assistant',
        content:
          'hello <img src=x onerror="window.__pwned=1"> <script>window.__pwned=2</script> ' +
          '[click](javascript:window.__pwned=3) <iframe src="https://evil.example"></iframe>',
      }),
    );
    await rendered('#msg-evil .msg-content');
    await page.waitForTimeout(200);
    assert.equal(await page.evaluate(() => window.__pwned), undefined);
    assert.equal(await msgEl('evil').locator('script, iframe').count(), 0);
    assert.equal(await msgEl('evil').locator('a[href^="javascript"]').count(), 0);
    assert.match(await msgEl('evil').locator('.msg-content').innerText(), /hello/);
  });

  test('agent-drawn form controls cannot appear in a message', async () => {
    await add(msg({ id: 'f', type: 'Assistant', content: '<button>Allow</button><input type="text"><form><textarea></textarea></form>' }));
    await rendered('#msg-f .msg-content');
    assert.equal(await msgEl('f').locator('button:not(.msg-copy-btn):not(.msg-action-btn), input, form, textarea').count(), 0);
  });

  test('web links in messages open in a new window and cannot reach back', async () => {
    await add(msg({ id: 'l', type: 'Assistant', content: '[docs](https://example.com)' }));
    await rendered('#msg-l a');
    const link = msgEl('l').locator('a');
    assert.equal(await link.getAttribute('target'), '_blank');
    assert.match(await link.getAttribute('rel'), /noopener/);
  });
});
