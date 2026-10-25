import { createServer } from 'vite';
import { chromium } from 'playwright-core';

const server = await createServer({ configFile: 'vite.dev.config.ts', server: { open: false }, logLevel: 'error' });
await server.listen();
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 420, height: 520 } });
await page.goto('http://localhost:5199/dev/index.html');
await page.waitForSelector('.editor[contenteditable="plaintext-only"]');
await page.evaluate(() => window.__host.deliver({ type: 'state', state: { ...window.__host.defaultState,
  busy: true, pendingFiles: [
    { fileName: 'HelloController.cs', filePath: 'C:\\src\\HelloController.cs', displayPath: 'src/HelloController.cs', kind: 'added', added: 9, removed: 0, source: 'agent' },
    { fileName: 'StatusBars.tsx', filePath: 'C:\\src\\StatusBars.tsx', displayPath: 'src/StatusBars.tsx', kind: 'modified', added: 73, removed: 20, source: 'agent' },
    { fileName: 'protocol.ts', filePath: 'C:\\src\\protocol.ts', displayPath: 'src/protocol.ts', kind: 'modified', added: 15, removed: 4, source: 'agent' },
    { fileName: 'styles.css', filePath: 'C:\\src\\styles.css', displayPath: 'src/styles.css', kind: 'modified', added: 145, removed: 26, source: 'agent' },
    { fileName: 'shot.mjs', filePath: 'C:\\src\\shot.mjs', displayPath: 'src/shot.mjs', kind: 'deleted', added: 0, removed: 31, source: 'scan' },
    { fileName: 'run.trx', filePath: 'C:\\src\\TestResults\\run.trx', displayPath: 'TestResults/run.trx', kind: 'added', added: 412, removed: 0, source: 'scan' },
    { fileName: 'NOTES', filePath: 'C:\\src\\NOTES', displayPath: 'NOTES', kind: 'modified', added: 2, removed: 0, source: 'scan' }], autoApprove: true,
  images: [{ id: 'i', fileName: 'clipboard.png' }], selections: [{ id: 's', label: 'Program.cs (25-30)', filePath: 'x' }] } }));
await page.evaluate(() => window.__host.deliver({ type: 'composer.set', text: 'fix the null check', commandName: 'review' }));
await page.keyboard.press('Control+End');
await page.screenshot({ path: 'e2e/.out/composer.png' });
await page.locator('.pending-group.other .pending-toggle').click();
await page.waitForTimeout(200); // let the chevron finish turning
await page.screenshot({ path: 'e2e/.out/pending-other.png' });
await page.locator('.pending-group.other .pending-toggle').click();
await page.locator('.editor').click();
await page.keyboard.press('Control+A');
await page.keyboard.type('/');
await page.screenshot({ path: 'e2e/.out/slash.png' });
await page.evaluate(() => {
  const r = document.documentElement.style;
  r.setProperty('--bg-primary', '#ffffff'); r.setProperty('--bg-secondary', '#f3f3f3'); r.setProperty('--bg-input', '#ffffff');
  r.setProperty('--text-primary', '#1e1e1e'); r.setProperty('--text-muted', '#717171'); r.setProperty('--border', '#cccccc'); r.setProperty('--accent', '#0066b8');
  document.body.style.background = '#ffffff'; document.body.style.color = '#1e1e1e';
});
await page.keyboard.press('Escape');
await page.screenshot({ path: 'e2e/.out/light.png' });
await browser.close();
await server.close();
