// Shared by the e2e harness and the screenshot scripts. Kept apart from harness.mjs, which registers test hooks.

/** `dev/index.html` (plus `query`) on a started Vite dev server, on whatever port it listens (see vite.dev.config.ts). */
export function devPage(server, query = '') {
  const base = server.resolvedUrls?.local[0];
  if (!base) throw new Error('the dev server reported no local URL');
  return new URL('dev/index.html' + query, base).href;
}
