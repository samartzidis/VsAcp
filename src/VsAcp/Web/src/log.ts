/**
 * Page diagnostics. The host copies the page's console into the VsAcp log (ChatWebView, through the DevTools
 * protocol), always - not only with DevTools enabled - so keep it cheap: lifecycle events with
 * ids and counts, never one line per streamed chunk and never message text (it is the agent's and the user's).
 */
function line(event: string, data?: Record<string, unknown>): string {
  return data ? `${event} ${JSON.stringify(data)}` : event;
}

export const log = {
  debug: (event: string, data?: Record<string, unknown>) => console.debug(line(event, data)),
  info: (event: string, data?: Record<string, unknown>) => console.info(line(event, data)),
  warn: (event: string, data?: Record<string, unknown>) => console.warn(line(event, data)),
  error: (event: string, data?: Record<string, unknown>) => console.error(line(event, data)),
};
