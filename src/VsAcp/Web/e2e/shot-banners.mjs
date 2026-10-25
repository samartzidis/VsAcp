// Visual check (not a test): renders each banner in the dev harness and writes PNGs to e2e/.out/.
import { createServer } from 'vite';
import { chromium } from 'playwright-core';

const server = await createServer({ configFile: 'vite.dev.config.ts', server: { open: false }, logLevel: 'error' });
await server.listen();
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 460, height: 620 } });
await page.goto('http://localhost:5199/dev/index.html');
await page.waitForSelector('.editor[contenteditable="plaintext-only"]');

const show = (banner) => page.evaluate((b) => window.__host.deliver({ type: 'banner.show', banner: b }), banner);
const shot = async (name) => {
  await page.waitForTimeout(150);
  await page.screenshot({ path: `e2e/.out/banner-${name}.png` });
};

await show({
  kind: 'permission',
  id: 'p',
  title: 'Run command: terraform apply -auto-approve in C:\\infra\\prod',
  options: [
    { optionId: 'a', name: 'Allow once', kind: 'allow_once' },
    { optionId: 'b', name: 'Allow always', kind: 'allow_always' },
    { optionId: 'c', name: 'Reject', kind: 'reject_once' },
  ],
});
await shot('permission');

await show({
  kind: 'permission',
  id: 'yn',
  title: 'rm DemoService/Handlers/HelloCommandHandler.cs DemoService.Contracts/HelloCommand.cs && ls DemoService/Handlers 2>&1; git status --short .',
  options: [
    { optionId: 'yes', name: 'Yes', kind: 'allow_once' },
    { optionId: 'no', name: 'No', kind: 'reject_once' },
  ],
});
await shot('permission-yesno');

await show({
  kind: 'revert',
  id: 'r',
  preview: {
    agent: {
      deleted: 1,
      overwritten: 3,
      recreated: 1,
      files: [
        { action: 'delete', path: 'src\\VsAcp.Core\\NewService.cs' },
        { action: 'overwrite', path: 'src\\VsAcp.Core\\Program.cs' },
        { action: 'overwrite', path: 'src\\VsAcp.Core\\Settings.cs' },
        { action: 'overwrite', path: 'README.md' },
        { action: 'recreate', path: 'docs\\old.md' },
      ],
      omitted: 0,
    },
    other: {
      deleted: 1,
      overwritten: 1,
      recreated: 0,
      files: [
        { action: 'delete', path: 'TestResults\\run.trx' },
        { action: 'overwrite', path: 'notes.md' },
      ],
      omitted: 0,
    },
    agentFilesKnown: true,
  },
});
await shot('revert');
await page.locator('.revert-other-toggle').click();
await page.getByRole('checkbox', { name: 'Also revert other changes' }).check();
await shot('revert-other');

await show({
  kind: 'elicitation',
  id: 'q',
  title: 'Deployment settings',
  message: 'Which environment should I deploy to?',
  description: 'The agent will use these values for this run only.',
  url: null,
  fields: [
    { name: 'name', kind: 'text', title: 'Release name', required: true, description: 'Shown in the pipeline', maxLength: 50, defaultText: 'v1.2.0' },
    { name: 'env', kind: 'select', title: 'Environment', required: true, options: [{ value: 'dev', title: 'Development' }, { value: 'prod', title: 'Production' }] },
    { name: 'tags', kind: 'multiSelect', title: 'Notify', required: false, options: [{ value: 'a', title: 'Alice' }, { value: 'b', title: 'Bob' }] },
    { name: 'dry', kind: 'boolean', title: 'Dry run', required: false, defaultBoolean: true },
  ],
});
await page.locator('#f-name').fill('');
await page.getByRole('button', { name: /^Continue/ }).click();
await shot('elicitation-error');
await page.locator('#f-name').fill('v1.2.0');
await page.getByRole('button', { name: /^Continue/ }).click();
await shot('elicitation');

await browser.close();
await server.close();
