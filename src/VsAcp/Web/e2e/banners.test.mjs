// Banner prompts (permission / revert / elicitation / error) in the real page against the mock host.

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { countOf, deliver, lastOf, page, setState } from './harness.mjs';

const show = (banner) => deliver({ type: 'banner.show', banner });
const resolved = () => lastOf('banner.resolve');

const permission = {
  kind: 'permission',
  id: 'p1',
  title: 'Run: npm test',
  options: [
    { optionId: 'allow_once', name: 'Allow once', kind: 'allow_once' },
    { optionId: 'allow_always', name: 'Always allow', kind: 'allow_always' },
    { optionId: 'reject_once', name: 'Reject', kind: 'reject_once' },
  ],
};

describe('permission and error banners', () => {
  test('lettered rows per option; clicking a row answers by option id; the banner is taken down', async () => {
    await show(permission);
    assert.equal(await page.locator('.banner-title').innerText(), 'Run: npm test');
    assert.deepEqual(await page.locator('.opt-key').allInnerTexts(), ['A', 'B'], 'Reject is the quiet Esc action, not a row');
    assert.equal(await page.getByRole('button', { name: 'Reject' }).getAttribute('class'), 'banner-link');

    await page.getByRole('radio', { name: 'Allow once' }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'p1', result: { optionId: 'allow_once' } });
    assert.equal(await page.locator('.banner').count(), 0, 'the banner is taken down once answered');
  });

  test('keyboard: a letter only selects, Enter continues, Esc rejects; nothing is granted by a letter alone', async () => {
    await show(permission);
    await page.waitForFunction(() => document.activeElement?.classList.contains('banner'));
    assert.equal(await page.getByRole('button', { name: /^Continue/ }).count(), 0, 'nothing to confirm before a letter picked a row');

    await page.keyboard.press('Enter');
    assert.equal(await countOf('banner.resolve'), 0, 'Enter alone never answers a permission prompt');
    await page.keyboard.press('b');
    assert.equal(await countOf('banner.resolve'), 0);
    assert.equal(await page.getByRole('button', { name: /^Continue/ }).count(), 1);
    assert.equal(await page.getByRole('radio', { name: 'Always allow' }).getAttribute('aria-checked'), 'true');
    await page.keyboard.press('Enter');
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'p1', result: { optionId: 'allow_always' } });
    await page.waitForFunction(() => document.activeElement?.classList.contains('editor'), null, { timeout: 2000 });

    await show({ ...permission, id: 'p2' });
    await page.waitForFunction(() => document.activeElement?.classList.contains('banner'));
    await page.keyboard.press('Escape');
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'p2', result: { optionId: 'reject_once' } });
  });

  test('yes / no: two footer buttons, no rows, one click answers, Esc is No, Enter does nothing', async () => {
    const yesNo = {
      kind: 'permission',
      id: 'yn',
      title: 'rm old.cs && git status',
      options: [
        { optionId: 'yes', name: 'Yes', kind: 'allow_once' },
        { optionId: 'no', name: 'No', kind: 'reject_once' },
      ],
    };
    await show(yesNo);
    assert.equal(await page.locator('.opt').count(), 0);
    assert.equal((await page.locator('.banner-link').innerText()).replace(/\s+/g, ' '), 'No Esc');
    await page.waitForFunction(() => document.activeElement?.classList.contains('banner'));
    await page.keyboard.press('Enter');
    assert.equal(await countOf('banner.resolve'), 0);
    await page.getByRole('button', { name: 'Yes' }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'yn', result: { optionId: 'yes' } });

    await show({ ...yesNo, id: 'yn2' });
    await page.waitForFunction(() => document.activeElement?.classList.contains('banner'));
    await page.keyboard.press('Escape');
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'yn2', result: { optionId: 'no' } });
  });

  test('a single option is just its button', async () => {
    await show({ kind: 'permission', id: 's1', title: 'Proceed?', options: [{ optionId: 'ok', name: 'OK', kind: 'allow_once' }] });
    assert.equal(await page.locator('.banner-link').count(), 0);
    await page.getByRole('button', { name: 'OK' }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 's1', result: { optionId: 'ok' } });
  });

  test('a banner does not take the focus (or the keystrokes) from a message being typed', async () => {
    await page.locator('.editor').click();
    await page.keyboard.type('hello');
    await show(permission);
    await page.keyboard.type('a');
    assert.equal(await countOf('banner.resolve'), 0);
    assert.equal(await page.locator('.opt.selected').count(), 0);
    assert.match(await page.locator('.editor').innerText(), /helloa/);
  });

  test('banner.hide removes it; a newer banner replaces an older one', async () => {
    await show(permission);
    await show({ kind: 'error', id: 'e1', message: 'The chat view failed to update.' });
    assert.equal(await page.locator('.banner').count(), 1);
    assert.match(await page.locator('.banner').innerText(), /failed to update/);
    await deliver({ type: 'banner.hide' });
    assert.equal(await page.locator('.banner').count(), 0);
  });

  test('the banner sits between the message list and the busy line, above the composer', async () => {
    await setState({ busy: true });
    await show(permission);
    const order = await page.evaluate(() =>
      [...document.querySelector('#spa-root').children].map((c) => c.className.split(' ')[0]),
    );
    assert.deepEqual(order.slice(0, 4), ['message-list', 'banner', 'busy-bar', 'composer']);
  });

  test('error banner: Reload view answers with an empty result', async () => {
    await show({ kind: 'error', id: 'e1', message: 'Out of sync.' });
    await page.getByRole('button', { name: 'Reload view' }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'e1', result: {} });
  });
});

describe('revert confirmation', () => {
  const agentFiles = {
    deleted: 1,
    overwritten: 2,
    recreated: 1,
    files: [
      { action: 'delete', path: 'src\\new.cs' },
      { action: 'overwrite', path: 'src\\a.cs' },
      { action: 'overwrite', path: 'src\\b.cs' },
      { action: 'recreate', path: 'src\\gone.cs' },
    ],
    omitted: 7,
  };
  const otherFiles = {
    deleted: 0,
    overwritten: 2,
    recreated: 0,
    files: [
      { action: 'overwrite', path: 'notes.md' },
      { action: 'overwrite', path: 'TestResults\\run.trx' },
    ],
    omitted: 0,
  };
  const noFiles = { deleted: 0, overwritten: 0, recreated: 0, files: [], omitted: 0 };

  test("lists the agent's files, focuses Cancel, and Esc cancels", async () => {
    await show({ kind: 'revert', id: 'r1', preview: { agent: agentFiles, other: null, agentFilesKnown: true } });
    assert.equal(await page.locator('.banner-title').innerText(), 'Reverting will change files on disk');
    assert.match(await page.locator('.banner-text').innerText(), /^The agent's changes after this message will be undone/);
    assert.equal(await page.locator('.banner-strong').innerText(), '4 files: 1 deleted, 2 overwritten, 1 recreated');
    assert.equal(await page.locator('.revert-row').count(), 4);
    assert.equal(await page.locator('.revert-row').first().locator('.revert-action').innerText(), 'DELETE');
    assert.equal(await page.locator('.revert-more').innerText(), '… and 7 more');
    assert.equal(await page.locator('.revert-other').count(), 0);
    // focus is moved in an effect after the render
    await page.waitForFunction(() => document.activeElement?.textContent?.startsWith('Cancel'));

    await page.keyboard.press('Escape');
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'r1', result: { confirmed: false } });
  });

  test('other changes are listed collapsed and kept unless the box is ticked', async () => {
    await show({ kind: 'revert', id: 'r3', preview: { agent: agentFiles, other: otherFiles, agentFilesKnown: true } });
    assert.equal(await page.locator('.revert-other-toggle').innerText(), 'Other changes (2)');
    assert.equal(await page.locator('.revert-row').count(), 4); // only the agent's until expanded
    await page.locator('.revert-other-toggle').click();
    assert.equal(await page.locator('.revert-other .revert-row').count(), 2);

    const box = page.getByRole('checkbox', { name: 'Also revert other changes' });
    assert.equal(await box.isChecked(), false);
    await page.getByRole('button', { name: 'Revert', exact: true }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'r3', result: { confirmed: true, includeOther: false } });

    await show({ kind: 'revert', id: 'r4', preview: { agent: agentFiles, other: otherFiles, agentFilesKnown: true } });
    await page.getByRole('checkbox', { name: 'Also revert other changes' }).check();
    await page.getByRole('button', { name: 'Revert', exact: true }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'r4', result: { confirmed: true, includeOther: true } });
  });

  test('says so when only other changes differ', async () => {
    await show({ kind: 'revert', id: 'r5', preview: { agent: noFiles, other: otherFiles, agentFilesKnown: true } });
    assert.equal(await page.locator('.banner-title').innerText(), 'No file changes from the agent to revert');
    assert.equal(await page.locator('.banner-strong').count(), 0);
    assert.equal(await page.locator('.revert-other-toggle').innerText(), 'Other changes (2)');
  });

  test("older chats whose agent files are unknown revert everything, with no checkbox", async () => {
    await show({ kind: 'revert', id: 'r6', preview: { agent: agentFiles, other: null, agentFilesKnown: false } });
    assert.match(await page.locator('.banner-text').innerText(), /^Your workspace will be restored/);
    assert.equal(await page.getByRole('checkbox').count(), 0);
    await page.getByRole('button', { name: 'Revert', exact: true }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'r6', result: { confirmed: true } });
  });

  test('Revert confirms; no preview shows the generic prompt', async () => {
    await show({ kind: 'revert', id: 'r2', preview: null });
    assert.equal(await page.locator('.banner-title').innerText(), 'Revert to here?');
    assert.equal(await page.locator('.revert-files').count(), 0);
    await page.getByRole('button', { name: 'Revert', exact: true }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'r2', result: { confirmed: true } });
  });
});

describe('elicitation', () => {
  const form = {
    kind: 'elicitation',
    id: 'q1',
    title: 'Deployment settings',
    message: 'Which environment?',
    description: null,
    url: null,
    fields: [
      { name: 'name', kind: 'text', title: 'Name', required: true, description: 'Your name', maxLength: 50 },
      { name: 'count', kind: 'integer', title: 'Count', required: false, minimum: 1, maximum: 5 },
      {
        name: 'env',
        kind: 'select',
        title: 'Environment',
        required: true,
        options: [
          { value: 'dev', title: 'Development' },
          { value: 'prod', title: 'Production' },
        ],
      },
      {
        name: 'tags',
        kind: 'multiSelect',
        title: 'Tags',
        required: false,
        options: [
          { value: 'a', title: 'Alpha' },
          { value: 'b', title: 'Beta' },
        ],
      },
      { name: 'dry', kind: 'boolean', title: 'Dry run', required: false, defaultBoolean: true },
    ],
  };

  const next = () => page.getByRole('button', { name: /^(Continue|Submit)/ }).click();

  test('form: one field per step with a "1 of N" stepper; validation blocks Continue; the typed answer is sent', async () => {
    await show(form);
    assert.equal(await page.locator('.banner-step').innerText(), '1 of 5');
    assert.match(await page.locator('.elicit-label').innerText(), /^1\. Name \*/);
    assert.equal(await page.locator('.elicit-field').count(), 1, 'only the current field is shown');

    await next();
    assert.equal(await page.locator('.banner-error').innerText(), 'Name: this field is required');
    assert.equal(await page.locator('.banner-step').innerText(), '1 of 5');
    assert.equal(await countOf('banner.resolve'), 0);

    await page.locator('#f-name').fill('Ada');
    assert.equal(await page.locator('.banner-error').count(), 0, 'editing clears the error');
    await next();
    assert.equal(await page.locator('.banner-step').innerText(), '2 of 5');
    await page.waitForFunction(() => document.activeElement?.id === 'f-count', null, { timeout: 2000 });

    await page.locator('#f-count').fill('9');
    await next();
    assert.equal(await page.locator('.banner-error').innerText(), 'Count: must be at most 5');
    await page.locator('#f-count').fill('3');
    await next();

    // Environment (required single choice): the first option is preselected; pick the second by its letter
    assert.equal(await page.locator('.opt-key').allInnerTexts().then((k) => k.join('')), 'AB');
    await page.getByRole('radio', { name: 'Production' }).click();
    await next();

    // Tags (multi): rows toggle
    await page.getByRole('checkbox', { name: 'Beta' }).click();
    await next();

    // Dry run (boolean) defaults to Yes; last step is Submit
    assert.equal(await page.getByRole('radio', { name: 'Yes' }).getAttribute('aria-checked'), 'true');
    assert.equal(await page.getByRole('button', { name: /^Submit/ }).count(), 1);
    await next();
    assert.deepEqual(await resolved(), {
      type: 'banner.resolve',
      id: 'q1',
      result: { action: 'accept', values: { name: 'Ada', count: 3, env: 'prod', tags: ['b'], dry: true } },
    });
  });

  test('form: the stepper goes back, letters pick rows, Enter in a field continues, Esc / Decline declines', async () => {
    await show(form);
    await page.locator('#f-name').fill('Ada');
    await page.locator('#f-name').press('Enter');
    assert.equal(await page.locator('.banner-step').innerText(), '2 of 5');
    await page.getByRole('button', { name: 'Previous question' }).click();
    assert.equal(await page.locator('#f-name').inputValue(), 'Ada', 'answers are kept when stepping back');

    await page.getByRole('button', { name: 'Next question' }).click();
    await page.locator('#f-count').press('Enter'); // optional and empty: moves on
    await page.waitForSelector('.opt-list');
    await page.locator('.banner').focus();
    await page.keyboard.press('b');
    assert.equal(await page.getByRole('radio', { name: 'Production' }).getAttribute('aria-checked'), 'true');

    await page.keyboard.press('Escape');
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'q1', result: { action: 'decline' } });

    await show({ ...form, id: 'q2' });
    await page.getByRole('button', { name: /^Decline/ }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'q2', result: { action: 'decline' } });
  });

  test('form: a "<question>_custom" Other field is shown on its question\'s step, not as a step of its own', async () => {
    const other = (n) => ({ name: `question_${n}_custom`, kind: 'text', title: 'Other', required: false, description: 'Type your own answer' });
    await show({
      ...form,
      id: 'q4',
      fields: [
        { name: 'question_0', kind: 'select', title: 'Auth', required: true, options: [{ value: 'jwt', title: 'JWT' }, { value: 'key', title: 'API key' }] },
        other(0),
        { name: 'question_1', kind: 'multiSelect', title: 'Hardening', required: false, options: [{ value: 'https', title: 'HTTPS' }, { value: 'cors', title: 'CORS' }] },
        other(1),
      ],
    });
    assert.equal(await page.locator('.banner-step').innerText(), '1 of 2');
    assert.equal(await page.locator('#f-question_0_custom').getAttribute('placeholder'), 'Type your own answer');
    await page.getByRole('radio', { name: 'API key' }).click();
    await page.locator('#f-question_0_custom').fill('only on the admin routes');
    await page.locator('#f-question_0_custom').press('Enter');

    assert.equal(await page.locator('.banner-step').innerText(), '2 of 2');
    assert.equal(await page.locator('#f-question_1_custom').count(), 1);
    await page.getByRole('checkbox', { name: 'CORS' }).click();
    await next();
    assert.deepEqual(await resolved(), {
      type: 'banner.resolve',
      id: 'q4',
      result: { action: 'accept', values: { question_0: 'key', question_0_custom: 'only on the admin routes', question_1: ['cors'] } },
    });
  });

  test('form: a single field has no stepper and submits on Continue', async () => {
    await show({ ...form, id: 'q3', fields: [form.fields[2]] });
    assert.equal(await page.locator('.banner-step').count(), 0);
    assert.doesNotMatch(await page.locator('.elicit-label').innerText(), /^1\./);
    await next();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'q3', result: { action: 'accept', values: { env: 'dev' } } });
  });

  test('URL mode: the page asks the host to open it and never opens it itself', async () => {
    await show({
      kind: 'elicitation',
      id: 'u1',
      title: null,
      message: 'Sign in',
      description: null,
      url: 'https://example.com/auth',
      fields: [],
    });
    assert.equal(await page.locator('.banner-title').innerText(), 'The agent needs your input');
    assert.match(await page.locator('.banner').innerText(), /https:\/\/example\.com\/auth/);

    await page.getByRole('button', { name: 'Open in browser' }).click();
    assert.deepEqual(await lastOf('banner.openUrl'), { type: 'banner.openUrl', id: 'u1' });
    assert.equal(await countOf('banner.resolve'), 0, 'opening the page does not answer the request');

    await page.getByRole('button', { name: 'Done' }).click();
    assert.deepEqual(await resolved(), { type: 'banner.resolve', id: 'u1', result: { action: 'accept' } });
  });
});
