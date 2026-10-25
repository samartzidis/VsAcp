// Colour system checks in a real browser: the host pushes only anchors + a scheme, and the page must stay
// readable (WCAG contrast) in dark, light and high-contrast. The anchor sets approximate what
// ChatSessionControl.ApplyThemeColors sends for typical themes; they are not captured from Visual Studio.

import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { deliver, page } from './harness.mjs';

const themes = {
  dark: { scheme: 'dark', highContrast: false, colors: { '--bg-primary': '#1e1e1e', '--text-primary': '#d4d4d4', '--border': '#3f3f46', '--accent': '#3794ff', '--button-bg': '#264f78', '--button-fg': '#ffffff' } },
  light: { scheme: 'light', highContrast: false, colors: { '--bg-primary': '#ffffff', '--text-primary': '#000000', '--border': '#ccceDB', '--accent': '#0066b8', '--button-bg': '#3399ff', '--button-fg': '#ffffff' } },
  // a coloured, non-default dark theme: the page must follow its colours, not assume VS Dark
  solarized: { scheme: 'dark', highContrast: false, colors: { '--bg-primary': '#002b36', '--text-primary': '#93a1a1', '--border': '#586e75', '--accent': '#268bd2', '--button-bg': '#268bd2', '--button-fg': '#fdf6e3' } },
  'high-contrast': { scheme: 'dark', highContrast: true, colors: { '--bg-primary': '#000000', '--text-primary': '#ffffff', '--border': '#6fc3df', '--accent': '#ffff00', '--button-bg': '#ffffff', '--button-fg': '#000000' } },
};

const code = [
  '```csharp',
  'public class A : B // note',
  '{ var s = "text"; int n = 42; if (x) return null; }',
  '```',
].join('\n');

async function load(theme) {
  await deliver({ type: 'theme', ...theme });
  await deliver({ type: 'messages.load', messages: [{
    id: 'a', type: 'Assistant', content: code + '\n\nplain **bold** [link](https://example.com) `inline`', toolName: null, title: '', status: 'Success',
    expanderTitle: '', bodyMode: 'Markdown', body: null, language: null, isStreaming: false, imageDataUris: null, fileNames: null,
    selections: null, checkpointId: null,
  }] });
  await page.waitForSelector('#msg-a .hljs-keyword');
}

/** WCAG contrast ratio of every colour sampled in the page against the background it sits on. */
async function contrasts() {
  return page.evaluate(() => {
    const parse = (c) => {
      const m = c.match(/rgba?\(([\d.]+)[, ]+([\d.]+)[, ]+([\d.]+)/) ?? c.match(/color\(srgb ([\d.]+) ([\d.]+) ([\d.]+)/);
      const v = m.slice(1, 4).map(Number);
      return c.startsWith('color(srgb') ? v.map((x) => x * 255) : v;
    };
    const lum = ([r, g, b]) => {
      const f = (x) => ((x /= 255) <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4);
      return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
    };
    const ratio = (a, b) => {
      const [x, y] = [lum(parse(a)), lum(parse(b))].sort((p, q) => q - p);
      return (x + 0.05) / (y + 0.05);
    };
    const bgOf = (el) => {
      for (let e = el; e; e = e.parentElement) {
        const bg = getComputedStyle(e).backgroundColor;
        if (bg && !/rgba\(.*, 0\)$/.test(bg) && bg !== 'transparent') return bg;
      }
      return getComputedStyle(document.body).backgroundColor;
    };
    const sample = (selector) => {
      const el = document.querySelector(selector);
      if (!el) return null;
      return { fg: getComputedStyle(el).color, bg: bgOf(el), ratio: ratio(getComputedStyle(el).color, bgOf(el)) };
    };
    const out = {};
    for (const [name, selector] of Object.entries({
      body: '#msg-a .md-content > p',
      keyword: '#msg-a .hljs-keyword',
      string: '#msg-a .hljs-string',
      number: '#msg-a .hljs-number',
      comment: '#msg-a .hljs-comment',
      type: '#msg-a .hljs-title',
      link: '#msg-a a',
      composerHint: '.placeholder',
    })) out[name] = sample(selector);
    return out;
  });
}

describe('every scheme stays readable', () => {
  for (const [name, theme] of Object.entries(themes)) {
    test(`${name}: text and code colours reach 4.5:1 (3:1 for comments)`, async () => {
      await load(theme);
      const c = await contrasts();
      const failures = Object.entries(c)
        .filter(([, s]) => s)
        .map(([token, s]) => ({ token, s, need: token === 'comment' ? 3 : 4.5 }))
        .filter(({ s, need }) => s.ratio < need)
        .map(({ token, s, need }) => `${token}: ${s.fg} on ${s.bg} = ${s.ratio.toFixed(2)}:1 (< ${need})`);
      assert.deepEqual(failures, [], `${name}: low contrast`);
    });
  }
});

describe('the page follows the host', () => {
  test('code blocks switch palette with the scheme, not only the text colour', async () => {
    await load(themes.dark);
    const dark = await page.evaluate(() => getComputedStyle(document.querySelector('#msg-a .hljs-keyword')).color);
    await load(themes.light);
    const light = await page.evaluate(() => getComputedStyle(document.querySelector('#msg-a .hljs-keyword')).color);
    assert.notEqual(dark, light);
  });

  test('a custom theme background is used as is (no assumed VS Dark surfaces)', async () => {
    await load(themes.solarized);
    assert.equal(await page.evaluate(() => getComputedStyle(document.body).backgroundColor), 'rgb(0, 43, 54)');
    // derived surfaces are mixed from the anchors, so they stay in the same family of colour
    const secondary = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg-secondary').trim());
    assert.match(secondary, /color-mix|rgb|#/);
  });

  test('switching theme back and forth leaves nothing behind from the previous one', async () => {
    await load(themes['high-contrast']);
    assert.equal(await page.evaluate(() => document.documentElement.dataset.contrast), 'high');
    await load(themes.dark);
    assert.equal(await page.evaluate(() => document.documentElement.dataset.contrast), 'normal');
    assert.equal(await page.evaluate(() => document.documentElement.style.getPropertyValue('--accent')), '#3794ff');
  });

  test('high contrast: muted text is full contrast and the banner buttons are outlined', async () => {
    await load(themes['high-contrast']);
    await deliver({ type: 'banner.show', banner: { kind: 'permission', id: 'p', title: 'Run?', options: [
      { optionId: 'a', name: 'Allow', kind: 'allow_once' }, { optionId: 'r', name: 'Reject', kind: 'reject_once' } ] } });
    await page.waitForSelector('.banner-btn');
    const muted = await page.evaluate(() => getComputedStyle(document.querySelector('.placeholder')).color);
    const text = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--text-primary').trim());
    assert.equal(muted, 'rgb(255, 255, 255)');
    assert.equal(text, '#ffffff');
    const border = await page.evaluate(() => getComputedStyle(document.querySelector('.banner-btn')).borderTopWidth);
    assert.equal(border, '2px');
  });
});
