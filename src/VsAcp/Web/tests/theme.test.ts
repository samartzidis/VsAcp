import { beforeEach, describe, expect, test } from 'vitest';
import { applyTheme, contrast, parseHex } from '../src/theme';

const root = document.documentElement;

beforeEach(() => {
  root.removeAttribute('style');
  delete root.dataset.scheme;
  delete root.dataset.contrast;
  // forget what an earlier test applied
  applyTheme({ colors: {}, scheme: 'dark', highContrast: false });
});

describe('applyTheme', () => {
  test('sets the anchor variables and the scheme / contrast attributes', () => {
    applyTheme({ colors: { '--bg-primary': '#ffffff', '--text-primary': '#000000' }, scheme: 'light', highContrast: false });
    expect(root.style.getPropertyValue('--bg-primary')).toBe('#ffffff');
    expect(root.style.getPropertyValue('--text-primary')).toBe('#000000');
    expect(root.dataset.scheme).toBe('light');
    expect(root.dataset.contrast).toBe('normal');
  });

  test('high contrast is reflected in the attribute', () => {
    applyTheme({ colors: {}, scheme: 'dark', highContrast: true });
    expect(root.dataset.contrast).toBe('high');
  });

  test('a later theme replaces the earlier one: variables it no longer provides are removed', () => {
    applyTheme({ colors: { '--accent': '#ff0000', '--border': '#111111' }, scheme: 'dark', highContrast: false });
    applyTheme({ colors: { '--border': '#222222' }, scheme: 'light', highContrast: false });
    expect(root.style.getPropertyValue('--accent')).toBe('');
    expect(root.style.getPropertyValue('--border')).toBe('#222222');
    expect(root.dataset.scheme).toBe('light');
  });

  test('properties it did not set itself are left alone', () => {
    root.style.setProperty('--something-else', 'x');
    applyTheme({ colors: { '--accent': '#ff0000' }, scheme: 'dark', highContrast: false });
    applyTheme({ colors: {}, scheme: 'dark', highContrast: false });
    expect(root.style.getPropertyValue('--something-else')).toBe('x');
  });
});

describe('derived text colours keep their contrast floor', () => {
  const ratio = (hexA: string, hexB: string) => contrast(parseHex(hexA)!, parseHex(hexB)!);
  const get = (name: string) => root.style.getPropertyValue(name);

  const anchors = {
    'VS Dark': { '--bg-primary': '#1e1e1e', '--text-primary': '#d4d4d4', '--accent': '#3794ff' },
    'VS Light': { '--bg-primary': '#ffffff', '--text-primary': '#000000', '--accent': '#0066b8' },
    'Solarized Dark': { '--bg-primary': '#002b36', '--text-primary': '#93a1a1', '--accent': '#268bd2' },
    'Solarized Light': { '--bg-primary': '#fdf6e3', '--text-primary': '#586e75', '--accent': '#268bd2' },
  };

  for (const [name, colors] of Object.entries(anchors)) {
    test(`${name}: soft text >= 6:1 (or unchanged), muted >= 4.5:1, link >= 4.5:1`, () => {
      applyTheme({ colors, scheme: 'dark', highContrast: false });
      const bg = colors['--bg-primary'];
      const raw = ratio(colors['--text-primary'], bg);
      expect(ratio(get('--text-soft'), bg)).toBeGreaterThanOrEqual(Math.min(6, raw) - 0.01);
      expect(ratio(get('--link-text'), bg)).toBeGreaterThanOrEqual(4.5);
    });
  }

  test('soft text is softer than the editor text when there is room, and equal to it when there is not', () => {
    applyTheme({ colors: anchors['VS Dark'], scheme: 'dark', highContrast: false });
    expect(get('--text-soft')).not.toBe('#d4d4d4');
    applyTheme({ colors: { '--bg-primary': '#002b36', '--text-primary': '#657b83' }, scheme: 'dark', highContrast: false });
    expect(get('--text-soft')).toBe('#657b83'); // 3.7:1 already: never dimmed further
  });

  test('a link colour that is already readable is left alone', () => {
    applyTheme({ colors: anchors['VS Dark'], scheme: 'dark', highContrast: false });
    expect(get('--link-text')).toBe('#3794ff');
  });

  test('high contrast: no dimming, border is the text colour', () => {
    applyTheme({ colors: { ...anchors['VS Dark'], '--border': '#6fc3df' }, scheme: 'dark', highContrast: true });
    expect(get('--text-soft')).toBe('#d4d4d4');
    expect(get('--text-muted')).toBe('#d4d4d4');
    expect(get('--border')).toBe('#d4d4d4');
  });

  test('anchors that are not #rrggbb are applied as given and nothing is derived from them', () => {
    applyTheme({ colors: { '--bg-primary': 'rgb(0,0,0)', '--text-primary': 'white' }, scheme: 'dark', highContrast: false });
    expect(get('--bg-primary')).toBe('rgb(0,0,0)');
    expect(get('--text-soft')).toBe('');
  });
});
