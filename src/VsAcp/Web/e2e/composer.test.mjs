// Drives the real page in Chromium (Edge engine = WebView2 engine) against the mock host.
// Run with: npm run test:e2e   (needs Microsoft Edge installed)

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { chip, countOf, deliver, editor, lastOf, page, setState } from './harness.mjs';

describe('editor basics', () => {
  test('shows the placeholder until something is typed', async () => {
    await assertVisible('.placeholder');
    await editor().click();
    await page.keyboard.type('hi');
    assert.equal(await page.locator('.placeholder').count(), 0);
  });

  test('typing reports the text to the host', async () => {
    await editor().click();
    await page.keyboard.type('hello world');
    const m = await lastOf('composer.changed');
    assert.deepEqual({ text: m.text, commandName: m.commandName }, { text: 'hello world', commandName: null });
  });

  test('Enter sends, Shift+Enter inserts a newline', async () => {
    await editor().click();
    await page.keyboard.type('a');
    await page.keyboard.press('Shift+Enter');
    await page.keyboard.type('b');
    assert.equal(await countOf('composer.send'), 0);
    assert.equal((await lastOf('composer.changed')).text, 'a\nb');

    await page.keyboard.press('Enter');
    const m = await lastOf('composer.send');
    assert.deepEqual({ text: m.text, commandName: m.commandName }, { text: 'a\nb', commandName: null });
  });

  test('host composer.set replaces text and chip; composer.insert adds plain text', async () => {
    await deliver({ type: 'composer.set', text: 'rest', commandName: 'review' });
    assert.equal(await chip().getAttribute('data-command'), 'review');
    assert.equal((await lastOf('composer.changed')).text, 'rest');
    await deliver({ type: 'composer.insert', text: '!' });
    assert.equal((await lastOf('composer.changed')).text, 'rest!');
    await deliver({ type: 'composer.set', text: '', commandName: null });
    assert.equal(await chip().count(), 0);
    assert.equal((await lastOf('composer.changed')).text, '');
  });

  test('is read-only while the host disables input', async () => {
    const editable = (value) =>
      page.waitForFunction((v) => document.querySelector('.editor').getAttribute('contenteditable') === v, value);
    await setState({ enabled: false, placeholder: 'Connecting to agent...' });
    await editable('false');
    assert.match(await page.locator('.placeholder').innerText(), /Connecting/);
    await setState({ enabled: true });
    await editable('plaintext-only');
  });
});

describe('slash commands', () => {
  test('typing / opens a filtered popup; Enter turns the pick into a chip', async () => {
    await editor().click();
    await page.keyboard.type('/');
    assert.equal(await page.locator('.slash-item').count(), 3);
    await page.keyboard.type('te');
    assert.equal(await page.locator('.slash-item').count(), 1);
    assert.match(await page.locator('.slash-item').innerText(), /test/);

    await page.keyboard.press('Enter');
    assert.equal(await chip().getAttribute('data-command'), 'test');
    assert.equal(await page.locator('.slash-popup').count(), 0);
    const m = await lastOf('composer.changed');
    assert.deepEqual({ text: m.text, commandName: m.commandName }, { text: '', commandName: 'test' });
    assert.equal(await countOf('composer.send'), 0, 'accepting a command must not send');
  });

  test('arrow keys move the selection and Tab accepts', async () => {
    await editor().click();
    await page.keyboard.type('/');
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Tab');
    assert.equal(await chip().getAttribute('data-command'), 'test');
  });

  test('Escape closes the popup without picking', async () => {
    await editor().click();
    await page.keyboard.type('/re');
    assert.equal(await page.locator('.slash-popup').count(), 1);
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('.slash-popup').count(), 0);
    assert.equal(await chip().count(), 0);
    assert.equal(await countOf('composer.stop'), 0);
  });

  test('picking mid-text keeps the surrounding text and the caret position', async () => {
    await editor().click();
    await page.keyboard.type('hello /te');
    await page.keyboard.press('Enter');
    assert.equal(await chip().getAttribute('data-command'), 'test');
    await page.keyboard.type('X');
    const m = await lastOf('composer.changed');
    assert.equal(m.text, 'hello X');
  });

  test('clicking a popup item picks it and keeps focus in the editor', async () => {
    await editor().click();
    await page.keyboard.type('/ex');
    await page.locator('.slash-item').first().click();
    assert.equal(await chip().getAttribute('data-command'), 'explain');
    await page.keyboard.type('why');
    assert.equal((await lastOf('composer.changed')).text, 'why');
  });

  test('Backspace right after the chip removes the whole chip', async () => {
    await deliver({ type: 'composer.set', text: 'rest', commandName: 'review' });
    await editor().click();
    await page.keyboard.press('Home');
    await page.keyboard.press('Backspace');
    assert.equal(await chip().count(), 0);
    assert.equal((await lastOf('composer.changed')).text, 'rest');
  });

  test('chip-only input: Backspace removes the chip', async () => {
    await deliver({ type: 'composer.set', text: '', commandName: 'review' });
    await editor().click();
    await page.keyboard.press('Backspace');
    assert.equal(await chip().count(), 0);
  });

  test('typing with the caret at the very start still lands behind the chip', async () => {
    await deliver({ type: 'composer.set', text: 'rest', commandName: 'review' });
    await editor().click();
    await page.keyboard.press('Home');
    await page.keyboard.type('X');
    const first = await editor().evaluate((el) => el.firstElementChild?.className);
    assert.equal(first, 'cmd-chip');
    assert.equal((await lastOf('composer.changed')).text, 'Xrest');
  });

  test('a command chip alone can be sent', async () => {
    await deliver({ type: 'composer.set', text: '', commandName: 'review' });
    await editor().click();
    await page.keyboard.press('Enter');
    const m = await lastOf('composer.send');
    assert.deepEqual({ text: m.text, commandName: m.commandName }, { text: '', commandName: 'review' });
  });
});

describe('paste', () => {
  const paste = (text, withImage = false) =>
    page.evaluate(
      ([t, img]) => {
        const dt = new DataTransfer();
        if (t) dt.setData('text/plain', t);
        if (img) dt.items.add(new File([new Uint8Array([1])], 'x.png', { type: 'image/png' }));
        const ev = new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true });
        document.querySelector('.editor').dispatchEvent(ev);
        return ev.defaultPrevented;
      },
      [text, withImage],
    );

  test('single-line text is inserted locally without asking the host', async () => {
    await editor().click();
    assert.equal(await paste('one line'), true);
    assert.equal((await lastOf('composer.changed')).text, 'one line');
    assert.equal(await countOf('composer.paste'), 0);
  });

  test('multi-line text is offered to the host, which may insert it', async () => {
    await editor().click();
    assert.equal(await paste('a\nb'), true);
    const m = await lastOf('composer.paste');
    assert.deepEqual({ text: m.text, hasImage: m.hasImage }, { text: 'a\nb', hasImage: false });
    assert.equal((await lastOf('composer.changed'))?.text ?? '', '', 'nothing is inserted until the host answers');
    await deliver({ type: 'composer.insert', text: 'a\nb' });
    assert.equal((await lastOf('composer.changed')).text, 'a\nb');
  });

  test('a clipboard image is always handed to the host', async () => {
    await editor().click();
    await paste('', true);
    assert.equal((await lastOf('composer.paste')).hasImage, true);
  });
});

describe('drag and drop', () => {
  const drag = (type, { files = [], text = '' } = {}) =>
    page.evaluate(
      ([t, f, tx]) => {
        const dt = new DataTransfer();
        for (const name of f) dt.items.add(new File(['x'], name));
        if (tx) dt.setData('text/plain', tx);
        const ev = new DragEvent(t, { dataTransfer: dt, bubbles: true, cancelable: true });
        document.querySelector('.composer-box').dispatchEvent(ev);
        return ev.defaultPrevented;
      },
      [type, files, text],
    );

  test('dropped files are handed to the host (so it can read their paths)', async () => {
    assert.equal(await drag('dragover', { files: ['a.cs'] }), true, 'dragover must be cancelled to allow the drop');
    assert.equal(await drag('drop', { files: ['a.cs', 'b.cs'] }), true, 'drop must be cancelled so the browser does not navigate');
    const m = await lastOf('composer.drop');
    assert.deepEqual(m.files, ['a.cs', 'b.cs']);
  });

  test('a drag without files (Solution Explorer) is still reported so the host can use its selection', async () => {
    await drag('drop');
    const m = await lastOf('composer.drop');
    assert.ok(m, 'composer.drop should be posted');
    assert.equal(m.files, undefined);
  });

  test('dropped text goes into the input instead', async () => {
    await drag('drop', { text: 'dragged words' });
    assert.equal(await countOf('composer.drop'), 0);
    assert.equal((await lastOf('composer.changed')).text, 'dragged words');
  });

  test('the box is highlighted while dragging over it and cleared after the drop', async () => {
    await drag('dragenter', { files: ['a.cs'] });
    assert.match(await page.locator('.composer-box').getAttribute('class'), /drop-target/);
    await drag('drop', { files: ['a.cs'] });
    assert.doesNotMatch(await page.locator('.composer-box').getAttribute('class'), /drop-target/);
  });
});

describe('toolbar, attachments and status', () => {
  test('Escape stops a running turn', async () => {
    await setState({ busy: true });
    assert.equal(await page.locator('.busy-bar').count(), 1);
    await editor().click();
    await page.keyboard.press('Escape');
    assert.equal(await countOf('composer.stop'), 1);
  });

  test('the busy bar shows a turning spinner before "Thinking..." and goes away with the turn', async () => {
    await setState({ busy: true });
    const spinner = page.locator('.busy-bar .busy-spinner');
    assert.equal(await spinner.count(), 1);
    assert.equal(await spinner.evaluate((el) => getComputedStyle(el).animationName), 'busy-spin');
    assert.equal((await page.locator('.busy-bar').innerText()).trim(), 'Thinking...');
    await setState({ busy: false });
    assert.equal(await page.locator('.busy-bar').count(), 0);
  });

  // Windows with "Animation effects" off reaches WebView2 as reduced motion; a spinner that stops or fades says nothing about progress
  test('the spinner turns at the same speed when the system asks for reduced motion', async () => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    try {
      await setState({ busy: true });
      const style = await page.locator('.busy-spinner').evaluate((el) => {
        const s = getComputedStyle(el);
        return { name: s.animationName, duration: s.animationDuration };
      });
      assert.equal(style.name, 'busy-spin');
      assert.equal(style.duration, '0.8s');
    } finally {
      await page.emulateMedia({ reducedMotion: 'no-preference' });
    }
  });

  test('the Stop button is only enabled while busy', async () => {
    // Send replaces Stop whenever there is something to send, so start from an empty composer
    await setState({ canSend: false });
    assert.equal(await page.locator('.tb-btn.stop').isDisabled(), true);
    await setState({ busy: true });
    assert.equal(await page.locator('.tb-btn.stop').isDisabled(), false);
    await page.locator('.tb-btn.stop').click();
    assert.equal(await countOf('composer.stop'), 1);
  });

  test('attachment chips render and report removal', async () => {
    await setState({
      images: [{ id: 'i1', fileName: 'clipboard.png' }],
      files: [{ id: 'f1', fileName: 'a.cs', filePath: 'C:\\a.cs' }],
      attachedFolders: [{ id: 'd1', folderName: 'assets', folderPath: 'C:\\assets' }],
      selections: [{ id: 's1', label: 'b.cs (3-9)', filePath: 'C:\\b.cs' }],
    });
    assert.equal(await page.locator('.attach-chip').count(), 4);
    await page.locator('.attach-chip', { hasText: 'b.cs (3-9)' }).locator('.attach-remove').click();
    assert.deepEqual(await lastOf('composer.remove'), { type: 'composer.remove', kind: 'selection', id: 's1' });
    await page.locator('.attach-chip', { hasText: 'assets' }).locator('.attach-remove').click();
    assert.deepEqual(await lastOf('composer.remove'), { type: 'composer.remove', kind: 'folder', id: 'd1' });
  });

  test('model dropdown lists the choices and reports the pick', async () => {
    await page.locator('.dd-btn[title="Select model"]').click();
    assert.equal(await page.locator('.dd-item').count(), 2);
    await page.locator('.dd-item', { hasText: 'gpt-5' }).click();
    assert.deepEqual(await lastOf('composer.setModel'), { type: 'composer.setModel', id: 'm|b' });
    assert.equal(await page.locator('.dd-list').count(), 0);
  });

  test('auto-approve toggle reports the new value', async () => {
    await page.locator('.tb-btn.auto').click();
    assert.deepEqual(await lastOf('composer.setAutoApprove'), { type: 'composer.setAutoApprove', value: true });
    await setState({ autoApprove: true });
    assert.match(await page.locator('.tb-btn.auto').innerText(), /Auto/);
  });

  test('active-context button toggles and dims when disabled', async () => {
    await page.locator('.ctx-btn').click();
    assert.equal(await countOf('composer.toggleActiveContext'), 1);
    await setState({ activeContext: { fileName: 'Program.cs', filePath: 'C:\\src\\Program.cs', enabled: false } });
    assert.match(await page.locator('.ctx-btn').getAttribute('class'), /off/);
  });

  test('pending files list shows the files with their line counts and its actions', async () => {
    const a = { fileName: 'A.cs', filePath: 'C:\\src\\A.cs', displayPath: 'src/A.cs', kind: 'modified', added: 9, removed: 0 };
    const b = { fileName: 'B.ts', filePath: 'C:\\src\\B.ts', displayPath: 'src/B.ts', kind: 'modified', added: 6, removed: 24 };
    assert.equal(await page.locator('.pending-files').count(), 0);
    await setState({ pendingFiles: [a, b] });
    assert.match(await page.locator('.pending-count').innerText(), /^2 Files$/);
    assert.equal(await page.locator('.pending-row').count(), 2);
    assert.equal(await page.locator('.pending-row').nth(0).locator('.pending-added').innerText(), '+9');
    assert.equal(await page.locator('.pending-row').nth(0).locator('.pending-removed').count(), 0);
    assert.equal(await page.locator('.pending-row').nth(1).locator('.pending-removed').innerText(), '-24');
    assert.equal(await page.locator('.pending-row').nth(0).locator('.file-badge').innerText(), 'C#');
    assert.equal(await page.locator('.pending-row').nth(1).locator('.file-badge').innerText(), 'TS');
    assert.equal(await page.locator('.pending-row').nth(1).locator('.pending-open').getAttribute('title'), 'src/B.ts');

    await page.getByRole('button', { name: 'Keep All' }).click();
    await page.getByRole('button', { name: 'Undo All' }).click();
    assert.equal(await countOf('changes.acceptAll'), 1);
    assert.equal(await countOf('changes.rejectAll'), 1);
    assert.equal((await lastOf('changes.acceptAll')).source, 'agent');
    assert.equal((await lastOf('changes.rejectAll')).source, 'agent');
    assert.equal(await page.getByRole('button', { name: 'Review' }).count(), 0);

    await page.locator('.pending-row').nth(1).locator('.pending-open').click();
    assert.equal((await lastOf('changes.open')).path, 'C:\\src\\B.ts');
    await page.locator('.pending-row').nth(0).hover();
    await page.getByRole('button', { name: 'Keep A.cs' }).click();
    assert.equal((await lastOf('changes.keep')).path, 'C:\\src\\A.cs');
    await page.locator('.pending-row').nth(1).hover();
    await page.getByRole('button', { name: 'Undo B.ts' }).click();
    assert.equal((await lastOf('changes.undo')).path, 'C:\\src\\B.ts');

    await setState({ pendingFiles: [a] });
    assert.match(await page.locator('.pending-count').innerText(), /^1 File$/);
  });

  test('created and deleted files are marked, and a deleted file cannot be opened', async () => {
    await setState({
      pendingFiles: [
        { fileName: 'New.cs', filePath: 'C:\\src\\New.cs', displayPath: 'src/New.cs', kind: 'added', added: 12, removed: 0 },
        { fileName: 'Old.cs', filePath: 'C:\\src\\Old.cs', displayPath: 'src/Old.cs', kind: 'deleted', added: 0, removed: 30 },
      ],
    });
    const added = page.locator('.pending-row').nth(0);
    const deleted = page.locator('.pending-row').nth(1);
    // the label is upper-cased by CSS, so compare the source text
    assert.equal(await added.locator('.pending-kind').textContent(), 'new');
    assert.equal(await added.locator('.pending-open').getAttribute('title'), 'src/New.cs (new)');
    assert.equal(await deleted.locator('.pending-kind').textContent(), 'deleted');
    assert.equal(await deleted.locator('.pending-open').getAttribute('title'), 'src/Old.cs (deleted)');
    assert.match(await deleted.getAttribute('class'), /deleted/);

    await added.locator('.pending-open').click();
    await deleted.locator('.pending-open').click();
    assert.equal(await countOf('changes.open'), 1);
    assert.equal((await lastOf('changes.open')).path, 'C:\\src\\New.cs');

    // Undo and Keep still work on both
    await deleted.hover();
    await page.getByRole('button', { name: 'Undo Old.cs' }).click();
    await page.getByRole('button', { name: 'Keep Old.cs' }).click();
    assert.equal((await lastOf('changes.undo')).path, 'C:\\src\\Old.cs');
    assert.equal((await lastOf('changes.keep')).path, 'C:\\src\\Old.cs');
  });

  test('changes found on disk are a collapsed "Other changes" group with its own actions', async () => {
    const mine = { fileName: 'A.cs', filePath: 'C:\\src\\A.cs', displayPath: 'src/A.cs', kind: 'modified', added: 2, removed: 1, source: 'agent' };
    const built = { fileName: 'out.trx', filePath: 'C:\\src\\out.trx', displayPath: 'src/out.trx', kind: 'added', added: 40, removed: 0, source: 'scan' };
    const gone = { fileName: 'Old.cs', filePath: 'C:\\src\\Old.cs', displayPath: 'src/Old.cs', kind: 'deleted', added: 0, removed: 9, source: 'scan' };
    await setState({ pendingFiles: [mine, built, gone] });

    // the agent's file counts alone in the main header; the other group starts collapsed
    assert.match(await page.locator('.pending-count').innerText(), /^1 File$/);
    assert.equal(await page.locator('.pending-other-count').innerText(), 'Other changes (2)');
    assert.equal(await page.locator('.pending-other-deleted').innerText(), '· 1 deleted');
    assert.equal(await page.locator('.pending-row').count(), 1);

    const otherToggle = page.locator('.pending-group.other .pending-toggle');
    assert.equal(await otherToggle.getAttribute('aria-expanded'), 'false');
    await otherToggle.click();
    assert.equal(await page.locator('.pending-group.other .pending-row').count(), 2);

    // each group's bulk actions say which group they are for
    await page.getByRole('button', { name: 'Keep All other changes', exact: true }).click();
    assert.equal((await lastOf('changes.acceptAll')).source, 'scan');
    await page.getByRole('button', { name: 'Undo All other changes', exact: true }).click();
    assert.equal((await lastOf('changes.rejectAll')).source, 'scan');
    await page.getByRole('button', { name: 'Keep All', exact: true }).click();
    assert.equal((await lastOf('changes.acceptAll')).source, 'agent');

    // rows in the other group work like any row
    const goneRow = page.locator('.pending-group.other .pending-row').nth(1);
    await goneRow.hover();
    await page.getByRole('button', { name: 'Undo Old.cs' }).click();
    assert.equal((await lastOf('changes.undo')).path, 'C:\\src\\Old.cs');
  });

  test('with only changes found on disk there is no "N Files" header', async () => {
    await setState({ pendingFiles: [{ fileName: 'log.txt', filePath: 'C:\\src\\log.txt', displayPath: 'src/log.txt', kind: 'modified', added: 3, removed: 0, source: 'scan' }] });
    assert.equal(await page.locator('.pending-count').count(), 0);
    assert.equal(await page.locator('.pending-other-count').innerText(), 'Other changes (1)');
    assert.equal(await page.locator('.pending-other-deleted').count(), 0);
  });

  test('pending files list collapses and expands from its header', async () => {
    await setState({ pendingFiles: [{ fileName: 'A.cs', filePath: 'C:\\src\\A.cs', displayPath: 'src/A.cs', kind: 'modified', added: 1, removed: 1 }] });
    assert.equal(await page.locator('.pending-row').count(), 1);
    await page.locator('.pending-toggle').click();
    assert.equal(await page.locator('.pending-row').count(), 0);
    assert.equal(await page.locator('.pending-toggle').getAttribute('aria-expanded'), 'false');
    await page.locator('.pending-toggle').click();
    assert.equal(await page.locator('.pending-row').count(), 1);
  });

  test('usage text is shown with its tooltip', async () => {
    assert.match(await page.locator('.usage').innerText(), /12\.3k/);
    assert.match(await page.locator('.usage').getAttribute('title'), /Context window/);
  });
});

describe('shared folders chip', () => {
  const folders = (over = {}) => ({
    items: [
      { path: 'C:\\libs\\shared', displayName: 'shared', source: 'auto' },
      { path: 'D:\\docs', displayName: 'docs', source: 'manual' },
    ],
    supported: true,
    canEdit: true,
    ...over,
  });

  test('is hidden when the host has no folder list (no local project)', async () => {
    assert.equal(await page.locator('.folders-btn').count(), 0);
  });

  test('sits on its own row below the input box, not in its toolbar', async () => {
    await setState({ folders: folders() });
    assert.equal(await page.locator('.composer-box .folders-btn').count(), 0);
    const box = await page.locator('.composer-box').boundingBox();
    const chipBox = await page.locator('.composer-footer .folders-btn').boundingBox();
    assert.ok(chipBox.y >= box.y + box.height, 'the chip is below the input box');
  });

  test('shows the count and lists each folder with its origin', async () => {
    await setState({ folders: folders() });
    assert.equal((await page.locator('.folders-count').innerText()).trim(), '2');
    assert.equal(await page.locator('.folders-pop').count(), 0);

    await page.locator('.folders-btn').click();
    assert.equal(await page.locator('.folders-row').count(), 2);
    assert.equal(await page.locator('.folders-row').nth(0).locator('.folders-name').innerText(), 'shared');
    assert.equal(await page.locator('.folders-row').nth(0).locator('.folders-tag').innerText(), 'project');
    assert.equal(await page.locator('.folders-row').nth(1).locator('.folders-tag').innerText(), 'added');
    assert.equal(await page.locator('.folders-row').nth(1).getAttribute('title'), 'D:\\docs');
    assert.match(await page.locator('.folders-hint').innerText(), /new chats/);
    assert.equal(await page.locator('.folders-warn').count(), 0);
  });

  test('Add folder and the remove buttons post their requests', async () => {
    await setState({ folders: folders() });
    await page.locator('.folders-btn').click();

    await page.getByRole('button', { name: 'Add folder...' }).click();
    assert.equal(await countOf('folders.add'), 1);

    await page.getByRole('button', { name: 'Stop sharing docs' }).click();
    assert.deepEqual(await lastOf('folders.remove'), { type: 'folders.remove', path: 'D:\\docs' });
  });

  test('the list follows the host and an empty list explains itself', async () => {
    await setState({ folders: folders() });
    await page.locator('.folders-btn').click();
    await setState({ folders: folders({ items: [] }) });
    assert.equal(await page.locator('.folders-row').count(), 0);
    assert.match(await page.locator('.folders-empty').innerText(), /solution folder is always shared/);
    assert.equal((await page.locator('.folders-count').innerText()).trim(), '0');
  });

  test('warns when the agent does not support extra folders', async () => {
    await setState({ folders: folders({ supported: false }) });
    assert.match(await page.locator('.folders-btn').getAttribute('class'), /warn/);
    assert.match(await page.locator('.folders-btn').getAttribute('title'), /does not support/);
    await page.locator('.folders-btn').click();
    assert.match(await page.locator('.folders-warn').innerText(), /does not support extra folders/);
  });

  test('is read-only when changes cannot be saved', async () => {
    await setState({ folders: folders({ canEdit: false }) });
    await page.locator('.folders-btn').click();
    assert.equal(await page.getByRole('button', { name: 'Add folder...' }).count(), 0);
    assert.equal(await page.locator('.folders-x').count(), 0);
  });

  test('Escape and a click elsewhere close the popover', async () => {
    await setState({ folders: folders() });
    await page.locator('.folders-btn').click();
    assert.equal(await page.locator('.folders-pop').count(), 1);
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('.folders-pop').count(), 0);

    await page.locator('.folders-btn').click();
    // the popover opens over the editor, so click the top of the page (message list area) instead
    await page.mouse.click(5, 5);
    assert.equal(await page.locator('.folders-pop').count(), 0);
  });
});

describe('dropping folders on the folders chip', () => {
  const state = (over = {}) => ({
    items: [{ path: 'C:\\libs\\shared', displayName: 'shared', source: 'auto' }],
    supported: true,
    canEdit: true,
    ...over,
  });
  const dragOn = (selector, type, files = ['docs']) =>
    page.evaluate(
      ([sel, t, f]) => {
        const dt = new DataTransfer();
        for (const name of f) dt.items.add(new File([''], name));
        const ev = new DragEvent(t, { dataTransfer: dt, bubbles: true, cancelable: true });
        document.querySelector(sel).dispatchEvent(ev);
        return { prevented: ev.defaultPrevented, dropEffect: dt.dropEffect };
      },
      [selector, type, files],
    );
  const cls = (selector) => page.locator(selector).getAttribute('class');

  test('a drop on the chip goes to folders.drop, not to the attachments', async () => {
    await setState({ folders: state() });
    const over = await dragOn('.folders-btn', 'dragover');
    assert.equal(over.prevented, true);
    await dragOn('.folders-btn', 'drop', ['docs', 'libs']);
    assert.deepEqual((await lastOf('folders.drop')).files, ['docs', 'libs']);
    assert.equal(await countOf('composer.drop'), 0);
  });

  test('the chip is highlighted instead of the box while dragging over it', async () => {
    await setState({ folders: state() });
    await dragOn('.folders-btn', 'dragenter');
    assert.match(await cls('.folders-btn'), /drop-target/);
    assert.doesNotMatch(await cls('.composer-box'), /drop-target/);

    await dragOn('.composer-box', 'dragenter');
    assert.doesNotMatch(await cls('.folders-btn'), /drop-target/);
    assert.match(await cls('.composer-box'), /drop-target/);

    await dragOn('.folders-btn', 'drop');
    assert.doesNotMatch(await cls('.folders-btn'), /drop-target/);
  });

  test('the open popover takes drops too', async () => {
    await setState({ folders: state() });
    await page.locator('.folders-btn').click();
    await dragOn('.folders-pop', 'drop');
    assert.equal(await countOf('folders.drop'), 1);
  });

  test('a read-only chip takes nothing', async () => {
    await setState({ folders: state({ canEdit: false }) });
    await dragOn('.folders-btn', 'dragenter');
    assert.doesNotMatch(await cls('.folders-btn'), /drop-target/);
    assert.doesNotMatch(await cls('.composer-box'), /drop-target/);
    assert.equal((await dragOn('.folders-btn', 'dragover')).dropEffect, 'none');
    await dragOn('.folders-btn', 'drop');
    assert.equal(await countOf('folders.drop'), 0);
    assert.equal(await countOf('composer.drop'), 0);
  });

  test('a drag without paths (Solution Explorer) is still reported', async () => {
    await setState({ folders: state() });
    await dragOn('.folders-btn', 'drop', []);
    const m = await lastOf('folders.drop');
    assert.ok(m, 'folders.drop should be posted');
    assert.equal(m.files, undefined);
  });

  test('drops elsewhere still attach files', async () => {
    await setState({ folders: state() });
    await dragOn('.composer-box', 'drop', ['a.cs']);
    assert.equal(await countOf('folders.drop'), 0);
    assert.deepEqual((await lastOf('composer.drop')).files, ['a.cs']);
  });
});

describe('session folders button (this chat\'s active folders)', () => {
  const shared = { path: 'C:\\libs\\shared', displayName: 'shared', source: 'active' };
  const docs = { path: 'D:\\docs', displayName: 'docs', source: 'active' };
  const configured = (items, over = {}) => ({ items, supported: true, canEdit: true, ...over });
  const cfg = (path, displayName, source = 'auto') => ({ path, displayName, source });

  test('is hidden until the session exists, and when it has no folders', async () => {
    await setState({ folders: configured([cfg(shared.path, 'shared')]), activeFolders: null });
    assert.equal(await page.locator('.session-folders').count(), 0);
    await setState({ activeFolders: [] });
    assert.equal(await page.locator('.session-folders').count(), 0);
  });

  test('lives in the input box toolbar and shows a read-only list', async () => {
    await setState({ folders: configured([cfg(shared.path, 'shared'), cfg(docs.path, 'docs', 'manual')]), activeFolders: [shared, docs] });
    assert.equal(await page.locator('.composer-box .session-folders').count(), 1);
    assert.equal((await page.locator('.session-folders .folders-count').innerText()).trim(), '2');

    await page.locator('.session-folders .folders-session-btn').click();
    assert.equal(await page.locator('.session-folders .folders-row').count(), 2);
    assert.equal(await page.locator('.session-folders .folders-row').nth(1).getAttribute('title'), 'D:\\docs');
    assert.equal(await page.locator('.session-folders .folders-x, .session-folders .folders-add').count(), 0);
    assert.equal(await page.locator('.session-folders-differs').count(), 0);
  });

  test('says so when the configured list no longer matches the session', async () => {
    await setState({ folders: configured([cfg(shared.path, 'shared'), cfg(docs.path, 'docs', 'manual')]), activeFolders: [shared] });
    await page.locator('.session-folders .folders-session-btn').click();
    assert.equal(await page.locator('.session-folders .folders-row').count(), 1);
    assert.match(await page.locator('.session-folders-differs').innerText(), /new chats/);
  });

  test('is amber and explains when the agent cannot take the folders', async () => {
    await setState({ folders: configured([cfg(shared.path, 'shared')], { supported: false }), activeFolders: [] });
    assert.match(await page.locator('.session-folders .folders-session-btn').getAttribute('class'), /warn/);
    await page.locator('.session-folders .folders-session-btn').click();
    assert.match(await page.locator('.session-folders .folders-warn').innerText(), /does not support extra folders/);
    assert.equal(await page.locator('.session-folders .folders-row').count(), 0);
  });

  test('Escape closes it', async () => {
    await setState({ folders: configured([cfg(shared.path, 'shared')]), activeFolders: [shared] });
    await page.locator('.session-folders .folders-session-btn').click();
    assert.equal(await page.locator('.session-folders .folders-pop').count(), 1);
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('.session-folders .folders-pop').count(), 0);
  });
});

async function assertVisible(selector) {
  assert.equal(await page.locator(selector).isVisible(), true, `${selector} should be visible`);
}
