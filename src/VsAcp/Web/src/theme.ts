import type { ThemeMessage } from './bridge/protocol';

// ---- colour maths ------------------------------------------------------------------------------------
// The host sends #rrggbb anchors. Three text colours depend on how those anchors combine (a fixed blend
// that reads well on VS Dark is unreadable on Solarized), so they are computed here against a contrast
// floor instead of being guessed in CSS. Everything else in theme.css is a plain derivation.

type Rgb = readonly [number, number, number];

export function parseHex(hex: string | undefined): Rgb | null {
  const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex?.trim() ?? '');
  return m ? [parseInt(m[1]!, 16), parseInt(m[2]!, 16), parseInt(m[3]!, 16)] : null;
}

function toHex([r, g, b]: Rgb): string {
  const h = (n: number) => Math.round(Math.min(255, Math.max(0, n))).toString(16).padStart(2, '0');
  return `#${h(r)}${h(g)}${h(b)}`;
}

/** `amount` of `to` mixed into `from` (0 = from, 1 = to), rounded to whole channels so the contrast measured is the colour shown. */
function mix(from: Rgb, to: Rgb, amount: number): Rgb {
  const channel = (i: 0 | 1 | 2) => Math.round(from[i] + (to[i] - from[i]) * amount);
  return [channel(0), channel(1), channel(2)];
}

function luminance([r, g, b]: Rgb): number {
  const f = (c: number) => ((c /= 255) <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
}

/** WCAG contrast ratio, 1 to 21. */
export function contrast(a: Rgb, b: Rgb): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

/**
 * Moves `from` toward `to` as far as `maxAmount` allows while keeping at least `minRatio` contrast against
 * `against`. If `from` does not reach `minRatio` to begin with, it is returned unchanged.
 */
function mixWithFloor(from: Rgb, to: Rgb, maxAmount: number, against: Rgb, minRatio: number): Rgb {
  let best = from;
  for (let amount = 0.01; amount <= maxAmount + 1e-9; amount += 0.01) {
    const candidate = mix(from, to, amount);
    if (contrast(candidate, against) < minRatio) break;
    best = candidate;
  }
  return best;
}

/**
 * Moves `from` toward `to` until it reaches `minRatio` against `against` (or fully reaches `to`).
 * Returns it unchanged when it already has that much contrast.
 */
function lift(from: Rgb, to: Rgb, against: Rgb, minRatio: number): Rgb {
  if (contrast(from, against) >= minRatio) return from;
  for (let amount = 0.02; amount < 1; amount += 0.02) {
    const candidate = mix(from, to, amount);
    if (contrast(candidate, against) >= minRatio) return candidate;
  }
  return to;
}

/** Contrast floors (WCAG AA for text is 4.5; soft reply text keeps a margin above it). */
const MIN_BODY = 6;
const MIN_TEXT = 4.5;

/**
 * Text colours that depend on the anchors:
 *  --text-soft   reply text, a little softer than the editor text, never below MIN_BODY
 *  --text-muted  secondary text, dimmer still, but at least MIN_TEXT on the lightest surface it sits on
 *  --link-text   the theme's link colour, lifted toward the text colour if it is too dim on this background
 * In high contrast nothing is dimmed.
 */
function derivedTextColors(colors: Record<string, string>, highContrast: boolean): Record<string, string> {
  const bg = parseHex(colors['--bg-primary']);
  const fg = parseHex(colors['--text-primary']);
  if (!bg || !fg) return {};

  const out: Record<string, string> = {};
  if (highContrast) {
    out['--text-soft'] = toHex(fg);
    out['--text-muted'] = toHex(fg);
    out['--border'] = toHex(fg);
  } else {
    out['--text-soft'] = toHex(mixWithFloor(fg, bg, 0.18, bg, MIN_BODY));
    // the input surface is the lightest tint the muted text is placed on (see --bg-input in theme.css)
    const surface = mix(bg, fg, 0.13);
    out['--text-muted'] = toHex(mixWithFloor(fg, bg, 0.42, surface, MIN_TEXT));
  }

  const accent = parseHex(colors['--accent']);
  if (accent) out['--link-text'] = toHex(lift(accent, fg, bg, MIN_TEXT));
  return out;
}

// ---- applying a theme --------------------------------------------------------------------------------

// Names of the inline custom properties the last theme message set, so a later message can remove the ones
// it no longer provides instead of leaving a previous theme's value behind.
let applied = new Set<string>();

/** Applies a theme message from the host: the anchor variables and the scheme / contrast attributes (see theme.css). */
export function applyTheme(message: Pick<ThemeMessage, 'colors' | 'scheme' | 'highContrast'>, root: HTMLElement = document.documentElement): void {
  const colors = { ...message.colors, ...derivedTextColors(message.colors, message.highContrast) };

  const next = new Set(Object.keys(colors));
  for (const name of applied) if (!next.has(name)) root.style.removeProperty(name);
  for (const [name, value] of Object.entries(colors)) root.style.setProperty(name, value);
  applied = next;

  root.dataset.scheme = message.scheme;
  root.dataset.contrast = message.highContrast ? 'high' : 'normal';
}
