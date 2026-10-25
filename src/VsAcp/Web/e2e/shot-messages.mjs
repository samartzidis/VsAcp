// Visual check (not a test): renders the sample conversation and writes PNGs to e2e/.out/.
import { createServer } from 'vite';
import { chromium } from 'playwright-core';

const server = await createServer({ configFile: 'vite.dev.config.ts', server: { open: false }, logLevel: 'error' });
await server.listen();
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 480, height: 760 } });
page.on('console', (m) => m.type() === 'error' && console.log('console error:', m.text()));
page.on('pageerror', (e) => console.log('page error:', e.message));
await page.goto('http://localhost:5199/dev/index.html?sample');
await page.waitForSelector('.msg-assistant');
await page.screenshot({ path: 'e2e/.out/messages.png' });

await page.locator('.thinking-header').click();
await page.locator('.msg-toolstep').first().locator('.toolstep-header').click();
await page.waitForTimeout(150);
await page.screenshot({ path: 'e2e/.out/messages-expanded.png' });

await browser.close();
await server.close();
