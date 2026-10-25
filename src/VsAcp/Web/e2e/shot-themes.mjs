// Visual check (not a test): the sample conversation under approximations of VS themes, written to
// e2e/.out/theme-*.png. Each theme is delivered as the same `theme` message the host sends (anchors +
// scheme); the anchor values are hand-written approximations, not captured from Visual Studio.
import { createServer } from 'vite';
import { chromium } from 'playwright-core';

const themes = {
  dark: { scheme: 'dark', highContrast: false, colors: { '--bg-primary': '#1e1e1e', '--text-primary': '#d4d4d4', '--border': '#3f3f46', '--accent': '#3794ff', '--button-bg': '#264f78', '--button-fg': '#ffffff' } },
  light: { scheme: 'light', highContrast: false, colors: { '--bg-primary': '#ffffff', '--text-primary': '#000000', '--border': '#ccceDB', '--accent': '#0066b8', '--button-bg': '#3399ff', '--button-fg': '#ffffff' } },
  solarized: { scheme: 'dark', highContrast: false, colors: { '--bg-primary': '#002b36', '--text-primary': '#93a1a1', '--border': '#586e75', '--accent': '#268bd2', '--button-bg': '#268bd2', '--button-fg': '#fdf6e3' } },
  'high-contrast': { scheme: 'dark', highContrast: true, colors: { '--bg-primary': '#000000', '--text-primary': '#ffffff', '--border': '#6fc3df', '--accent': '#ffff00', '--button-bg': '#ffffff', '--button-fg': '#000000' } },
};

const server = await createServer({ configFile: 'vite.dev.config.ts', server: { open: false }, logLevel: 'error' });
await server.listen();
const browser = await chromium.launch({ channel: 'msedge', headless: true });

for (const [name, theme] of Object.entries(themes)) {
  const page = await browser.newPage({ viewport: { width: 480, height: 760 } });
  await page.goto('http://localhost:5199/dev/index.html?sample');
  await page.waitForSelector('.msg-assistant');
  await page.evaluate((t) => window.__host.deliver({ type: 'theme', ...t }), theme);
  await page.locator('.thinking-header').click();
  await page.locator('.msg-toolstep').first().locator('.toolstep-header').click();
  await page.evaluate(() =>
    window.__host.deliver({ type: 'banner.show', banner: { kind: 'permission', id: 'p', title: 'Run command: npm test', options: [
      { optionId: 'a', name: 'Allow once', kind: 'allow_once' }, { optionId: 'r', name: 'Reject', kind: 'reject_once' } ] } }));
  await page.waitForTimeout(200);
  await page.screenshot({ path: `e2e/.out/theme-${name}.png` });
  await page.close();
}

await browser.close();
await server.close();
